// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Api;
using Grog.Core.Format;
using Grog.Core.Models;

namespace Grog.Core.Download;

/// <summary>
/// Parallel download worker pool: N files in flight until the queue drains. Resumes partials via HTTP
/// Range with atomic rename on success, verifies size + MD5 when GOG provides a checksum, backs off on
/// 429/503 (Retry-After or exponential), aborts stalled files, and preserves .part files on stop.
/// </summary>
public sealed class DownloadEngine
{
    private readonly GogApiClient _api;
    private readonly HttpClient _http;
    // Ordered, not FIFO: the queue IS the priority. Position decides what runs next, the user can drag
    // rows, and moving a file to the front pre-empts whatever is running.
    private readonly List<DownloadTask> _queue = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private int _nextIndex;
    private int _total;
    // Live worker-pool control: the worker count is tracked so MaxConcurrent can change on a RUNNING engine
    // (raise -> spawn more slots; lower -> surplus workers retire between files), and the run completes when
    // the count reaches zero (queue drained or canceled).
    private int _workerCount;
    private volatile bool _running;
    private TaskCompletionSource? _runComplete;

    public string BackupRoot { get; set; } = "";
    /// <summary>The session's ledger of bytes a running move still has to write (null = none).</summary>
    public Volumes.PendingWrites? PendingWrites { get; init; }
    /// <summary>Optional multi-volume layout. When set, target dir + root are resolved through it
    /// (respecting the routing policy and per-file RootId); otherwise falls back to flat BackupRoot.</summary>
    public Volumes.IBackupLayout? Layout { get; set; }
    /// <summary>Concurrent workers. Default 3 -- a meaningful speedup while staying polite to the CDN.
    /// Settable live via <see cref="SetMaxConcurrent"/> while a download is running.</summary>
    public int MaxConcurrent { get; set; } = 3;
    public long BytesPerSecondLimit { get; set; } = 0;
    /// <summary>Abort a file if no bytes arrive for this long; it resumes on the next run.</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Max 429/503 retries per file before giving up (resumes next run).</summary>
    public int MaxBackoffRetries { get; set; } = 5;

    /// <summary>The per-account session engine (multi-account step 4). When set, each file resolves and
    /// downloads through its FIRST AUTHENTICATED OWNER's session (Auth.AccountSessions picker) instead of
    /// the single constructor api. A file whose owners are ALL signed out is SKIPPED with a log line naming
    /// them (task.SkipReason, DownloadTaskState.Skipped) -- never a failure, never a retry strike, never
    /// Unavailable, because GOG refused nothing. A 401 mid-run marks that ONE account failed and the file
    /// retries through its next owner before giving up. Null = legacy single-session behavior, unchanged.</summary>
    public Auth.AccountSessions? Sessions { get; set; }

    public event Action<DownloadTask>? TaskChanged;
    /// <summary>Raised (on a worker thread) when a superseded build is archived under Old Versions/ with
    /// KeepOldVersions on. Carries a ready-made <see cref="GameFile"/> (IsOldVersion=true) for the
    /// listener to record in the game's <see cref="Models.LibraryItem.OldVersionFiles"/> and persist.</summary>
    public event Action<Models.GameFile>? OldVersionArchived;
    public Action<string>? DebugTrace { get; set; }
    /// <summary>(09-19) What happened to a PARTIAL when its file started again: resumed at N, or restarted from zero
    /// and why. The user log had no line for either, so "my half-downloaded file went back to 0%" could not be
    /// confirmed or ruled out from it (owner, walk pass 4). One line per file that had a .part; none otherwise.</summary>
    public Action<string>? PartialNotice { get; set; }
    private void NotePartial(DownloadTask t, string what)
        => PartialNotice?.Invoke($"{t.GameTitle} - {t.File.ResolvedFileName ?? t.File.Name}: {what}");

    /// <summary>Per-device write timing; when a device reads slow, at most ONE in-flight file targets it while
    /// other devices keep the full worker pool. Shared across runs by the host so the lesson survives a
    /// Pause/Resume. Null disables the gate.</summary>
    public DeviceWriteMonitor? DeviceMonitor { get; set; }
    public IEnumerable<DownloadTask> Snapshot { get { lock (_gate) return _queue.ToArray(); } }
    public int QueuedCount { get { lock (_gate) return _queue.Count(t => t.State is DownloadTaskState.Pending or DownloadTaskState.Active); } }

    /// <summary>Raised when the order changes, so the UI can redraw without polling.</summary>
    public event Action? QueueChanged;

    public DownloadEngine(GogApiClient api, HttpClient http)
    {
        _api = api;
        _http = http;
    }

    public void Enqueue(DownloadTask task)
    {
        task.State = DownloadTaskState.Pending;
        lock (_gate) { _queue.Add(task); _total = _queue.Count; }
        EnsureWorkers();   // a running engine whose surplus workers retired gets a slot for the new file (no-op before Run)
        TaskChanged?.Invoke(task);
        QueueChanged?.Invoke();
    }

    /// <summary>Enqueues many files as ONE event: all added under a single lock, QueueChanged fired exactly
    /// once, so the UI rebuilds the queue once instead of N times (O(N^2)) on a big backup.</summary>
    public void EnqueueRange(IEnumerable<DownloadTask> tasks)
    {
        lock (_gate)
        {
            // One live task per file. Two whole-queue re-plans (the run's mid-run pass on a pool thread, a sort or
            // Storage pass on the UI thread) each snapshot "known" before either enqueues, so both handed over the
            // same newly fitting file: two workers on one .part, the second failing on the share lock (QA 09-19).
            var live = new HashSet<(long, string)>(_queue
                .Where(t => t.State is DownloadTaskState.Pending or DownloadTaskState.Active)
                .Select(t => (t.File.GameGogId, t.File.FileKey)));
            foreach (var task in tasks)
            {
                if (!live.Add((task.File.GameGogId, task.File.FileKey))) continue;
                task.State = DownloadTaskState.Pending; _queue.Add(task);
            }
            _total = _queue.Count;
        }
        // Workers retire when TakeNext runs dry; a whole-queue re-plan admits files into a RUNNING engine, and
        // without this they sat Pending until the next Resume.
        EnsureWorkers();
        QueueChanged?.Invoke();
    }

    /// <summary>Move a queued file to a new position. Landing inside the active window pre-empts the
    /// running task with the lowest priority, which returns to Pending keeping its partial file.</summary>
    public void MoveTo(DownloadTask task, int newIndex)
    {
        var bumped = new List<DownloadTask>();
        lock (_gate)
        {
            var live = _queue.Where(t => t.State is DownloadTaskState.Pending or DownloadTaskState.Active).ToList();
            if (!live.Contains(task)) return;
            newIndex = Math.Clamp(newIndex, 0, live.Count - 1);
            live.Remove(task);
            live.Insert(newIndex, task);

            // Rebuild preserving finished tasks' relative order at the end.
            var done = _queue.Where(t => t.State is not (DownloadTaskState.Pending or DownloadTaskState.Active)).ToList();
            _queue.Clear(); _queue.AddRange(live); _queue.AddRange(done);

            // Anything now beyond the active window must stop running and go back to waiting - ALL of it.
            bumped.AddRange(BeyondWindow(live));
        }
        foreach (var b in bumped)
        {
            b.PreemptRequested = true;
            SafeCancel(b);
        }
        QueueChanged?.Invoke();
    }

    /// <summary>Reorder the whole live queue to match <paramref name="order"/> ((GogId, FileKey) keys,
    /// most-important first) -- the bulk equivalent of <see cref="MoveTo"/>, used by the sortable queue.
    /// Finished tasks keep their relative order at the end. If the reorder pushes an actively-downloading
    /// file out of the concurrency window, that file is pre-empted (paused, partial kept) so a higher-sorted
    /// file can start immediately -- same mechanism as a drag-to-top.</summary>
    public void Reorder(IReadOnlyList<(long GogId, string FileKey)> order)
    {
        var bumped = new List<DownloadTask>();
        lock (_gate)
        {
            var live = _queue.Where(t => t.State is DownloadTaskState.Pending or DownloadTaskState.Active).ToList();
            if (live.Count < 2) return;
            var rank = new Dictionary<(long, string), int>();
            for (int i = 0; i < order.Count; i++) rank[(order[i].GogId, order[i].FileKey)] = i;
            int Rank(DownloadTask t) => rank.TryGetValue((t.File.GameGogId, t.File.FileKey), out var r) ? r : int.MaxValue;
            var sorted = live.OrderBy(Rank).ToList();   // OrderBy is stable, so unnamed items keep relative order

            var done = _queue.Where(t => t.State is not (DownloadTaskState.Pending or DownloadTaskState.Active)).ToList();
            _queue.Clear(); _queue.AddRange(sorted); _queue.AddRange(done);

            // EVERY active task now beyond the window is preempted, not just the first (owner-hit 09-02 with
            // simultaneous downloads > 1: a re-sort left a second demoted file running to completion).
            bumped.AddRange(BeyondWindow(sorted));
        }
        foreach (var b in bumped)
        {
            b.PreemptRequested = true;
            SafeCancel(b);
        }
        QueueChanged?.Invoke();
    }

    /// <summary>Cancel MANY tasks as ONE event: all removed under a single lock, TaskChanged raised once for
    /// the batch and QueueChanged exactly once. The counterpart to <see cref="EnqueueRange"/>.</summary>
    /// <remarks>Cancelling in a loop is O(N * queue): every call raises TaskChanged, which runs a full
    /// RunSettler pass over the whole snapshot, and QueueChanged, which rebuilds the whole queue pane. At a
    /// hundred-odd files against a thousand-file queue that is a multi-minute freeze indistinguishable from a
    /// hang (owner-hit 09-10, twice, on a queue re-sort). Any bulk cancel MUST come through here.</remarks>
    public int CancelRange(IEnumerable<DownloadTask> tasks)
    {
        var list = tasks as IList<DownloadTask> ?? tasks.ToList();
        if (list.Count == 0) return 0;
        var actives = new List<DownloadTask>();
        int n = 0;
        lock (_gate)
        {
            foreach (var task in list)
            {
                // Decide INSIDE the lock: a worker can flip Pending -> Active under us (same rule as Cancel).
                bool wasActive = task.State == DownloadTaskState.Active;
                if (wasActive) { actives.Add(task); n++; continue; }
                if (task.State != DownloadTaskState.Pending) continue;
                task.State = DownloadTaskState.Canceled;
                if (_queue.Remove(task)) n++;
            }
            _total = _queue.Count;
        }
        foreach (var a in actives) SafeCancel(a);
        // One settle pass and one rebuild for the whole batch, not one per file.
        if (n > 0) { TaskChanged?.Invoke(list[0]); QueueChanged?.Invoke(); }
        return n;
    }

    /// <summary>Cancel one file. If it's running it stops now; either way it leaves the queue. The partial
    /// stays on disk, so re-queuing later resumes rather than restarts.</summary>
    /// <summary>Cancel the task for one file identity, if the engine holds it. Returns whether it did.</summary>
    public bool CancelFile(long gogId, string fileKey)
    {
        var t = Snapshot.FirstOrDefault(x => x.File.GameGogId == gogId && x.File.FileKey == fileKey);
        if (t is null) return false;
        Cancel(t);
        return true;
    }

    /// <summary>Cancel every task belonging to the given games (an item leaving the library takes its
    /// in-flight transfers with it).</summary>
    public int CancelGames(IReadOnlySet<long> gogIds)
    {
        // CancelRange, not a Cancel loop: one settle pass and one rebuild for the batch (measured 09-10,
        // 1100 files: 2200 events and 1.2M snapshot rows walked, versus 2 events and none).
        return CancelRange(Snapshot.Where(x => gogIds.Contains(x.File.GameGogId)).ToList());
    }

    public void Cancel(DownloadTask task)
    {
        bool wasActive;
        lock (_gate)
        {
            // Decide INSIDE the lock: a worker can flip Pending -> Active between an unlocked read and the
            // cancel, and the canceled entry leaves the queue here rather than lingering as a ghost.
            wasActive = task.State == DownloadTaskState.Active;
            if (task.State == DownloadTaskState.Pending) task.State = DownloadTaskState.Canceled;
            if (!wasActive && _queue.Remove(task)) _total = _queue.Count;   // it left the run entirely: the total follows
        }
        if (wasActive) SafeCancel(task);
        TaskChanged?.Invoke(task);
        QueueChanged?.Invoke();
    }

    /// <summary>The Active tasks that sit beyond the worker window in <paramref name="live"/> (Pending +
    /// Active, priority order) and should be pre-empted. With the device gate, an Active file may legitimately
    /// sit behind gated Pending files (they cannot run yet, it can), so a Pending entry ahead of it counts
    /// against the window only when it is takeable right now. Call under the lock.</summary>
    private List<DownloadTask> BeyondWindow(List<DownloadTask> live)
    {
        int slots = Math.Max(1, MaxConcurrent);
        var bumped = new List<DownloadTask>();
        int ahead = 0;   // entries seen so far that hold or would take a worker slot
        // Per slow drive (one file at a time): whether a row above already holds or would take its one slot, and
        // whether a WAITING row ranks above. A Pending row counts even though the drive gates it: what gates it is
        // the Active task below it, and skipping it let a demoted file keep the drive (walk 09-25: a 4 GB part ran
        // for a minute under a Size sort while the 1 MB rows above it waited). Only the FIRST row per slow drive
        // takes a global slot, so a fast drive's download is never bumped for rows that cannot run yet (QA 09-25).
        var slowTaken = new HashSet<string>(StringComparer.Ordinal);
        var slowWaiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in live)
        {
            string root = t.TargetRootId ?? "";
            bool slow = DeviceMonitor is { } mon && mon.IsSlow(root);
            if (t.State == DownloadTaskState.Active)
            {
                // A file past its transfer (writing to disk, verifying, saving) has every byte: it finishes, never
                // bumped (owner 09-25). A second Active on a slow drive is bumped only when a waiting row on that
                // drive ranks above it, so raising the limit or an unrelated drag stops nothing.
                bool finishing = t.FinishingPhase is not null;
                if (!finishing && (ahead >= slots || (slow && slowWaiting.Contains(root)))) { bumped.Add(t); continue; }
                ahead++;   // every running file holds a worker, first on its drive or not
                if (slow) slowTaken.Add(root);
            }
            else if (!slow) ahead++;
            else { slowWaiting.Add(root); if (slowTaken.Add(root)) ahead++; }
        }
        return bumped;
    }

    private bool IsGated(DownloadTask x)
        => DeviceMonitor is { } mon && mon.IsSlow(x.TargetRootId ?? "")
           && _queue.Any(o => o.State == DownloadTaskState.Active && (o.TargetRootId ?? "") == (x.TargetRootId ?? ""));

    /// <summary>(09-19) Finish what is transferring, start nothing else: workers retire as their files land and the
    /// run ends with the rest still Pending, exactly as a pause leaves them but with no partials made. A move that
    /// was asked for mid-backup sets it, so the move never waits for a 500 GB queue and nothing in flight is cut.</summary>
    public bool StartNothingNew { get; set; }

    /// <summary>(09-25, owner) "Finish current files" from a PAUSE: only these files may start (the ones already in
    /// progress). When none of them is left to start, the engine turns into a plain drain (StartNothingNew), so the
    /// run ends exactly as a running "Finish current files" does. Null = no restriction.</summary>
    public IReadOnlySet<(long GogId, string FileKey)>? OnlyStart { get; set; }

    // ---- (09-25, owner) Storage that went away mid-run ----
    // A drive pulled mid-transfer failed every file writing to it AND every file started in the ~2 s before the App's
    // presence watch noticed (walk 09-25 on the Mac: 10 failures, 10 strikes). Now: a file whose root folder is gone is
    // DriveGone (no strike, stays queued), its root is marked offline here, and nothing new starts on it until the host
    // says it is back. Files bound elsewhere keep downloading.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _offlineRoots = new(StringComparer.Ordinal);
    /// <summary>Raised (worker thread) the first time a file finds its storage gone; the root id, "" for the primary.</summary>
    public event Action<string>? RootWentAway;
    /// <summary>Roots this engine will not start files on until <see cref="RootBack"/>.</summary>
    public IReadOnlyCollection<string> OfflineRoots => _offlineRoots.Keys.ToList();
    /// <summary>The host saw the storage come back: files bound to it may start again.</summary>
    /// <returns>True when this engine's run is still live (its waiting files start now); false when it already ended.</returns>
    public bool RootBack(string? rootId)
    {
        _offlineRoots.TryRemove(rootId ?? "", out _);
        Grog.Core.Volumes.DriveWaits.Remove(rootId);
        lock (_retireGate)   // like StartNewAgain: never spawn a worker onto a run whose last worker already retired
        {
            if (!_running || Volatile.Read(ref _workerCount) == 0) return false;
            EnsureWorkers();
            return true;
        }
    }
    private bool Waiting(DownloadTask t)
        => _offlineRoots.ContainsKey(t.TargetRootId ?? "") || Grog.Core.Volumes.DriveWaits.Contains(t.TargetRootId);
    private string? RootPathFor(DownloadTask t)
        => Layout is null ? BackupRoot : string.IsNullOrEmpty(t.TargetRootId) ? BackupRoot : Layout.RootPath(t.TargetRootId);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _seenRoots = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The task's storage went away: its folder was there earlier in this run, is not now, AND its volume
    /// reads offline. A folder that never existed (a new --primary) is created as before; a folder deleted on a
    /// drive that is still plugged in (Windows reads volume-present as online) fails as before (review 09-25: it
    /// would otherwise wait forever for a "reconnect" the watch can never see). File I/O: worker threads only.</summary>
    private bool RootGone(DownloadTask t)
    {
        var p = RootPathFor(t);
        if (string.IsNullOrEmpty(p)) return false;
        try
        {
            if (Directory.Exists(p)) { _seenRoots.TryAdd(p, 0); return false; }
            return _seenRoots.ContainsKey(p) && !Grog.Core.Storage.DriveResolver.Probe(p, fresh: true).Online;
        }
        catch { return false; }
    }
    private void MarkRootGone(DownloadTask t)
    {
        var key = t.TargetRootId ?? "";
        Grog.Core.Volumes.DriveWaits.Add(t.TargetRootId);
        if (_offlineRoots.TryAdd(key, 0)) { try { RootWentAway?.Invoke(key); } catch { /* a host handler must not fail the worker */ } }
    }
    /// <summary>(09-25) Pending files held back by a waiting drive: they did not fail, and did not land.</summary>
    public List<(long GogId, string FileKey)> WaitingPendingKeys()
    {
        lock (_gate) return _queue.Where(x => x.State == DownloadTaskState.Pending && Waiting(x))
                                  .Select(x => (x.File.GameGogId, x.File.FileKey)).ToList();
    }
    /// <summary>Pending files that can start now: not held back by an offline root (the run's tail counts these).</summary>
    public int StartablePendingCount()
    {
        lock (_gate) return _queue.Count(x => x.State == DownloadTaskState.Pending && !Waiting(x));
    }

    private readonly object _retireGate = new();
    /// <summary>(09-22, owner "Keep downloading") Undo <see cref="StartNothingNew"/> while the run is still live: the
    /// queue carries on as if it was never asked. False = the run already ended (the last worker retired); the
    /// host then treats it as a normal end. Under the same gate as the last worker's exit, so a worker is never
    /// spawned onto a run that has already completed.</summary>
    public bool StartNewAgain()
    {
        lock (_retireGate)
        {
            if (!_running || Volatile.Read(ref _workerCount) == 0) return false;
            StartNothingNew = false;
            EnsureWorkers();
            return true;
        }
    }

    // (QA 09-30 B6) Files whose .part is being carried between drives right now: a worker must not open the same
    // .part for writing while the carry is still copying into it. Held keys are skipped by TakeNext and taken on the
    // next pass once the hold is released.
    private readonly HashSet<(long, string)> _held = new();
    public void HoldFiles(IEnumerable<(long GogId, string FileKey)> keys) { lock (_gate) foreach (var k in keys) _held.Add(k); }
    public void ReleaseFiles(IEnumerable<(long GogId, string FileKey)> keys) { lock (_gate) foreach (var k in keys) _held.Remove(k); }

    internal DownloadTask? TakeNextForTest() => TakeNext(out _);
    internal DownloadTask? TakeNextForTest(out bool gatedPending) => TakeNext(out gatedPending);
    private DownloadTask? TakeNext(out bool gatedPending)
    {
        gatedPending = false;
        if (StartNothingNew) return null;
        lock (_gate)
        {
            DownloadTask? t = null;
            var only = OnlyStart;
            bool anyAllowed = false;
            foreach (var x in _queue)
            {
                if (x.State != DownloadTaskState.Pending) continue;
                if (only is not null && !only.Contains((x.File.GameGogId, x.File.FileKey))) continue;
                if (Waiting(x)) continue;   // its storage is gone: it waits, it does not fail
                if (_held.Count > 0 && _held.Contains((x.File.GameGogId, x.File.FileKey))) { gatedPending = true; continue; }   // its .part is being carried: the worker waits, it does not retire
                anyAllowed = true;
                // A slow device takes one file at a time: skip a Pending file whose device already has one in
                // flight and let the worker take the next file bound elsewhere (or wait, if there is none).
                if (IsGated(x)) { gatedPending = true; continue; }
                t = x; break;
            }
            if (t is not null) { t.State = DownloadTaskState.Active; t.FinishingPhase = null; }   // under the gate: a reorder never reads a stale phase
            else if (only is not null && !anyAllowed) StartNothingNew = true;   // every file in progress has started: now a plain drain
            return t;
        }
    }

    /// <summary>Completes once the run's last worker has retired, which is after every file stream is closed
    /// and its data handed to the OS. Already complete when no run is live. A quit waits on this so a slow drive
    /// finishes its writes with the window still up.</summary>
    public Task WhenStopped => _running ? (_runComplete?.Task ?? Task.CompletedTask) : Task.CompletedTask;

    public Task RunToCompletionAsync(CancellationToken external = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        _total = _queue.Count;
        _running = true;
        _runComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EnsureWorkers();
        return _runComplete.Task;
    }

    /// <summary>Spin up worker loops until there are MaxConcurrent of them. Called at run start and whenever
    /// MaxConcurrent is raised, so new download slots open immediately -- no pause/resume.</summary>
    private void EnsureWorkers()
    {
        if (!_running || _cts is null) return;
        var ct = _cts.Token;
        while (true)
        {
            int cur = Volatile.Read(ref _workerCount);
            if (cur >= Math.Max(1, MaxConcurrent)) return;
            if (Interlocked.CompareExchange(ref _workerCount, cur + 1, cur) != cur) continue;   // lost the race; re-read
            _ = Task.Run(() => WorkerLoopAsync(ct));
        }
    }

    /// <summary>Change the number of concurrent downloads on a RUNNING engine. Raising it opens new slots at
    /// once (while files remain to fill them); lowering it lets surplus workers finish their current file and
    /// retire. A live push from the setting -- no pause/resume required.</summary>
    /// <param name="preempt">True (a deliberate setting change): files running outside the new window are
    /// preempted at once - position is priority. False (the Auto advisor's per-completion re-pick): surplus
    /// workers simply retire as their files finish, so a pick oscillating 4/3/4 on a mixed queue never
    /// churns half-fetched files through pause and resume.</param>
    public void SetMaxConcurrent(int n, bool preempt = true)
    {
        MaxConcurrent = Math.Max(1, n);
        EnsureWorkers();
        if (!preempt) return;
        var bumped = new List<DownloadTask>();
        lock (_gate)
        {
            var live = _queue.Where(t => t.State is DownloadTaskState.Pending or DownloadTaskState.Active).ToList();
            bumped.AddRange(BeyondWindow(live));
        }
        foreach (var b in bumped) { b.PreemptRequested = true; SafeCancel(b); }
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>Cancel a task's per-file token from ANY thread. The worker owns that CTS and disposes it the
    /// instant its file finishes, so a UI-thread caller (drag-to-reorder, per-file cancel) can capture a
    /// reference that is nulled/disposed a microsecond later -- swallow that benign race instead of crashing.</summary>
    private static void SafeCancel(DownloadTask? task)
    {
        var cts = task?.Cancellation;
        if (cts is null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* worker already finished this file; nothing to cancel */ }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
      try
      {
        while (!ct.IsCancellationRequested)
        {
            // Live scale-down: a surplus worker (MaxConcurrent was lowered) retires between files.
            if (Volatile.Read(ref _workerCount) > Math.Max(1, MaxConcurrent)) return;
            var task = TakeNext(out bool gated);
            if (task is null)
            {
                if (!gated) return;
                // Every remaining file is bound to a device that is busy and slow: wait for it rather than retire,
                // or the run would end with files still Pending.
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return; }
                continue;
            }

            task.Index = Interlocked.Increment(ref _nextIndex);
            // Per-task token, linked to the job's: canceling one file (or pre-empting it for a
            // higher-priority one) must not stop the others.
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct);
            task.Cancellation = link;
            try
            {
                TaskChanged?.Invoke(task);
                QueueChanged?.Invoke();
                bool skipped = await ExecuteAsync(task, link.Token);
                task.State = skipped ? DownloadTaskState.Skipped : DownloadTaskState.Completed;
            }
            // Only OUR cancel (the run's token, a pre-empt or a per-file cancel, all of which cancel the linked token)
            // is Canceled. A resolve or probe timeout inside the API client also surfaces as
            // OperationCanceledException, and that is a failure like any other network error.
            catch (OperationCanceledException) when (link.Token.IsCancellationRequested || task.PreemptRequested)
            {
                // Pre-empted: something more important jumped the queue. Go back to waiting; the partial
                // in .grog-tmp means resuming costs nothing.
                if (task.PreemptRequested && !ct.IsCancellationRequested)
                {
                    task.PreemptRequested = false;
                    task.State = DownloadTaskState.Pending;
                    task.BytesPerSecond = 0;
                }
                else task.State = DownloadTaskState.Canceled;
            }
            catch (Grog.Core.Api.GogUnavailableException uex)
            {
                // GOG refused to serve this file to the account. Still "Failed" as a transfer outcome, but
                // flagged so settling records it as Unavailable rather than burning a retry strike and
                // eventually condemning a never-fetched file as Corrupt.
                // Reason BEFORE state: the settler scans every task on any worker's TaskChanged, so a sibling's
                // event between the two writes settled this as a plain failure and struck it (flaky
                // A_refusal_settles_as_Unavailable, 09-06).
                task.Error = uex.Reason;
                task.UnavailableReason = uex.Reason;
                task.State = DownloadTaskState.Failed;
            }
            catch (Exception ex)
            {
                DebugTrace?.Invoke($"FAIL {task.File.FileKey}: {ex}");
                task.FailureKind = DownloadFailure.Of(ex);
                // (09-25) Whatever the exception says (I/O error, access denied, path not found), a file whose storage
                // folder is gone lost its DRIVE, not its bytes: no strike, it waits for the drive.
                bool gone = ex is DriveGoneException || RootGone(task);
                // (walk 09-25, Mac) A force-detach fails the open .part with "Input/output error" BEFORE the mount point
                // is gone: checked at once, the root still read present and 3 files took a strike. An I/O-type error on a
                // file under a root gets up to 2 s to see whether its drive is going away.
                // Only an unclassified I/O error: disk-full and network failures say what they are and never wait.
                if (!gone && ex is IOException or UnauthorizedAccessException && task.FailureKind == DownloadFailureKind.Other
                    && !string.IsNullOrEmpty(RootPathFor(task)))
                    for (int i = 0; i < 8 && !gone; i++) { await Task.Delay(250, CancellationToken.None); gone = RootGone(task); }
                if (gone)
                {
                    task.FailureKind = DownloadFailureKind.DriveGone;
                    task.DriveGone = true;
                    MarkRootGone(task);
                }
                task.DiskFull = task.FailureKind == DownloadFailureKind.DiskFull;   // before State, like Error: the settler reads it the moment it sees Failed
                task.Error = ex.Message;   // before State: the settler journals Error the moment it sees Failed
                task.State = DownloadTaskState.Failed;
            }
            finally
            {
                // Core's fact, like HasPartial: the bytes a stopped transfer left in its .part. Only the App
                // recorded PartialBytes (per tick), so a CLI or scheduled run interrupted at 190 of 200 MB
                // re-priced the file at its full size on the next plan (QA 09-18).
                // The .part on disk is the fact; with no file at PartPath (failed before the first write) the record is
                // left alone rather than stamped with a counter that names bytes nobody has (QA 09-30 A3).
                if (task.State is not (DownloadTaskState.Completed or DownloadTaskState.Skipped) && task.File.HasPartial && task.BytesReceived > 0
                    && PartLength(task) is { } partLen)
                    task.File.PartialBytes = partLen;
                task.Cancellation = null;
                task.BytesPerSecond = 0;
                TaskChanged?.Invoke(task);
                QueueChanged?.Invoke();
            }
        }
      }
      finally
      {
        // Last worker out ends the run (queue drained, canceled, or all surplus workers retired to zero --
        // which can't happen while MaxConcurrent >= 1, so zero only means "nothing left to do").
        lock (_retireGate)
            if (Interlocked.Decrement(ref _workerCount) == 0)
            {
                _running = false;
                _runComplete?.TrySetResult();
            }
      }
    }

    public int TotalCount => _total;

    /// <summary>Run one task: pick the serving session (owner picker when <see cref="Sessions"/> is set,
    /// else the constructor api) and download with backoff. Returns true when the task was SKIPPED because
    /// no owner could serve it -- the worker records Skipped, not Completed. On AuthExpired the failed
    /// account is invalidated for the run and the file falls through to its next authenticated owner.</summary>
    private async Task<bool> ExecuteAsync(DownloadTask task, CancellationToken ct)
    {
        if (Sessions is null)
        {
            await DownloadWithBackoffAsync(task, _api, ct);
            return false;
        }
        while (true)
        {
            var owner = Sessions.FirstAuthenticatedOwner(task.File);
            if (owner is null)
            {
                task.SkipReason = $"Signed out: {Sessions.OwnerNames(task.File)}";
                DebugTrace?.Invoke($"skip: {task.GameTitle} / {task.File.Name} -- {task.SkipReason}");
                return true;
            }
            try
            {
                task.DownloadedVia = owner;   // the log names the session that actually fetched
                await DownloadWithBackoffAsync(task, Sessions.ApiFor(owner), ct);
                Sessions.MarkAuthOk(owner);
                return false;
            }
            catch (Auth.AuthExpiredException)
            {
                // ONE account's session lapsed mid-run; stop offering it and let the next owner serve.
                Sessions.MarkAuthFailed(owner);
            }
        }
    }

    private async Task DownloadWithBackoffAsync(DownloadTask task, GogApiClient api, CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                await DownloadOneAsync(task, api, ct);
                return;
            }
            catch (OperationCanceledException oce) when (!ct.IsCancellationRequested)
            {
                // Nobody canceled this file: an internal timeout (GOG's link resolve, the sizing probe) fired.
                // Named as such, so it classifies as a network failure and takes the strike/retry path.
                throw new TimeoutException("GOG did not answer the download request in time.", oce);
            }
            catch (BackoffException bex) when (attempt < MaxBackoffRetries)
            {
                attempt++;
                var delay = BackoffPolicy.Delay(attempt, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), bex.RetryAfter);
                // Said out loud (09-19): this wait was silent, so a CDN slowing us down looked like Grog idling.
                PartialNotice?.Invoke($"{task.GameTitle} - {task.File.Name}: GOG's download server asked us to slow down; waiting {delay.TotalSeconds:F0} s (try {attempt} of {MaxBackoffRetries}).");
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>One file, start to settled manifest entry, in five stages: place it, prepare its partial,
    /// adopt an existing copy if there is one, transfer, then verify and settle. Each stage is its own
    /// method below; this is the storyline. Was a single 210-line method until 09-02.</summary>
    private async Task DownloadOneAsync(DownloadTask task, GogApiClient api, CancellationToken ct)
    {
        // (09-25) Its storage is gone: say so before touching the network (a Mac answered "Access denied" for the
        // vanished /Volumes folder, and that read as a failure with a strike).
        if (RootGone(task)) throw new DriveGoneException(RootPathFor(task) ?? task.TargetRootId ?? "primary");
        DebugTrace?.Invoke($"start: {task.GameTitle} / {task.File.Name}  key={task.File.FileKey}");
        // (09-19) "start" = picked up to the GET's headers in hand: GOG's link resolve plus the CDN's first answer.
        // The phase line had no figure for it, so a slow GOG and a slow drive read the same in the log.
        var startClock = System.Diagnostics.Stopwatch.StartNew();
        // Skip the CDN sizing probe (one round-trip per file) when the manifest already carries the
        // advertised size and name -- the real GET's own headers supply the authoritative length below.
        bool skipProbe = task.File.ExpectedSizeBytes is > 0 && !string.IsNullOrWhiteSpace(task.File.Name);
        var resolved = await api.ResolveDownloadAsync(task.File.FileKey, ct, probe: !skipProbe);
        // Advertised size for the pre-GET checks; the probe's answer wins when we have it (it is the
        // CDN's own number), the manifest's advertised size stands in when the probe was skipped.
        long? advertised = resolved.ContentLength ?? (skipProbe ? task.File.ExpectedSizeBytes : null);
        task.BytesTotal = resolved.ContentLength;
        DebugTrace?.Invoke($"resolved len={advertised?.ToString() ?? "?"} name={resolved.FileName} probe={(!skipProbe)}");

        var place = Place(task);
        var partPath = PreparePartial(task, place);
        task.PartPath = partPath;
        // Where the partial's bytes are: the planner discounts the file only there. Only when a .part is really here:
        // a stray that could not be carried (no room) stays on its drive, and repointing the record at this one
        // priced the file at its remainder on a drive holding nothing (QA 09-30 A3).
        if (File.Exists(partPath)) task.File.PartialRootId = place.RootId;

        // The REAL filename comes from the server, never from the display name. With the probe it is
        // already in hand; with the probe skipped it is read off the GET's own headers below. Only a
        // probe that ran and still learned nothing falls back to the label (the pre-1060 behavior).
        string? fileName = string.IsNullOrWhiteSpace(resolved.FileName) ? null : Naming.SafeFileName(resolved.FileName);
        if (fileName is null && !skipProbe) fileName = Naming.SafeFileName(task.File.Name);
        string? finalPath = fileName is null ? null : Path.Combine(place.Dir, fileName);

        if (finalPath is not null && TryAdoptExisting(task, fileName!, finalPath, advertised, place)) return;

        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        // Defensive: a .part that is already >= the full expected size can't be resumed -- a Range
        // request from its length yields HTTP 416 and would loop forever. This also self-heals a
        // .part left behind by an older build that failed verification without cleaning up. Start clean.
        // Only a REAL length may condemn a partial (walk 09-19): with the probe skipped `advertised` is GOG's rounded
        // label, and a paused 1.99 MB partial of a file labelled "1 MB" was thrown away on every resume. The same
        // test would discard a 4.05 GB partial of a part labelled 4.00 GB. With no real length in hand the ranged
        // GET decides: a 416 carries the true length, and the branch below keeps or restarts on that.
        if (existing > 0 && (resolved.ContentLength ?? task.File.WireSizeBytes) is { } known && existing > known)
        {
            NotePartial(task, $"partial of {Format.ByteFormat.Size(existing)} is not smaller than the file ({Format.ByteFormat.Size(known)}); restarted from zero.");
            DeletePartQuietly(task, partPath);
            existing = 0;
        }

        // Fetch the checksum XML IN PARALLEL with the transfer (it used to run after, adding a full
        // round-trip per file). Quiet: a failed checksum fetch downgrades to size-only verification
        // instead of failing a file whose bytes arrived fine.
        Task<string?>? md5Task = resolved.ChecksumXmlUrl is { } xmlUrl ? FetchMd5Quiet(api, xmlUrl, ct) : null;

        var request = new HttpRequestMessage(HttpMethod.Get, resolved.FinalUrl);
        if (existing > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

        var response = await SendWithStallTimeoutAsync(request, ct);
        DebugTrace?.Invoke($"GET status={(int)response.StatusCode} existing={existing}");

        // 416 on a resume: the .part is at or past the REAL end, which the advertised size did not reveal
        // (GOG rounds it; a stale manifest size can be larger than the file - owner-hit 09-04 on a pause /
        // resume of a fully-received .part). Content-Range "bytes */N" carries the truth: a .part of exactly
        // N is complete and goes straight to verify; anything else is bad and the download restarts clean.
        bool partIsComplete = false;
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            long? trueLength = response.Content.Headers.ContentRange?.Length;
            response.Dispose(); request.Dispose();
            if (trueLength is { } n && existing == n)
            {
                partIsComplete = true;
                if (resolved.ContentLength is null) task.BytesTotal = n;
            }
            else
            {
                DeletePartQuietly(task, partPath);
                existing = 0;
                request = new HttpRequestMessage(HttpMethod.Get, resolved.FinalUrl);
                try { response = await SendWithStallTimeoutAsync(request, ct); }
                catch { request.Dispose(); throw; }
                DebugTrace?.Invoke($"GET (restart after 416) status={(int)response.StatusCode}");
            }
        }
        // A 206 must continue exactly where the .part ends. One that starts elsewhere would append at the wrong
        // offset and produce a file of the right length with the wrong bytes: restart from zero instead.
        if (existing > 0 && response.StatusCode == HttpStatusCode.PartialContent
            && response.Content.Headers.ContentRange?.From is { } rangeFrom && rangeFrom != existing)
        {
            NotePartial(task, $"GOG resumed at byte {rangeFrom} instead of {existing}, so the {Format.ByteFormat.Size(existing)} partial was discarded; restarted from zero.");
            DebugTrace?.Invoke($"206 from={rangeFrom} != existing={existing}: restart");
            response.Dispose(); request.Dispose();
            DeletePartQuietly(task, partPath);
            existing = 0;
            request = new HttpRequestMessage(HttpMethod.Get, resolved.FinalUrl);
            try { response = await SendWithStallTimeoutAsync(request, ct); }
            catch { request.Dispose(); throw; }
            DebugTrace?.Invoke($"GET (restart after bad 206) status={(int)response.StatusCode}");
        }
        using var _req = request; using var _resp = response;

        if (!partIsComplete && response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            throw new BackoffException(response.Headers.RetryAfter?.Delta);

        bool resuming = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
        if (existing > 0 && !partIsComplete)
            NotePartial(task, resuming ? $"resumed at {Format.ByteFormat.Size(existing)}."
                                       : $"GOG answered {(int)response.StatusCode} to a resume request, so the {Format.ByteFormat.Size(existing)} partial was discarded; restarted from zero.");
        if (!resuming && !partIsComplete) existing = 0;
        if (!partIsComplete) response.EnsureSuccessStatusCode();

        // A fresh 200 is only trusted when its headers look like the file: a web page (a login or error page served
        // with 200) is refused outright, and with the probe skipped a length far from GOG's label fails the file
        // instead of being accepted as the file. No length at all is judged after the transfer, once the checksum
        // fetch has answered (see VerifyPartialAsync).
        if (!partIsComplete && !resuming)
            CheckFreshBody(task, response, resolved);
        bool sizeless = !partIsComplete && resolved.ContentLength is null && response.Content.Headers.ContentLength is null;

        // Probe skipped: the GET's headers name the file (Content-Disposition, else the URL tail).
        // SECURITY: server-supplied, so sanitized like every other source - a "../" must never escape the root.
        if (fileName is null)
        {
            if (partIsComplete)
            {
                // A 416 answer names nothing: run the probe we skipped so the file lands under its REAL name.
                var named = await api.ResolveDownloadAsync(task.File.FileKey, ct, probe: true);
                fileName = Naming.SafeFileName(string.IsNullOrWhiteSpace(named.FileName) ? task.File.Name : named.FileName);
            }
            else fileName = Naming.SafeFileName(GogApiClient.ExtractFileName(response, resolved.FinalUrl));
            finalPath = Path.Combine(place.Dir, fileName);
            // Already on disk under its real name: nothing to fetch (the headers cost one round-trip).
            // The adopt compares against the REAL total (the .part plus the GET's Content-Length, or the 416's
            // length), never GOG's rounded label: an intact copy whose length differs from the label is adopted.
            long? adoptSize = partIsComplete ? existing
                : response.Content.Headers.ContentLength is { } getLen ? existing + getLen : advertised;
            // PreparePartial made .grog-tmp for this file; an adopt leaves it empty, so tidy here too.
            if (TryAdoptExisting(task, fileName, finalPath, adoptSize, place)) { RemoveTmpDirIfEmpty(Path.GetDirectoryName(partPath)!); return; }
        }
        task.ResolvedFileName = fileName;

        // The GET's own headers carry the authoritative length; with the probe skipped this is the first
        // trustworthy number (the manifest's advertised size can be stale -- see the footer per-row math).
        if (!partIsComplete && resolved.ContentLength is null && response.Content.Headers.ContentLength is { } wireLen)
        {
            task.BytesTotal = existing + wireLen;
            TaskChanged?.Invoke(task);   // the row's total just became real; let the footer see it
        }
        long? expectedTotal = partIsComplete ? existing : resolved.ContentLength ?? task.BytesTotal;   // the CDN's own end wins over a stale probe

        // The REAL size is in hand and nothing has been written yet: the last gate before bytes flow (owner 09-19).
        // The plan worked from GOG's rounded label; this compares the true remainder with the drive's live free
        // space, less what the other transfers on the same volume still have to write. No room: the file is held
        // as won't-fit (no strike, partial kept) instead of dying mid-stream with the drive full.
        if (expectedTotal is { } real && real > 0)
        {
            // One file at a time through the gate: two workers checking at the same instant each counted the OTHER
            // at its rounded label (its real size not yet published), both passed, and together they over-filled
            // the drive by a few hundred KB (fill simulation 09-19). Publishing the real size and checking are
            // one step under this lock, so the later of any two always sees the earlier one's true remainder.
            lock (_roomGate)
            {
                task.File.WireSizeBytes = real;
                task.BytesTotal ??= real;
                if (!partIsComplete) EnsureRoom(task, place, real, existing);
                task.RoomChecked = true;
            }
        }

        double startS = startClock.Elapsed.TotalSeconds;
        var phaseClock = System.Diagnostics.Stopwatch.StartNew();
        string? streamedMd5 = null;   // the digest the copy loop built; a 416-complete .part was never streamed here
        if (partIsComplete) { task.BytesReceived = existing; TaskChanged?.Invoke(task); }
        else streamedMd5 = await StreamToPartialAsync(task, response, partPath, existing, resuming, ct);
        // Per-phase durations, recorded from the worker: "100% and stuck" is answered with numbers, not
        // impressions (owner, 09-04: a 300 MB file took ~10 s from 100% to the next file on a USB stick).
        double transferS = phaseClock.Elapsed.TotalSeconds - task.FlushSeconds; phaseClock.Restart();

        var actualSize = new FileInfo(partPath).Length;
        if (expectedTotal is { } expected && actualSize != expected)
        {
            // The completed .part is the wrong size -- it's not a resumable partial, it's bad. Delete it
            // so the next attempt (auto-retry or manual) starts a clean download instead of trying to
            // resume from a full-but-wrong file and looping on HTTP 416.
            DeletePartQuietly(task, partPath);
            throw new IOException($"Size mismatch: expected {expected}, got {actualSize}");
        }

        bool verified = await VerifyPartialAsync(task, partPath, fileName, md5Task, sizeless, streamedMd5, ct);
        double verifyS = phaseClock.Elapsed.TotalSeconds; phaseClock.Restart();
        // The device's cost per file is the flush PLUS the read-back for the hash: on the 09-04 stick the
        // verify was the long tail (15-18 s), the flush often under a second.
        DeviceMonitor?.Record(task.TargetRootId ?? "", task.FlushSeconds + verifyS);

        task.FinishingPhase = "Saving"; TaskChanged?.Invoke(task);
        // One step, not delete-then-move: a move that threw (antivirus lock, long path) had already destroyed the
        // old good copy, and the complete .part was then discarded as "not smaller than the file" (QA 09-19).
        // A same-name update lands on the old build: with KeepOldVersions on it is held aside under a sibling
        // name, put back if the move fails, and archived only once the new build is under the name.
        var held = HoldOldBuild(task, finalPath!);
        try
        {
            BeforeFinalMove?.Invoke(partPath, finalPath!);
            File.Move(partPath, finalPath!, overwrite: true);
        }
        catch
        {
            if (held is not null) try { File.Move(held, finalPath!, overwrite: true); } catch { /* the old build stays under the sibling name */ }
            throw;
        }
        if (held is not null) ArchiveHeldBuild(task, held, finalPath!, place.RootBase, place.RootId);
        // A stray partial of this file left on ANOTHER drive (no room to carry it over) has no use now: the file is
        // complete. Left, it held that drive's room until a later sweep (fill simulation 09-19).
        if (Layout is { } lay2)
            foreach (var other in lay2.OnlineRootPaths)
                try
                {
                    if (string.Equals(other, place.RootBase, StringComparison.OrdinalIgnoreCase)) continue;
                    var stray = PartialPathFor(other, task.File);
                    if (File.Exists(stray)) { File.Delete(stray); RemoveTmpDirIfEmpty(Path.GetDirectoryName(stray)!); }
                }
                catch { /* the sweep takes it later */ }
        // Temp means temp: the folder goes with its last file. Every finisher tries; the folder only goes when
        // it is empty, so a sibling with an open .part keeps it. The one hole is a sibling between its
        // CreateDirectory and its .part open (owner-hit 09-05, 16:0x): closed by that sibling retrying the open
        // once (StreamToPartialAsync). The 1116 "no other Active task" guard read the queue list unlocked and
        // threw "collection was modified" under a Try Again or re-sort mid-save; and two files finishing together
        // each saw the other Active, so neither removed the folder (review 09-06).
        RemoveTmpDirIfEmpty(Path.GetDirectoryName(partPath)!);
        UpdateManifestEntry(task, finalPath!, actualSize, verified, place.RootBase, place.RootId);
        task.PhaseSummary = $"start {startS:F1} s · transfer {transferS:F1} s · flush {task.FlushSeconds:F1} s · verify {verifyS:F1} s · save {phaseClock.Elapsed.TotalSeconds:F1} s";
    }

    /// <summary>Where a file lands: its folder, the root that folder is under (LocalRelativePath is computed
    /// against it) and that root's id. Honors a front-run placement plan, else the layout's routing.</summary>
    private readonly record struct Placement(string Dir, string RootBase, string? RootId);

    private Placement Place(DownloadTask task)
    {
        // The persisted slug, so downloads land in the folder reorg/verify/import plan against; a title
        // change on GOG's side must not fork a second folder.
        var slug = string.IsNullOrEmpty(task.GameSlug) ? Naming.Slug(task.GameTitle) : task.GameSlug!;
        string dir;
        string rootBaseForRel;   // the root path we compute LocalRelativePath against
        string? rootId = null;
        if (Layout is not null)
        {
            string rid;
            string? target;
            // Honor the front-run placement plan (overflow/spill) when set + online; else routing policy.
            if (!string.IsNullOrEmpty(task.TargetRootId) && Layout.IsOnline(task.TargetRootId))
            {
                rid = task.TargetRootId!;
                var rootPath = Layout.RootPath(rid);
                target = rootPath is null ? null : Path.Combine(rootPath, Layout.SubPathFor(slug, task.File.Kind, task.File, task.File.GameGogId, rid));
            }
            else
            {
                target = Layout.ResolveTargetDir(task.File.GameGogId, slug, task.File.Kind, out rid, task.File);
            }
            if (target is null)
                throw new IOException($"Target root for {task.GameTitle} is offline; skipping.");
            dir = target;
            rootId = rid;
            rootBaseForRel = Layout.RootPath(rid) ?? BackupRoot;
        }
        else
        {
            var subDir = task.File.Kind == FileKind.Extra ? Path.Combine(slug, "extras") : slug;
            dir = Path.Combine(BackupRoot, subDir);
            rootBaseForRel = BackupRoot;
        }
        Directory.CreateDirectory(dir);
        return new Placement(dir, rootBaseForRel, rootId);
    }

    /// <summary>The identity-keyed .part path for this file, with a one-time adoption of the display-name
    /// keyed partial the 1060-1083 builds wrote.</summary>
    private string PreparePartial(DownloadTask task, Placement place)
    {
        // In-progress downloads live in a per-device temp folder on the SAME root as the final file, so
        // finalizing is always a same-device rename and reorg/orphan scans never see half-written files.
        // The .part is keyed by the file's IDENTITY (root + game + FileKey), never by a display name:
        // GameFile.Name is GOG's label ("Setup (Part 1 of 3)", "manual (33 pages)") and two files of one
        // game can share it - keying by name collided their partials under concurrent workers (09-02).
        var tmpDir = Path.Combine(place.RootBase, ".grog-tmp");
        // Under the same lock as the remove-if-empty (09-20): this create was the one left outside it, and a worker
        // deleting the empty folder at the same instant failed the transfer with "'.grog-tmp' already exists"
        // (fill simulation, seed 3; about 1 full-suite run in 10).
        lock (_tmpDirGate) Directory.CreateDirectory(tmpDir);
        var partPath = Path.Combine(tmpDir, StablePartName(task.File));
        // Builds up to 1211 keyed the name by the root's path too: adopt that one once.
        var rootKeyed = Path.Combine(tmpDir, RootKeyedPartName(place.RootBase, task.File));
        if (!File.Exists(partPath) && File.Exists(rootKeyed))
            try { File.Move(rootKeyed, partPath); } catch { /* start clean if the adopt fails */ }
        // A partial written by the 1060-1083 builds was keyed by the display-name path: adopt it once.
        var legacyPart = Path.Combine(tmpDir, StableTempName(Path.Combine(place.Dir, Naming.SafeFileName(task.File.Name))));
        if (!File.Exists(partPath) && File.Exists(legacyPart))
            try { File.Move(legacyPart, partPath); } catch { /* start clean if the adopt fails */ }
        // A Pause/Resume re-plan can route the file to ANOTHER device than the one its partial sits on. Bring
        // the partial along (cross-volume move) instead of leaving an orphan .part that no count owns and
        // re-fetching bytes already paid for (owner-caught 09-04).
        if (!File.Exists(partPath) && Layout is { } lay)
            foreach (var otherRoot in lay.OnlineRootPaths)
            {
                if (string.Equals(otherRoot, place.RootBase, StringComparison.OrdinalIgnoreCase)) continue;
                var stray = PartialPathFor(otherRoot, task.File);
                if (!File.Exists(stray)) continue;
                // Adopt, else leave it: the stray is paid-for bytes, and deleting it on a failed move (no room on
                // the new device for a partial the plan priced at its remainder) lost them (QA 09-18). An orphan
                // .part is swept by SweepOrphanPartials once nothing owns it.
                // Across volumes the move is a COPY of the whole partial onto the new drive: only when it has the
                // room for it, beside what its running transfers still need (fill simulation 09-19: a stray carried
                // onto a nearly full second drive over-filled it). No room: it stays, and this file starts clean here.
                try
                {
                    bool sameVolume = string.Equals(VolumeOf(otherRoot), VolumeOf(place.RootBase), StringComparison.OrdinalIgnoreCase);
                    if (!sameVolume)
                        lock (_roomGate)
                        {
                            long othersHere = OthersStillToWrite(task, place.RootBase);   // before the free read, as in EnsureRoom
                            if (FreeBytesProbe(place.RootBase) is { } freeHere && freeHere - FloorBytes - othersHere < ChargeOn(place.RootBase, new FileInfo(stray).Length)) break;
                            File.Move(stray, partPath);   // inside the gate: no sibling's check can land between the test and the copy
                            break;
                        }
                    File.Move(stray, partPath);
                }
                catch { /* left in place */ }
                break;
            }
        return partPath;
    }

    /// <summary>A complete copy already on disk under the real name (size matches what GOG advertises) is
    /// recorded as Present without a transfer. Size-only: it was never hashed here, so not Verified.</summary>
    private bool TryAdoptExisting(DownloadTask task, string fileName, string finalPath, long? advertised, Placement place)
    {
        if (task.File.RefetchRequested) return false;   // a fix / re-download: the copy on disk is what is being replaced
        if (!File.Exists(finalPath) || advertised is not { } exp || new FileInfo(finalPath).Length != exp) return false;
        task.ResolvedFileName = fileName;
        task.BytesReceived = exp;
        UpdateManifestEntry(task, finalPath, exp, verified: false, place.RootBase, place.RootId);
        return true;
    }

    /// <summary>How far a fresh GET's Content-Length may sit from GOG's rounded label before the body is refused:
    /// one display unit of the label (a "1 MB" label covers a 1.99 MB file) plus the planner's LabelSlack.</summary>
    internal static long LabelTolerance(long label)
    {
        long unit = label >= 1L << 30 ? 1L << 30 : label >= 1L << 20 ? 1L << 20 : 1L << 10;
        return unit + Volumes.PlacementPlanner.LabelSlack(label);
    }

    /// <summary>A fresh 200 is only the file when its headers say so: a text/html body is a web page, not the
    /// file; with the probe skipped, a length far outside the label's tolerance is refused rather than accepted
    /// as the file.</summary>
    private static void CheckFreshBody(DownloadTask task, HttpResponseMessage response, ResolvedDownload resolved)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            throw new IOException("GOG returned a web page instead of the file.");
        if (resolved.ContentLength is not null) return;   // the probe already vouched for the length
        if (response.Content.Headers.ContentLength is { } len && task.File.ExpectedSizeBytes is { } label
            && RefusesBodyLength(task.File.Kind, len, label))
            throw new IOException($"GOG sent {Format.ByteFormat.Size(len)} for a file it lists as {Format.ByteFormat.Size(label)}; not accepted as the file.");
    }

    /// <summary>GOG's listed size is a display figure and can be off by several percent (a 941 MB label for an 897 MB
    /// file, measured); the checksum decides those. Only a body of a different order (an error page, a stub) is refused.
    /// An extra's label is not even that: GOG lists many manuals and wallpapers at a flat 10.00 MB (a 111.90 KB manual,
    /// measured 09-30), so for extras only the media type decides.</summary>
    internal static bool RefusesBodyLength(FileKind kind, long len, long label)
        => kind != FileKind.Extra && label > 0 && !PlausibleBodyLength(len, label);

    /// <summary>A fresh body is the file when it is within a display unit of the listed size, or within half to double
    /// of it; the checksum catches the rest.</summary>
    internal static bool PlausibleBodyLength(long len, long label)
        => Math.Abs(len - label) <= 1L << 20 || (len * 2 >= label && len <= label * 2);

    /// <summary>The transfer loop: response body to the .part (append when resuming), with the live rate
    /// limit, a stall timer per read, and a speed sample every half second for the rows. Returns the lowercase
    /// hex MD5 of the whole .part, built as the bytes pass so the verify never re-reads the landed file.</summary>
    private async Task<string> StreamToPartialAsync(DownloadTask task, HttpResponseMessage response, string partPath,
                                                    long existing, bool resuming, CancellationToken ct)
    {
        var fileMode = resuming ? FileMode.Append : FileMode.Create;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            // A resume hashes the prefix already on disk once, before appending; a restart from zero starts clean.
            if (resuming && existing > 0) await HashPrefixAsync(partPath, existing, hash, buffer, ct);

            // Creating the temp folder and opening the .part inside it is ONE step against the folder's removal (a
            // sibling finishing on this device removes .grog-tmp when it is empty). Unguarded, the removal landed between
            // the two and the transfer failed with "could not find" / "already exists" (owner-hit 09-05; the fill
            // simulation reproduced it 09-19). RemoveTmpDirIfEmpty takes the same lock.
            FileStream outStream;
            lock (_tmpDirGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
                outStream = new FileStream(partPath, fileMode, FileAccess.Write, FileShare.None, WriteBufferBytes);
            }
            await using var _ = outStream;
            task.File.HasPartial = true;   // Core's fact, not the host's: a .part exists for this file from here on
            task.File.PartialRootId = task.TargetRootId;   // and THIS is where it is (the Place-time stamp covers only an adopted stray)
            await using var inStream = await response.Content.ReadAsStreamAsync(ct);
            task.BytesReceived = existing;
            var throttle = new RateLimiter(() => BytesPerSecondLimit);   // live: a mid-transfer settings change applies to this file

            // Speed + stall tracking. StallGuard owns the per-read stall timer; a stall throws StallException
            // (not a user cancel) and the .part is preserved for resume.
            var speedWindowStart = DateTime.UtcNow;
            long speedWindowBytes = 0;
            using (var stall = new StallGuard(ct, StallTimeout))
            {
                while (true)
                {
                    int read = await stall.ReadAsync(inStream, buffer);
                    if (read <= 0) break;

                    await outStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    task.BytesReceived += read;
                    speedWindowBytes += read;

                    var elapsed = (DateTime.UtcNow - speedWindowStart).TotalSeconds;
                    if (elapsed >= 0.5)
                    {
                        task.BytesPerSecond = speedWindowBytes / elapsed;
                        speedWindowStart = DateTime.UtcNow;
                        speedWindowBytes = 0;
                        TaskChanged?.Invoke(task);
                    }
                    await throttle.ThrottleAsync(read, ct);
                }
            }
            task.FinishingPhase = "Writing to disk"; task.BytesPerSecond = 0; TaskChanged?.Invoke(task);
            var flushClock = System.Diagnostics.Stopwatch.StartNew();
            await outStream.FlushAsync(ct);
            outStream.Flush(flushToDisk: true);
            task.FlushSeconds = flushClock.Elapsed.TotalSeconds;
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>Feeds the first <paramref name="length"/> bytes already in the .part into the running hash.</summary>
    private static async Task HashPrefixAsync(string partPath, long length, IncrementalHash hash, byte[] buffer, CancellationToken ct)
    {
        await using var prefix = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        long left = length;
        while (left > 0)
        {
            int n = await prefix.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
            if (n <= 0) break;
            hash.AppendData(buffer, 0, n);
            left -= n;
        }
    }

    /// <summary>MD5 the completed .part against GOG's checksum when one arrived. A mismatch discards the
    /// partial (the retry must re-fetch from scratch) and throws; no checksum means size-only (false). A body
    /// the wire never sized (<paramref name="sizeless"/>) with no checksum either has nothing vouching for it:
    /// the .part is discarded and the file fails rather than land unverified.</summary>
    private async Task<bool> VerifyPartialAsync(DownloadTask task, string partPath, string? fileName, Task<string?>? md5Task, bool sizeless, string? streamedMd5, CancellationToken ct)
    {
        var expectedMd5 = md5Task is null ? null : await md5Task;
        if (expectedMd5 is null && sizeless)
        {
            DeletePartQuietly(task, partPath);
            throw new IOException("GOG sent neither a size nor a checksum for this file");
        }
        if (md5Task is null) { DebugTrace?.Invoke($"verify: no checksum url for {fileName} (size-only)"); return false; }
        if (expectedMd5 is null) { DebugTrace?.Invoke($"verify: checksum xml had no file MD5 for {fileName}"); return false; }
        task.FinishingPhase = "Verifying"; TaskChanged?.Invoke(task);
        // The copy loop's digest covers the whole .part; only a .part that arrived complete (416) is read back.
        var actualMd5 = streamedMd5 ?? await Grog.Core.Verify.Hashing.Md5Async(partPath, ct);
        if (!string.Equals(actualMd5, expectedMd5, StringComparison.OrdinalIgnoreCase))
        {
            // Corrupt content -- discard the partial so the retry re-fetches from scratch.
            DeletePartQuietly(task, partPath);
            throw new IOException($"MD5 mismatch for {fileName}");
        }
        task.File.ExpectedMd5 = expectedMd5;
        DebugTrace?.Invoke($"verify: MD5 OK for {fileName} ({expectedMd5})");
        return true;
    }

    /// <summary>Checksum fetch that never throws: it runs concurrently with the transfer, and a failed
    /// metadata call must downgrade to size-only verification, not fail a file whose bytes arrived.</summary>
    private static async Task<string?> FetchMd5Quiet(GogApiClient api, string xmlUrl, CancellationToken ct)
    {
        try { return await api.FetchMd5Async(xmlUrl, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>A fixed floor per volume that no plan and no transfer may spend: directory growth and filesystem
    /// metadata that the per-file cluster rounding does not name. Fixed, so a landing frees nothing the plan did not
    /// already count (the per-file 256 KB slack it replaces was given back on landing and let one more small file in
    /// after every landing; walk 10-01).</summary>
    public const long FloorBytes = 2 * 1024 * 1024;
    /// <summary>The target volume's allocation unit; overridable for tests (0 = bytes cost bytes).</summary>
    internal Func<string, long> ClusterBytesProbe { get; set; } = path => Storage.DriveResolver.ClusterBytes(path);
    private long ChargeOn(string rootBase, long bytes) => Storage.DriveResolver.OnDisk(bytes, ClusterBytesProbe(rootBase));

    /// <summary>The .part stream's write buffer: received bytes that may not be on the disk yet.</summary>
    internal const int WriteBufferBytes = 1 << 20;

    /// <summary>Test seam: the live free bytes of the volume behind a path (null = unknown, the check is skipped).</summary>
    internal Func<string, long?> FreeBytesProbe { get; set; } = path =>
    {
        try { return Storage.DriveResolver.For(path)?.AvailableFreeSpace; } catch { return null; }
    };

    /// <summary>Test seam: the per-file ceiling of the volume behind a path.</summary>
    internal Func<string, long> MaxFileBytesProbe { get; set; } = Storage.DriveResolver.MaxFileBytes;

    private static readonly object _roomGate = new();

    private void EnsureRoom(DownloadTask task, Placement place, long real, long existing)
    {
        long ceiling = MaxFileBytesProbe(place.RootBase);
        if (real > ceiling)
            throw NoRoomException($"{Format.ByteFormat.Size(real)} is over the {Format.ByteFormat.Size(ceiling)} a single file can be on this drive's filesystem");
        // ORDER MATTERS: what the others still have to write is read BEFORE the drive's free space. Bytes they write
        // between the two reads are then counted twice (safe) instead of not at all. The other way round, a free
        // probe that takes seconds on a busy USB stick (4.8 s measured 09-19) let a 45 MB/s sibling write 200 MB
        // that neither figure held (fill simulation 09-19).
        long others = OthersStillToWrite(task, place.RootBase);
        if (FreeBytesProbe(place.RootBase) is not { } probed) return;
        long free = probed - FloorBytes;   // the floor is nobody's to spend
        long need = Math.Max(0, ChargeOn(place.RootBase, real) - existing);
        DebugTrace?.Invoke($"room {task.File.FileKey}: free={free} others={others} need={need} existing={existing} real={real}");
        if (free - others < need)
            throw NoRoomException($"needs {Format.ByteFormat.Size(need)}, and {Format.ByteFormat.Size(Math.Max(0, free - others))} is free once the transfers already running finish");
    }

    /// <summary>What the OTHER active transfers on the same volume still have to write, each with its allocation cost.</summary>
    private long OthersStillToWrite(DownloadTask task, string rootBase)
    {
        string? myVolume = VolumeOf(rootBase);
        long others = PendingWrites?.ForVolume(rootBase) ?? 0L;   // (09-22) a running move's remaining copies count too
        lock (_gate)
            foreach (var t in _queue)
            {
                if (ReferenceEquals(t, task) || t.State != DownloadTaskState.Active) continue;
                var root = Layout?.RootPath(t.TargetRootId ?? "") ?? BackupRoot;
                if (!string.Equals(VolumeOf(root), myVolume, StringComparison.OrdinalIgnoreCase)) continue;
                long total = t.BytesTotal ?? t.File.PlanSizeBytes ?? 0;
                // BytesReceived counts what was HANDED to the file stream; up to one write buffer of it has not reached
                // the disk yet, so the free figure still includes that much (fill simulation 09-19: the drive went
                // over by just under 1 MiB with two transfers running).
                // Before its own room check a transfer's BytesReceived is only its RECORDED partial, which may still sit
                // on another drive (the stray is carried over just before the check): counted as on this disk, a
                // sibling checking in that window passed, and the carried partial then over-filled the drive (fill
                // simulation 09-19). Unchecked = its whole size is still to come.
                // Unchecked, but its recorded partial already sits on THIS volume: those bytes are in the free figure
                // already, so charging the whole size counted them twice (QA 09-30 A9).
                long onDisk = t.RoomChecked ? Math.Max(0, t.BytesReceived - WriteBufferBytes)
                            : t.File.HasPartial && t.File.PartialRootId is { } pr
                              && string.Equals(VolumeOf(Layout?.RootPath(pr) ?? ""), myVolume, StringComparison.OrdinalIgnoreCase)
                              ? t.File.PartialBytes ?? 0 : 0;
                others += Math.Max(0, ChargeOn(root, total) - onDisk);
            }
        return others;
    }

    private static string? VolumeOf(string path)
    {
        try { return Storage.DriveResolver.MountRootOf(path) ?? path; } catch { return path; }
    }

    private static IOException NoRoomException(string why) => new("No room: " + why + ".", unchecked((int)0x80070070));

    /// <summary>ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL on Windows (also what FAT32 answers past 4 GiB), ENOSPC /
    /// EFBIG / EDQUOT on Unix.</summary>
    internal static bool IsDiskFull(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is not IOException) continue;
            int code = e.HResult & 0xFFFF;
            if (e.HResult == unchecked((int)0x80070070) || e.HResult == unchecked((int)0x80070027)) return true;
            if (!OperatingSystem.IsWindows() && (code is 28 or 27 or 122 or 69)) return true;
        }
        return false;
    }

    /// <summary>What the .part really holds. The counter runs ahead of the disk by whatever sat in the stream's
    /// buffer when a write threw (14,679 bytes past FAT32's ceiling, walk 09-19), and the planner prices the file
    /// by this figure.</summary>
    /// <summary>The .part's length on disk, or null when there is no file at PartPath.</summary>
    private static long? PartLength(DownloadTask task)
    {
        try { if (task.PartPath is { } p && File.Exists(p)) return new FileInfo(p).Length; } catch { /* unreadable: unknown */ }
        return null;
    }

    /// <summary>Best-effort delete of a partial download. Used when a completed .part fails size/MD5
    /// verification, so it is never mistaken for a resumable partial on the next attempt.</summary>
    private static void DeletePartQuietly(DownloadTask task, string partPath)
    {
        try { if (File.Exists(partPath)) File.Delete(partPath); } catch { /* best-effort */ }
        // The record follows the disk: a discarded .part left HasPartial/PartialBytes standing, so the next plan
        // priced the file at "what it still needs" over bytes that were gone, marked it Fits on a drive without
        // the room, and the restart died out of space (QA 09-18).
        task.File.HasPartial = false; task.File.PartialBytes = null;
    }

    /// <summary>Deletes the resumable partial for a file, if one is on disk -- an explicit cancel means the
    /// user does not want the file (pausing keeps partials). The temp name is a hash of the final path, so
    /// that path is reproduced the same way the download does. Best-effort: never fails the cancel.</summary>
    public void DeletePartial(Grog.Core.Models.GameFile f, string gameTitle)
    {
        try
        {
            string? finalPath = Layout?.ResolvePath(f);
            string root = BackupRoot;
            if (finalPath is null)
            {
                var name = string.IsNullOrWhiteSpace(f.ResolvedFileName) ? Naming.SafeFileName(f.Name) : f.ResolvedFileName!;
                string rid = "";
                var dir = Layout?.ResolveTargetDir(f.GameGogId, Naming.Slug(gameTitle), f.Kind, out rid, f);
                if (dir is null)
                {
                    var sub = f.Kind == Grog.Core.Models.FileKind.Extra
                        ? Path.Combine(Naming.Slug(gameTitle), "extras") : Naming.Slug(gameTitle);
                    dir = Path.Combine(BackupRoot, sub);
                }
                else if (Layout is { } l2) root = l2.RootPath(rid) ?? BackupRoot;
                finalPath = Path.Combine(dir, name);
            }
            // The .part lives on the same root as the resolved final file: resolve straight from RootId.
            else root = Layout?.RootPath(f.RootId ?? "") ?? BackupRoot;

            // Identity-keyed name (current builds) AND the legacy path-keyed names (pre-09-02 and the
            // 1060-1083 display-name key), so a cancel never strands a partial from an older build.
            foreach (var part in new[]
                     {
                         Path.Combine(root, ".grog-tmp", StablePartName(f)),
                         Path.Combine(root, ".grog-tmp", RootKeyedPartName(root, f)),
                         Path.Combine(root, ".grog-tmp", StableTempName(finalPath)),
                         Path.Combine(root, ".grog-tmp", StableTempName(Path.Combine(Path.GetDirectoryName(finalPath) ?? "", Naming.SafeFileName(f.Name)))),
                     })
                if (File.Exists(part)) File.Delete(part);
        }
        catch { /* best effort: cleanup must never fail a cancel */ }
        // The transfer writes to the task's TARGET root, which a spill makes different from the routed one resolved
        // above: look on every connected root, or a spilled file's .part is orphaned (QA 09-19).
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BackupRoot };
        if (Layout is { } l3) foreach (var r in l3.OnlineRootPaths) all.Add(r);
        foreach (var r in all)
            try { if (!string.IsNullOrEmpty(r)) { var part = PartialPathFor(r, f); if (File.Exists(part)) File.Delete(part); } }
            catch { /* in use or unreadable: left for the sweep */ }
    }

    /// <summary>The GET's header wait under the same stall timeout the body gets. The download client has an
    /// infinite timeout by design (a 10 GB body must not time out), and the body loop times out per read, but a
    /// CDN that never answers the request itself left a file Active forever at the head of the queue
    /// (owner-hit 09-04: a resume stuck at 476 of 486 MB, 0%). Expiry throws a normal failure: strike, retry.</summary>
    private async Task<HttpResponseMessage> SendWithStallTimeoutAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new StallException($"No response from the download server within {StallTimeout.TotalSeconds:F0} s");   // a network failure, not Other (QA 09-30 C9)
        }
    }

    /// <summary>Re-target files that have not started. <paramref name="plan"/> receives the Pending tasks in
    /// queue order plus the in-flight ones, and returns the new root per pending task id; anything not in the
    /// answer keeps its target. Runs under the queue lock so a worker cannot take a task mid-change. Returns
    /// how many targets changed.</summary>
    public int Retarget(Func<IReadOnlyList<DownloadTask>, IReadOnlyList<DownloadTask>, IReadOnlyDictionary<Guid, string?>> plan)
    {
        lock (_gate)
        {
            var pending = _queue.Where(t => t.State == DownloadTaskState.Pending).ToList();
            if (pending.Count == 0) return 0;
            var active = _queue.Where(t => t.State == DownloadTaskState.Active).ToList();
            var answer = plan(pending, active);
            int changed = 0;
            foreach (var t in pending)
                if (answer.TryGetValue(t.Id, out var rid) && !string.Equals(rid, t.TargetRootId, StringComparison.Ordinal))
                { t.TargetRootId = rid; changed++; }
            return changed;
        }
    }

    /// <summary>A .grog-tmp with nothing left in it is removed: it exists only while something is mid-flight
    /// (owner, 09-05: "if it is temp it should not be permanent"). Best effort; a concurrent worker that is
    /// about to create its .part simply recreates the folder (PreparePartial does CreateDirectory).</summary>
    private static readonly object _tmpDirGate = new();
    private static void RemoveTmpDirIfEmpty(string dir)
    {
        // FolderPruner owns the delete (and its one retry on a transient lock); this only serializes it against PreparePartial.
        lock (_tmpDirGate) Volumes.FolderPruner.DeleteIfEmpty(dir);
    }

    /// <summary>Delete every .part under each online root's .grog-tmp. Callers invoke it only when nothing is
    /// queued and no file carries a partial, so there is nothing left that could resume from them.</summary>
    public int SweepOrphanPartials() => SweepOrphanPartials(null);

    /// <summary>Owner-aware sweep: every .part older than ten minutes that none of <paramref name="owners"/> (the
    /// files whose record says HasPartial) accounts for. Null = no owners, everything old goes.</summary>
    public int SweepOrphanPartials(IEnumerable<Grog.Core.Models.GameFile>? owners)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BackupRoot };
        if (Layout is { } l) foreach (var r in l.OnlineRootPaths) roots.Add(r);
        return SweepOrphanPartials(roots, owners);
    }

    /// <inheritdoc cref="SweepOrphanPartials(IEnumerable{Grog.Core.Models.GameFile})"/>
    public static int SweepOrphanPartials(IEnumerable<string> rootPaths, IEnumerable<Grog.Core.Models.GameFile>? owners)
    {
        int n = 0;
        var roots = rootPaths.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in owners ?? Array.Empty<Grog.Core.Models.GameFile>())
        {
            keep.Add(StablePartName(o));
            foreach (var r in roots) keep.Add(RootKeyedPartName(r, o));   // a partial written by a build up to 1211
        }
        foreach (var root in roots)
        {
            try
            {
                var dir = Path.Combine(root, ".grog-tmp");
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*.part"))
                {
                    if (keep.Contains(Path.GetFileName(f))) continue;
                    // Another process (a CLI run) may own a fresh one; only what has sat untouched a while goes.
                    try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < TimeSpan.FromMinutes(10)) continue; } catch { continue; }
                    try { File.Delete(f); n++; } catch { /* in use or gone: leave it */ }
                }
                RemoveTmpDirIfEmpty(dir);
            }
            catch { /* unreadable root: nothing to sweep */ }
        }
        return n;
    }

    /// <summary>Manifest-relative path in the documented shape: forward slashes on every OS (BackupLayout
    /// resolves both, but string comparisons in orphan/dedup passes must see one form).</summary>
    internal static string RelPath(string relBase, string fullPath)
        => Path.GetRelativePath(relBase, fullPath).Replace('\\', '/');

    /// <summary>Identity-keyed partial name: root + game + FileKey. Stable across runs and unique per file,
    /// whatever GOG's display name says.</summary>
    /// <remarks>The root's PATH is no longer in the key (QA 09-19): a stick re-plugged under another drive letter
    /// hashed to a new name, so its .part was orphaned while the record still discounted the file by it, and the
    /// restart died out of space. <see cref="RootKeyedPartName"/> is the old name, adopted once where found.</remarks>
    private static string StablePartName(Grog.Core.Models.GameFile f)
        => StableTempName($"part|{f.GameGogId}|{f.FileKey}");
    private static string RootKeyedPartName(string rootBase, Grog.Core.Models.GameFile f)
        => StableTempName($"{rootBase}|{f.GameGogId}|{f.FileKey}");

    /// <summary>The .part names that belong to <paramref name="f"/>, whatever root holds them (current name only:
    /// a root-keyed legacy name cannot be derived without the path it was written under).</summary>
    public static string PartNameFor(Grog.Core.Models.GameFile f) => StablePartName(f);

    /// <summary>Where this file's resumable .part lives on <paramref name="rootBase"/>: the same identity key the
    /// engine writes, so Delete can remove the partial it would otherwise orphan.</summary>
    public static string PartialPathFor(string rootBase, Grog.Core.Models.GameFile f)
    {
        var now = Path.Combine(rootBase, ".grog-tmp", StablePartName(f));
        var legacy = Path.Combine(rootBase, ".grog-tmp", RootKeyedPartName(rootBase, f));
        try { if (!File.Exists(now) && File.Exists(legacy)) return legacy; } catch { /* unreadable: the current name */ }
        return now;
    }

    private static string StableTempName(string finalPath)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(finalPath));
        return Convert.ToHexString(hash, 0, 8) + ".part";   // 16 hex chars = 64 bits
    }

    /// <summary>Where a superseded build is archived when <see cref="KeepOldVersions"/> is on. Sits beside
    /// Games/ and Extras/ rather than inside a game folder, so it is one obvious place to look at (and to
    /// account for) rather than debris scattered through the library.</summary>
    public const string OldVersionsFolder = "Old Versions";

    /// <summary>Opt-in: keep the previous build when an update replaces it. Off by default because it
    /// silently doubles the cost of every updated game. On, it is the only copy of a build GOG no longer
    /// offers -- the revert path when a re-release breaks mods or saves.</summary>
    public bool KeepOldVersions { get; set; }

    /// <summary>The file we are about to replace, if any. An update reuses GOG's file SLOT but often lands
    /// under a new CDN name, so the old build would otherwise stay on disk unreferenced: the manifest points
    /// at the new file and nothing remembers the old one. Either archive it or remove it, never orphan it.</summary>
    private void RetireSupersededCopy(DownloadTask task, string newFinalPath, string relBase, string gameTitle, string? rootId)
    {
        try
        {
            // The superseded copy's relative path is relative to the root it ACTUALLY lives on (its stored
            // RootId), which is not necessarily the root the update is landing on (routing/placement can move
            // it). Resolve against the old root, or we'd look on the wrong device and orphan the old build.
            // (RetireSupersededCopy runs BEFORE UpdateManifestEntry rewrites task.File.RootId, so it's still old.)
            if (CurrentCopyPath(task) is not { } prev) return;
            if (string.Equals(Path.GetFullPath(prev), Path.GetFullPath(newFinalPath),
                              StringComparison.OrdinalIgnoreCase)) return;   // same path: overwritten in place (archived before the move when kept)

            if (!KeepOldVersions) { File.Delete(prev); return; }
            ArchiveOldVersion(task, prev, relBase, gameTitle, rootId);
        }
        catch { /* best effort: retiring the old build must never fail the new download */ }
    }

    /// <summary>The copy the manifest record currently points at, when it is on disk: the old build an update
    /// is about to replace. Null when the record holds nothing or the file is gone.</summary>
    private string? CurrentCopyPath(DownloadTask task)
    {
        var prevRel = task.File.LocalRelativePath;
        if (string.IsNullOrWhiteSpace(prevRel)) return null;
        var oldBase = (task.File.RootId is { } orid ? Layout?.RootPath(orid) : null) ?? BackupRoot;
        var prev = Path.Combine(oldBase, prevRel!);
        return File.Exists(prev) ? prev : null;
    }

    /// <summary>Test seam: runs with (partPath, finalPath) just before the .part is moved under its final name.</summary>
    internal Action<string, string>? BeforeFinalMove;

    /// <summary>The sibling name an old build waits under while the new one is moved into place.</summary>
    internal const string HeldOldBuildSuffix = ".grogold";

    /// <summary>An update that keeps the file's name lands ON the old build, so the overwrite would lose it. With
    /// KeepOldVersions on, the old copy is renamed to a sibling temp name first (same folder, no bytes move) and
    /// that path is returned; off, or nothing to keep, null: the overwrite is the intended outcome.</summary>
    private string? HoldOldBuild(DownloadTask task, string finalPath)
    {
        if (!KeepOldVersions || task.File.RefetchRequested) return null;   // a fix replaces a bad copy; that is not a version worth keeping
        try
        {
            if (CurrentCopyPath(task) is not { } prev) return null;
            if (!string.Equals(Path.GetFullPath(prev), Path.GetFullPath(finalPath), StringComparison.OrdinalIgnoreCase)) return null;
            var held = finalPath + HeldOldBuildSuffix;
            File.Move(prev, held, overwrite: true);
            return held;
        }
        catch { return null; /* best effort: holding the old build must never fail the new download */ }
    }

    /// <summary>The new build is under the name: archive the held old build into Old Versions under its
    /// original filename. Best effort; a failure leaves it under the sibling name rather than losing it.</summary>
    private void ArchiveHeldBuild(DownloadTask task, string held, string finalPath, string relBase, string? rootId)
    {
        try { ArchiveOldVersion(task, held, relBase, task.GameTitle, rootId, Path.GetFileName(finalPath)); }
        catch { /* best effort: archiving the old build must never fail the new download */ }
    }

    /// <summary>Move a superseded build into Old Versions and announce it as a tracked old-version record.
    /// <paramref name="archiveAs"/> names the archived file when the source sits under a temp name.</summary>
    private void ArchiveOldVersion(DownloadTask task, string prev, string relBase, string gameTitle, string? rootId, string? archiveAs = null)
    {
        // Archived on the drive it ALREADY sits on (QA 09-19): an update that spilled to another drive dragged
        // the old build across with it, a whole second file of room on the target that no plan had charged.
        // In place it is a rename: no bytes move, no drive's free space changes.
        if (task.File.RootId is { } keepRoot && Layout?.RootPath(keepRoot) is { } keepBase) { relBase = keepBase; rootId = keepRoot; }
        var dir = Path.Combine(relBase, OldVersionsFolder, Naming.Slug(gameTitle));
        Directory.CreateDirectory(dir);
        var name = archiveAs ?? Path.GetFileName(prev);
        var dest = Path.Combine(dir, name);
        // Two supersessions of one slot can share a CDN name; keep both rather than clobbering the older.
        for (int n = 2; File.Exists(dest); n++)
            dest = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
        File.Move(prev, dest);

        // Record the archived build so it's tracked (movable, counted for disk) rather than orphaned.
        // A distinct FileKey (slot key + archived filename) keeps multiple retained builds unique.
        long size = 0; try { size = new FileInfo(dest).Length; } catch { }
        OldVersionArchived?.Invoke(new Models.GameFile
        {
            GameGogId = task.File.GameGogId,
            FileKey = task.File.FileKey + "#old:" + Path.GetFileName(dest),
            Kind = task.File.Kind,
            Name = task.File.Name,
            ExtraType = task.File.ExtraType,
            Os = task.File.Os,
            Language = task.File.Language,
            Version = task.File.Version,
            RootId = rootId,
            LocalRelativePath = RelPath(relBase, dest),
            LocalSizeBytes = size,
            State = Models.FileState.Verified,
            IsOldVersion = true,
        });
    }

    private void UpdateManifestEntry(DownloadTask task, string finalPath, long size, bool verified,
        string relBase, string? rootId)
    {
        RetireSupersededCopy(task, finalPath, relBase, task.GameTitle, rootId);
        task.File.RootId = rootId;
        task.File.LocalRelativePath = RelPath(relBase, finalPath);
        task.File.ResolvedFileName = Path.GetFileName(finalPath);   // the real name, for import matching + repair
        task.File.LocalSizeBytes = size;
        task.File.DownloadedAt = DateTimeOffset.UtcNow;
        task.File.State = verified ? FileState.Verified : FileState.Present;
        task.File.RefetchRequested = false;   // fresh bytes landed
        if (verified) task.File.LastVerifiedAt = DateTimeOffset.UtcNow;
    }

}

/// <summary>Thrown on 429/503 to trigger backoff-and-retry, carrying an optional server delay.</summary>
internal sealed class BackoffException : Exception
{
    public TimeSpan? RetryAfter { get; }
    public BackoffException(TimeSpan? retryAfter) : base("Server asked to slow down.") => RetryAfter = retryAfter;
}

/// <summary>Simple rate limiter. The rate is read per chunk so a settings change takes effect on an
/// in-flight transfer; a change resets the accounting window so old-rate arrears cannot stall or burst
/// the new rate. 0 (or less) = the throttle path is skipped entirely -- disabled is always uncapped.</summary>
internal sealed class RateLimiter
{
    private readonly Func<long> _bytesPerSec;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    private long _bytesSinceReset;
    private long _lastRate;

    /// <summary>The sleep is injectable so tests assert whether a wait was asked for, not how long the clock ran.</summary>
    public RateLimiter(Func<long> bytesPerSec, Func<TimeSpan, CancellationToken, Task>? delay = null)
    { _bytesPerSec = bytesPerSec; _delay = delay ?? ((d, c) => Task.Delay(d, c)); }
    public RateLimiter(long bytesPerSec) : this(() => bytesPerSec) { }

    public async Task ThrottleAsync(int bytes, CancellationToken ct)
    {
        var rate = _bytesPerSec();
        if (rate != _lastRate) { _lastRate = rate; _sw.Restart(); _bytesSinceReset = 0; }
        if (rate <= 0) return;
        _bytesSinceReset += bytes;
        var expected = (double)_bytesSinceReset / rate;
        var actual = _sw.Elapsed.TotalSeconds;
        if (expected > actual) await _delay(TimeSpan.FromSeconds(expected - actual), ct);
        if (_sw.Elapsed.TotalSeconds > 5) { _sw.Restart(); _bytesSinceReset = 0; }
    }
}

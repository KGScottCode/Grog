// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Download;
using Grog.Core.Manifest;

namespace Grog.Core.Runs;

/// <summary>
/// Turns an engine's progress events into settled manifest records: every task that reached a terminal
/// state is settled exactly once (DownloadSettlement), journalled, reported to the host, and the manifest
/// is checkpointed on a throttle so a killed run forgets at most a window of work; the run journal holds the rest.
///
/// <para>Scans the WHOLE snapshot on every event, keyed by task id, rather than trusting the event's own
/// task: hosts coalesce events (the App drops intermediates by design), and a per-event check left files
/// that finished inside one window unsettled. The App learned that the hard way (09-01); the CLI used a
/// per-event check until 09-02. One implementation now.</para>
/// </summary>
public sealed class RunSettler : IDisposable
{
    private readonly IManifestStore _manifest;
    private readonly DownloadEngine _engine;
    private readonly RunJournal? _journal;
    private readonly IBackupHost _host;
    private readonly Dictionary<Guid, DownloadTaskState> _lastState = new();
    private readonly object _gate = new();
    private readonly TimeSpan _checkpointEvery;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    /// <summary>Serialises host notifications. Two workers finishing at the same instant used to call
    /// TaskSettled concurrently, and a host doing the obvious thing (appending to a list) silently lost one
    /// (owner-hit 09-09). Never held while taking <see cref="_gate"/> or the manifest gate, so it cannot
    /// deadlock with a settle; a host that marshals to a UI thread posts and returns, it does not block.</summary>
    private readonly object _notifyGate = new();
    private DateTime _lastSaveUtc = DateTime.MinValue;

    public int Completed { get; private set; }
    /// <summary>Condemned this run: three strikes, now Corrupt. Only an explicit retry re-admits these.</summary>
    /// <summary>Distinct files given up on this run; a file that fails twice counts once, and a later landing removes it.</summary>
    public int Failed => _failedKeys.Count;
    private readonly HashSet<(long, string)> _failedKeys = new();
    /// <summary>Failed this run with strikes left: not done, but the next run tries again on its own. A host
    /// counting "what did not land" adds these to <see cref="Failed"/>; the App's condemned count does not.</summary>
    public int Retrying { get; private set; }
    public int Refused { get; private set; }
    public int Skipped { get; private set; }
    /// <summary>Files the drive had no room for once their real size was known: held as won't-fit, not failed.</summary>
    public int NoRoom { get; private set; }
    /// <summary>(09-25) Files stopped because their storage went away: waiting for the drive, not failed.</summary>
    public int DriveOffline { get; private set; }
    private readonly HashSet<(long, string)> _driveOfflineKeys = new();
    /// <summary>The files counted in <see cref="DriveOffline"/>.</summary>
    public bool CountedDriveOffline(long gogId, string fileKey) => _driveOfflineKeys.Contains((gogId, fileKey));

    /// <summary>(New items 09-09) The scope this run measures completeness against. THE one place that
    /// knows an item just became complete is the settle below, so the auto-clear of the New flag lives
    /// there; null falls back to the manifest's saved scope (a caller that never sets it still behaves).</summary>
    public Sync.Scope? ClearNewScope { get; set; }

    /// <summary>A task changed state (fired once per transition, from the settle pass, on the settler's own
    /// worker). BackupRun's mid-run re-plan listens here; a listener must not block.</summary>
    public event Action<DownloadTask, DownloadTaskState>? Transition;

    public RunSettler(IManifestStore manifest, DownloadEngine engine, RunJournal? journal, IBackupHost host,
                      TimeSpan? checkpointEvery = null)
    {
        _manifest = manifest; _engine = engine; _journal = journal; _host = host;
        // 10 s: the run journal already carries crash recovery, so the whole-manifest write is a coarse safety net.
        _checkpointEvery = checkpointEvery ?? TimeSpan.FromSeconds(10);
        _loop = Task.Run(LoopAsync);
        _engine.TaskChanged += OnTaskChanged;
    }

    // (09-25) ONE ordered settle worker. The engine raises TaskChanged on whatever thread called it, the UI
    // thread included (a cancel, a re-plan's CancelRange), and a settle pass takes the settler lock, the manifest
    // gate, writes the journal and waits its turn to notify behind a worker's drive probe: 5 to 24 s on the UI
    // thread (walk 09-25). The event now only marks the settler dirty and wakes this loop; a pass scans the whole
    // snapshot, so a burst of events collapses into one pass with nothing lost. Settle and notify run on one
    // thread, so their order is total.
    private int _dirty;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private long _requestedGen, _doneGen;
    private readonly object _barrierLock = new();
    private readonly List<(long Gen, TaskCompletionSource Done)> _barriers = new();

    private void OnTaskChanged(DownloadTask _) => Signal();

    private void Signal()
    {
        if (Interlocked.Exchange(ref _dirty, 1) == 0)
            try { _wake.Release(); } catch (ObjectDisposedException) { /* run over */ }
    }

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await _wake.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            // Cleared BEFORE the pass: an event that lands mid-pass forces one more pass, never a lost settle.
            while (Interlocked.Exchange(ref _dirty, 0) == 1)
            {
                long gen = Interlocked.Read(ref _requestedGen);
                try { SettlePass(); }
                catch (Exception ex) { try { _host.Log($"Settle pass failed: {ex.Message}"); } catch { } }
                ReleaseBarriers(gen);
            }
        }
    }

    private void ReleaseBarriers(long gen)
    {
        List<TaskCompletionSource>? done = null;
        lock (_barrierLock)
        {
            if (gen > _doneGen) _doneGen = gen;
            for (int i = _barriers.Count - 1; i >= 0; i--)
                if (_barriers[i].Gen <= _doneGen) { (done ??= new()).Add(_barriers[i].Done); _barriers.RemoveAt(i); }
        }
        if (done is not null) foreach (var d in done) d.TrySetResult();
    }

    /// <summary>Wait (bounded, 10 s) until every event raised before this call is settled and notified. The run
    /// calls it after the engine drains, so its admission pass sees the landed files as landed.</summary>
    public async Task SettledAsync()
    {
        var barrier = SettledBarrier();
        await Task.WhenAny(barrier, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
    }

    /// <summary>Wait until a settle pass that STARTED after this call has finished, notifications included.</summary>
    private Task SettledBarrier()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_stop.IsCancellationRequested) { tcs.TrySetResult(); return tcs.Task; }   // disposed: nothing will settle
        lock (_barrierLock) _barriers.Add((Interlocked.Increment(ref _requestedGen), tcs));
        Interlocked.Exchange(ref _dirty, 1);
        try { _wake.Release(); } catch (ObjectDisposedException) { tcs.TrySetResult(); }
        return tcs.Task;
    }

    /// <summary>One settle pass over the engine's snapshot: idempotent, because a task whose state has not
    /// moved since last time is skipped. Runs ONLY on the settler's worker (09-25); <see cref="FlushAsync"/> waits
    /// for a pass that started after it, so a run never returns with a landed file unsettled or a host un-notified
    /// (owner-hit 09-09).</summary>
    private void SettlePass()
    {
        UiThreadGuard.NotOnUi("a settle pass");
        bool changed = false;
        // Settle under the lock, notify outside it: a host callback that marshals to a UI thread must never
        // run while this lock is held, or a second event arriving on that thread deadlocks against it.
        var notify = new List<(DownloadTask Task, DownloadTaskState Prev, DownloadSettlement.Outcome? Outcome)>();
        // Journal lines are collected under the locks and written after them, in one batch (09-25).
        var journal = new List<Action<RunJournal>>();
        lock (_gate)
        using (_manifest.Gate.Enter())   // settling writes file state + the queue: under the manifest gate (09-08)
        {
            _manifest.Gate.AssertHeld();   // canary (manifest gate 09-08)
            foreach (var x in _engine.Snapshot)

            {
                _lastState.TryGetValue(x.Id, out var prev);
                if (prev == x.State) continue;
                _lastState[x.Id] = x.State;
                if (x.State is not (DownloadTaskState.Completed or DownloadTaskState.Failed or DownloadTaskState.Skipped
                                    or DownloadTaskState.Canceled))
                {
                    notify.Add((x, prev, null));
                    continue;
                }
                if (x.State == DownloadTaskState.Canceled)
                {
                    // A stopped task keeps its partial and its record untouched; the journal notes it, uncounted.
                    { var (n, b, st, e) = (x.ResolvedFileName ?? x.File.Name, x.BytesReceived, x.State.ToString(), x.Error);
                      journal.Add(j => j.FileNotCounted(n, "canceled", b, st, e)); }
                    notify.Add((x, prev, DownloadSettlement.Outcome.NotFinished));
                    continue;
                }

                var m = _manifest.Current;
                var file = m.ItemById(x.File.GameGogId)?.Files.Find(f => f.FileKey == x.File.FileKey);
                var (outcome, manifestChanged) = DownloadSettlement.Settle(m, x, file);
                changed |= manifestChanged;
                // (New items 09-09) A file just landed: if that completed the whole item in scope, it is no
                // longer new to you. Completeness comes from BackupScope, never re-derived here.
                if (outcome == DownloadSettlement.Outcome.Completed)
                    changed |= Sync.NewItems.ClearIfComplete(m, x.File.GameGogId,
                        ClearNewScope ?? m.Scope?.ToScope() ?? Sync.Scope.Both);
                switch (outcome)
                {
                    case DownloadSettlement.Outcome.Completed: Completed++; _failedKeys.Remove((x.File.GameGogId, x.File.FileKey)); break;
                    case DownloadSettlement.Outcome.GaveUp: _failedKeys.Add((x.File.GameGogId, x.File.FileKey)); break;
                    case DownloadSettlement.Outcome.WillRetry: Retrying++; break;
                    case DownloadSettlement.Outcome.Unavailable: Refused++; break;
                    case DownloadSettlement.Outcome.Skipped: Skipped++; break;
                    case DownloadSettlement.Outcome.NoRoom: NoRoom++; break;
                    case DownloadSettlement.Outcome.DriveOffline:
                        if (_driveOfflineKeys.Add((x.File.GameGogId, x.File.FileKey))) DriveOffline++;   // once per file: a drive that flaps twice stops the re-added copy too
                        break;
                }
                var jname = x.ResolvedFileName ?? x.File.Name;
                long jbytes = x.BytesReceived; string jstate = x.State.ToString();
                switch (outcome)
                {
                    // A refusal is not a failure (you cannot be incomplete against something you were never
                    // offered) and a skip waits for a sign-in: recorded, never counted as failed.
                    case DownloadSettlement.Outcome.Unavailable:
                        { var r = x.UnavailableReason; journal.Add(j => j.FileNotCounted(jname, "unavailable", jbytes, jstate, r)); break; }
                    case DownloadSettlement.Outcome.DriveOffline:
                        { var r = x.Error; journal.Add(j => j.FileNotCounted(jname, "drive offline", jbytes, jstate, r)); break; }
                    case DownloadSettlement.Outcome.NoRoom:
                        // Held, not failed: the drive had no room. It stays queued as won't-fit and is counted there.
                        { var r = x.Error; journal.Add(j => j.FileNotCounted(jname, "no room", jbytes, jstate, r)); break; }
                    case DownloadSettlement.Outcome.Skipped:
                        { var r = x.SkipReason; journal.Add(j => j.FileNotCounted(jname, "skipped", jbytes, jstate, r)); break; }
                    case DownloadSettlement.Outcome.NotFinished:
                        // The file is already present (an earlier attempt landed it): no strike, and not a failure in
                        // the journal either, or the card named a Verified file as failed (QA 09-30 C6).
                        { var r = x.Error; journal.Add(j => j.FileNotCounted(jname, "already present", jbytes, jstate, r)); break; }
                    default:
                        { bool ok = outcome == DownloadSettlement.Outcome.Completed; var err = x.Error;
                          var kind = x.State == DownloadTaskState.Failed ? x.FailureKind.ToString().ToLowerInvariant() : null;
                          long jg = x.File.GameGogId; string jk = x.File.FileKey;
                          journal.Add(j => j.File(jname, ok, jbytes, jstate, err, kind, jg, jk)); break; }
                }
                notify.Add((x, prev, outcome));
            }
        }
        if (_journal is { } jr && journal.Count > 0)
        {
            jr.BeginBatch();
            try { foreach (var op in journal) op(jr); }
            finally { jr.EndBatch(); }
        }
        // Transition fires AFTER the record settled, so a listener reading file state sees the settled value.
        // _notifyGate is kept as a guard: only the settler's worker notifies now.
        if (notify.Count > 0)
            lock (_notifyGate)
                foreach (var (task, prev, outcome) in notify)
                {
                    // One subscriber that throws must not cost the rest of the pass its notifications: those tasks are
                    // already marked seen and would never be told again (review 09-25).
                    try { Transition?.Invoke(task, prev); } catch (Exception ex) { try { _host.Log($"Settle notify failed: {ex.Message}"); } catch { } }
                    if (outcome is { } o)
                        try { _host.TaskSettled(task, o); } catch (Exception ex) { try { _host.Log($"Settle notify failed: {ex.Message}"); } catch { } }
                }
        if (changed) RequestCheckpoint();
    }

    /// <summary>Throttled: one whole-manifest write per window at most, never two overlapping. A request inside
    /// the window is not dropped: one trailing save is scheduled for the window's end (coalesced), so a settle
    /// just after a checkpoint waits at most one window, not until the next transition.</summary>
    private void RequestCheckpoint()
    {
        lock (_gate)
        {
            var left = _checkpointEvery - (DateTime.UtcNow - _lastSaveUtc);
            if (left > TimeSpan.Zero)
            {
                if (_trailingSave) return;   // one pending trailing save covers every request in the window
                _trailingSave = true;
                _ = Task.Delay(left, _stop.Token).ContinueWith(_ =>
                {
                    lock (_gate) _trailingSave = false;
                    if (!_stop.IsCancellationRequested) RequestCheckpoint();
                }, TaskScheduler.Default);
                return;
            }
            _lastSaveUtc = DateTime.UtcNow;
        }
        _ = Task.Run(async () =>
        {
            if (!await _saveGate.WaitAsync(0)) return;
            try { await _manifest.SaveAsync(); } catch { /* checkpoint is best-effort; the end-of-run save is authoritative */ }
            finally { _saveGate.Release(); }
        });
    }
    private bool _trailingSave;

    /// <summary>Test seam: the checkpoint window in force.</summary>
    internal TimeSpan CheckpointEvery => _checkpointEvery;

    /// <summary>Test seam: how long <see cref="FlushAsync"/> waits for the settle worker before saving anyway.</summary>
    internal TimeSpan FlushBound { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>True when the last <see cref="FlushAsync"/> saved before the settle worker finished, so the
    /// manifest on disk may hold records not yet settled. The run's final save reads it to decide on one more
    /// bounded flush; a second <see cref="FlushAsync"/> is always safe and clears it once the worker catches up.</summary>
    public bool HasUnsettledWork => Volatile.Read(ref _unsettled) != 0;
    private int _unsettled;

    /// <summary>The end of the run: drain any pending checkpoint into one authoritative save.</summary>
    public async Task FlushAsync()
    {
        // (09-09) the tail: settle and notify anything not yet settled. (09-25) Through the settle worker: wait for
        // a pass that started after this call to finish, notifications included. Bounded: a wedged host callback
        // must not hang the end of a run; the bound expiring is remembered in HasUnsettledWork.
        var barrier = SettledBarrier();
        if (await Task.WhenAny(barrier, Task.Delay(FlushBound)).ConfigureAwait(false) != barrier)
        {
            Volatile.Write(ref _unsettled, 1);
            try { _host.Log($"Settle pass still running after {FlushBound.TotalSeconds:F0} s at the end of the run; saving what is settled."); } catch { }
        }
        else Volatile.Write(ref _unsettled, 0);
        await _saveGate.WaitAsync();
        try { await _manifest.SaveAsync(); } finally { _saveGate.Release(); }
    }

    public void Dispose()
    {
        _engine.TaskChanged -= OnTaskChanged;
        _stop.Cancel();
        // Let a pass in flight finish (bounded), release anyone still waiting, then free the primitives.
        try { _loop.Wait(TimeSpan.FromSeconds(1)); } catch { /* the loop logs its own failures */ }
        List<TaskCompletionSource> left;
        lock (_barrierLock) { left = _barriers.Select(b => b.Done).ToList(); _barriers.Clear(); }
        foreach (var d in left) d.TrySetResult();
        if (_loop.IsCompleted) { _wake.Dispose(); _stop.Dispose(); }
    }
}

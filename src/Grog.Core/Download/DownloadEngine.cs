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

public sealed class DownloadEngine
{
    private readonly GogApiClient _api;
    private readonly HttpClient _http;
    private readonly List<DownloadTask> _queue = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private int _nextIndex;
    private int _total;
    private int _workerCount;
    private volatile bool _running;
    private TaskCompletionSource? _runComplete;

    public string BackupRoot { get; set; } = "";
    public Volumes.PendingWrites? PendingWrites { get; init; }
    public Volumes.IBackupLayout? Layout { get; set; }
    public int MaxConcurrent { get; set; } = 3;
    public long BytesPerSecondLimit { get; set; } = 0;
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public int MaxBackoffRetries { get; set; } = 5;

    public Auth.AccountSessions? Sessions { get; set; }

    public event Action<DownloadTask>? TaskChanged;
    public event Action<Models.GameFile>? OldVersionArchived;
    private Action<string>? _debugTrace;
    public Action<string>? DebugTrace { get => _debugTrace; set => _debugTrace = Grog.Core.Format.UrlRedaction.Wrap(value); }
    public Action<string>? PartialNotice { get; set; }
    private void NotePartial(DownloadTask t, string what)
        => PartialNotice?.Invoke($"{t.GameTitle} - {t.File.ResolvedFileName ?? PartialLabel(t.File)}: {what}");
    private static string PartialLabel(Models.GameFile f)
        => string.IsNullOrEmpty(f.Os) ? f.Name : $"{f.Name} ({Models.PlatformNames.Label(f.Os)})";

    public DeviceWriteMonitor? DeviceMonitor { get; set; }
    public IEnumerable<DownloadTask> Snapshot { get { lock (_gate) return _queue.ToArray(); } }
    public int QueuedCount { get { lock (_gate) return _queue.Count(t => t.State is DownloadTaskState.Pending or DownloadTaskState.Active); } }

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
        EnsureWorkers();   // a running engine whose surplus workers retired gets a slot for the new file
        TaskChanged?.Invoke(task);
        QueueChanged?.Invoke();
    }

    public void EnqueueRange(IEnumerable<DownloadTask> tasks)
    {
        lock (_gate)
        {
            // One live task per file
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
        // Workers retire when TakeNext runs dry
        EnsureWorkers();
        QueueChanged?.Invoke();
    }

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

            // Rebuild preserving finished tasks' relative order
            var done = _queue.Where(t => t.State is not (DownloadTaskState.Pending or DownloadTaskState.Active)).ToList();
            _queue.Clear(); _queue.AddRange(live); _queue.AddRange(done);

            bumped.AddRange(BeyondWindow(live));
        }
        foreach (var b in bumped)
        {
            b.PreemptRequested = true;
            SafeCancel(b);
        }
        QueueChanged?.Invoke();
    }

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

            bumped.AddRange(BeyondWindow(sorted));
        }
        foreach (var b in bumped)
        {
            b.PreemptRequested = true;
            SafeCancel(b);
        }
        QueueChanged?.Invoke();
    }

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
                bool wasActive = task.State == DownloadTaskState.Active;
                if (wasActive) { actives.Add(task); n++; continue; }
                if (task.State != DownloadTaskState.Pending) continue;
                task.State = DownloadTaskState.Canceled;
                if (_queue.Remove(task)) n++;
            }
            _total = _queue.Count;
        }
        foreach (var a in actives) SafeCancel(a);

        if (n > 0) { TaskChanged?.Invoke(list[0]); QueueChanged?.Invoke(); }
        return n;
    }

    public bool CancelFile(long gogId, string fileKey)
    {
        var t = Snapshot.FirstOrDefault(x => x.File.GameGogId == gogId && x.File.FileKey == fileKey);
        if (t is null) return false;
        Cancel(t);
        return true;
    }

    public int CancelGames(IReadOnlySet<long> gogIds)
    {
        return CancelRange(Snapshot.Where(x => gogIds.Contains(x.File.GameGogId)).ToList());
    }

    public void Cancel(DownloadTask task)
    {
        bool wasActive;
        lock (_gate)
        {
            wasActive = task.State == DownloadTaskState.Active;
            if (task.State == DownloadTaskState.Pending) task.State = DownloadTaskState.Canceled;
            if (!wasActive && _queue.Remove(task)) _total = _queue.Count;   // it left the run entirely: the total follows
        }
        if (wasActive) SafeCancel(task);
        TaskChanged?.Invoke(task);
        QueueChanged?.Invoke();
    }

    private List<DownloadTask> BeyondWindow(List<DownloadTask> live)
    {
        int slots = Math.Max(1, MaxConcurrent);
        var bumped = new List<DownloadTask>();
        int ahead = 0;   
        var slowTaken = new HashSet<string>(StringComparer.Ordinal);
        var slowWaiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in live)
        {
            string root = t.TargetRootId ?? "";
            bool slow = DeviceMonitor is { } mon && mon.IsSlow(root);
            if (t.State == DownloadTaskState.Active)
            {
                bool finishing = t.FinishingPhase is not null;
                if (!finishing && (ahead >= slots || (slow && slowWaiting.Contains(root)))) { bumped.Add(t); continue; }
                ahead++; 
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

    public bool StartNothingNew { get; set; }

    public IReadOnlySet<(long GogId, string FileKey)>? OnlyStart { get; set; }

    // Storage that went away mid-run
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _offlineRoots = new(StringComparer.Ordinal);
    public event Action<string>? RootWentAway;
    public IReadOnlyCollection<string> OfflineRoots => _offlineRoots.Keys.ToList();
    public bool RootBack(string? rootId)
    {
        _offlineRoots.TryRemove(rootId ?? "", out _);
        Grog.Core.Volumes.DriveWaits.Remove(rootId);
        lock (_retireGate)  
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
    public List<(long GogId, string FileKey)> WaitingPendingKeys()
    {
        lock (_gate) return _queue.Where(x => x.State == DownloadTaskState.Pending && Waiting(x))
                                  .Select(x => (x.File.GameGogId, x.File.FileKey)).ToList();
    }
    public int StartablePendingCount()
    {
        lock (_gate) return _queue.Count(x => x.State == DownloadTaskState.Pending && !Waiting(x));
    }

    private readonly object _retireGate = new();
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
                if (_held.Count > 0 && _held.Contains((x.File.GameGogId, x.File.FileKey))) { gatedPending = true; continue; }  
                anyAllowed = true;
                if (IsGated(x)) { gatedPending = true; continue; }
                t = x; break;
            }
            if (t is not null) { t.State = DownloadTaskState.Active; t.FinishingPhase = null; }   
            else if (only is not null && !anyAllowed) StartNothingNew = true;   
            return t;
        }
    }

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
            // Live scale-down
            if (Volatile.Read(ref _workerCount) > Math.Max(1, MaxConcurrent)) return;
            var task = TakeNext(out bool gated);
            if (task is null)
            {
                if (!gated) return;
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return; }
                continue;
            }

            task.Index = Interlocked.Increment(ref _nextIndex);
            // Per-task token
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct);
            task.Cancellation = link;
            try
            {
                TaskChanged?.Invoke(task);
                QueueChanged?.Invoke();
                bool skipped = await ExecuteAsync(task, link.Token);
                task.State = skipped ? DownloadTaskState.Skipped : DownloadTaskState.Completed;
            }
            catch (OperationCanceledException) when (link.Token.IsCancellationRequested || task.PreemptRequested)
            {
                // Pre-empted
                if (task.PreemptRequested && !ct.IsCancellationRequested)
                {
                    task.PreemptRequested = false;
                    task.State = DownloadTaskState.Pending;
                    task.BytesPerSecond = 0; task.SecondsLeft = null;
                }
                else task.State = DownloadTaskState.Canceled;
            }
            catch (Grog.Core.Api.GogUnavailableException uex)
            {
                // GOG refused to serve this file to the account
                task.Error = uex.Reason;
                task.UnavailableReason = uex.Reason;
                task.State = DownloadTaskState.Failed;
            }
            catch (Exception ex)
            {
                DebugTrace?.Invoke($"FAIL {task.File.FileKey}: {ex}");
                task.FailureKind = DownloadFailure.Of(ex);
                bool gone = ex is DriveGoneException || RootGone(task);
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
                if (task.State is not (DownloadTaskState.Completed or DownloadTaskState.Skipped) && task.File.HasPartial && task.BytesReceived > 0
                    && PartLength(task) is { } partLen)
                    task.File.PartialBytes = partLen;
                task.Cancellation = null;
                task.BytesPerSecond = 0; task.SecondsLeft = null;
                TaskChanged?.Invoke(task);
                QueueChanged?.Invoke();
            }
        }
      }
      finally
      {
        lock (_retireGate)
            if (Interlocked.Decrement(ref _workerCount) == 0)
            {
                _running = false;
                _runComplete?.TrySetResult();
            }
      }
    }

    public int TotalCount => _total;

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
                // ONE account's session lapsed mid-run
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
                throw new TimeoutException("GOG did not answer the download request in time.", oce);
            }
            catch (BackoffException bex) when (attempt < MaxBackoffRetries)
            {
                attempt++;
                var delay = BackoffPolicy.Delay(attempt, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), bex.RetryAfter);
                PartialNotice?.Invoke($"{task.GameTitle} - {task.File.Name}: GOG's download server asked us to slow down; waiting {delay.TotalSeconds:F0} s (try {attempt} of {MaxBackoffRetries}).");
                await Task.Delay(delay, ct);
            }
        }
    }

    private async Task DownloadOneAsync(DownloadTask task, GogApiClient api, CancellationToken ct)
    {
        if (RootGone(task)) throw new DriveGoneException(RootPathFor(task) ?? task.TargetRootId ?? "primary");
        DebugTrace?.Invoke($"start: {task.GameTitle} / {task.File.Name}  key={task.File.FileKey}");
        var startClock = System.Diagnostics.Stopwatch.StartNew();
        bool skipProbe = task.File.ExpectedSizeBytes is > 0 && !string.IsNullOrWhiteSpace(task.File.Name);
        var resolved = await api.ResolveDownloadAsync(task.File.FileKey, ct, probe: !skipProbe);
        long? advertised = resolved.ContentLength ?? (skipProbe ? task.File.ExpectedSizeBytes : null);
        task.BytesTotal = resolved.ContentLength;
        DebugTrace?.Invoke($"resolved len={advertised?.ToString() ?? "?"} name={resolved.FileName} probe={(!skipProbe)}");

        var place = Place(task);
        var partPath = PreparePartial(task, place);
        task.PartPath = partPath;
        if (File.Exists(partPath)) task.File.PartialRootId = place.RootId;

        string? fileName = string.IsNullOrWhiteSpace(resolved.FileName) ? null : Naming.SafeFileName(resolved.FileName);
        if (fileName is null && !skipProbe) fileName = Naming.SafeFileName(task.File.Name);
        string? finalPath = fileName is null ? null : Path.Combine(place.Dir, fileName);

        if (finalPath is not null && TryAdoptExisting(task, fileName!, finalPath, advertised, place)) return;

        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        if (existing > 0 && (resolved.ContentLength ?? task.File.WireSizeBytes) is { } known && existing > known)
        {
            NotePartial(task, $"partial of {Format.ByteFormat.Size(existing)} is not smaller than the file ({Format.ByteFormat.Size(known)}); restarted from zero.");
            DeletePartQuietly(task, partPath);
            existing = 0;
        }

        Task<string?>? md5Task = resolved.ChecksumXmlUrl is { } xmlUrl ? FetchMd5Quiet(api, xmlUrl, ct) : null;

        var request = new HttpRequestMessage(HttpMethod.Get, resolved.FinalUrl);
        if (existing > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

        var response = await SendWithStallTimeoutAsync(request, ct);
        DebugTrace?.Invoke($"GET status={(int)response.StatusCode} existing={existing}");

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

        if (!partIsComplete && !resuming)
            CheckFreshBody(task, response, resolved);
        bool sizeless = !partIsComplete && resolved.ContentLength is null && response.Content.Headers.ContentLength is null;

        if (fileName is null)
        {
            if (partIsComplete)
            {
                var named = await api.ResolveDownloadAsync(task.File.FileKey, ct, probe: true);
                fileName = Naming.SafeFileName(string.IsNullOrWhiteSpace(named.FileName) ? task.File.Name : named.FileName);
            }
            else fileName = Naming.SafeFileName(GogApiClient.ExtractFileName(response, resolved.FinalUrl));
            finalPath = Path.Combine(place.Dir, fileName);
            long? adoptSize = partIsComplete ? existing
                : response.Content.Headers.ContentLength is { } getLen ? existing + getLen : advertised;

            if (TryAdoptExisting(task, fileName, finalPath, adoptSize, place)) { RemoveTmpDirIfEmpty(Path.GetDirectoryName(partPath)!); return; }
        }
        task.ResolvedFileName = fileName;

        if (!partIsComplete && resolved.ContentLength is null && response.Content.Headers.ContentLength is { } wireLen)
        {
            task.BytesTotal = existing + wireLen;
            TaskChanged?.Invoke(task);   // the row's total just became real; let the footer see it
        }
        long? expectedTotal = partIsComplete ? existing : resolved.ContentLength ?? task.BytesTotal;   // the CDN's own end wins over a stale probe

        if (expectedTotal is { } real && real > 0)
        {
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
        string? streamedMd5 = null;  
        if (partIsComplete) { task.BytesReceived = existing; TaskChanged?.Invoke(task); }
        else streamedMd5 = await StreamToPartialAsync(task, response, partPath, existing, resuming, ct);
        double transferS = phaseClock.Elapsed.TotalSeconds - task.FlushSeconds; phaseClock.Restart();

        var actualSize = new FileInfo(partPath).Length;
        if (expectedTotal is { } expected && actualSize != expected)
        {
            // The completed .part is the wrong size - it's not a resumable partial. Delete it
            DeletePartQuietly(task, partPath);
            throw new IOException($"Size mismatch: expected {expected}, got {actualSize}");
        }

        bool verified = await VerifyPartialAsync(task, partPath, fileName, md5Task, sizeless, streamedMd5, ct);
        double verifyS = phaseClock.Elapsed.TotalSeconds; phaseClock.Restart();
        DeviceMonitor?.Record(task.TargetRootId ?? "", task.FlushSeconds + verifyS);

        task.FinishingPhase = "Saving"; TaskChanged?.Invoke(task);
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
        if (Layout is { } lay2)
            foreach (var other in lay2.OnlineRootPaths)
                try
                {
                    if (string.Equals(other, place.RootBase, StringComparison.OrdinalIgnoreCase)) continue;
                    var stray = PartialPathFor(other, task.File);
                    if (File.Exists(stray)) { File.Delete(stray); RemoveTmpDirIfEmpty(Path.GetDirectoryName(stray)!); }
                }
                catch { /* the sweep takes it later */ }
        RemoveTmpDirIfEmpty(Path.GetDirectoryName(partPath)!);
        UpdateManifestEntry(task, finalPath!, actualSize, verified, place.RootBase, place.RootId);
        task.PhaseSummary = $"start {startS:F1} s · transfer {transferS:F1} s · flush {task.FlushSeconds:F1} s · verify {verifyS:F1} s · save {phaseClock.Elapsed.TotalSeconds:F1} s";
    }

    private readonly record struct Placement(string Dir, string RootBase, string? RootId);

    private Placement Place(DownloadTask task)
    {
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

    private string PreparePartial(DownloadTask task, Placement place)
    {
        var tmpDir = Path.Combine(place.RootBase, ".grog-tmp");
        lock (_tmpDirGate) Directory.CreateDirectory(tmpDir);
        var partPath = Path.Combine(tmpDir, StablePartName(task.File));
        var rootKeyed = Path.Combine(tmpDir, RootKeyedPartName(place.RootBase, task.File));
        if (!File.Exists(partPath) && File.Exists(rootKeyed))
            try { File.Move(rootKeyed, partPath); } catch { /* start clean if the adopt fails */ }

        var legacyPart = Path.Combine(tmpDir, StableTempName(Path.Combine(place.Dir, Naming.SafeFileName(task.File.Name))));
        if (!File.Exists(partPath) && File.Exists(legacyPart))
            try { File.Move(legacyPart, partPath); } catch { /* start clean if the adopt fails */ }
        if (!File.Exists(partPath) && Layout is { } lay)
            foreach (var otherRoot in lay.OnlineRootPaths)
            {
                if (string.Equals(otherRoot, place.RootBase, StringComparison.OrdinalIgnoreCase)) continue;
                var stray = PartialPathFor(otherRoot, task.File);
                if (!File.Exists(stray)) continue;

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

    private bool TryAdoptExisting(DownloadTask task, string fileName, string finalPath, long? advertised, Placement place)
    {
        if (task.File.RefetchRequested) return false;   // a fix / re-download: the copy on disk is what is being replaced
        if (!File.Exists(finalPath) || advertised is not { } exp || new FileInfo(finalPath).Length != exp) return false;
        task.ResolvedFileName = fileName;
        task.BytesReceived = exp;
        UpdateManifestEntry(task, finalPath, exp, verified: false, place.RootBase, place.RootId);
        return true;
    }

    internal static long LabelTolerance(long label)
    {
        long unit = label >= 1L << 30 ? 1L << 30 : label >= 1L << 20 ? 1L << 20 : 1L << 10;
        return unit + Volumes.PlacementPlanner.LabelSlack(label);
    }

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

    internal static bool RefusesBodyLength(FileKind kind, long len, long label)
        => kind != FileKind.Extra && label > 0 && !PlausibleBodyLength(len, label);

    internal static bool PlausibleBodyLength(long len, long label)
        => Math.Abs(len - label) <= 1L << 20 || (len * 2 >= label && len <= label * 2);

    private async Task<string> StreamToPartialAsync(DownloadTask task, HttpResponseMessage response, string partPath,
                                                    long existing, bool resuming, CancellationToken ct)
    {
        var fileMode = resuming ? FileMode.Append : FileMode.Create;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            // A resume hashes the prefix already on disk once, before appending
            if (resuming && existing > 0) await HashPrefixAsync(partPath, existing, hash, buffer, ct);

            FileStream outStream;
            lock (_tmpDirGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
                outStream = new FileStream(partPath, fileMode, FileAccess.Write, FileShare.None, WriteBufferBytes);
            }
            await using var _ = outStream;
            task.File.HasPartial = true;   // Core's fact, not the host's: a .part exists for this file from here on
            task.File.PartialRootId = task.TargetRootId;   
            await using var inStream = await response.Content.ReadAsStreamAsync(ct);
            task.BytesReceived = existing;
            var throttle = new RateLimiter(() => BytesPerSecondLimit);  

            // Speed + stall tracking
            var speedWindowStart = DateTime.UtcNow;
            long speedWindowBytes = 0;
            var fileEta = new FileEta();
            task.SecondsLeft = fileEta.Update(speedWindowStart, task.BytesReceived, task.BytesTotal);
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
                        task.SecondsLeft = fileEta.Update(DateTime.UtcNow, task.BytesReceived, task.BytesTotal);
                        speedWindowStart = DateTime.UtcNow;
                        speedWindowBytes = 0;
                        TaskChanged?.Invoke(task);
                    }
                    await throttle.ThrottleAsync(read, ct);
                }
            }
            task.FinishingPhase = "Writing to disk"; task.BytesPerSecond = 0; task.SecondsLeft = null; TaskChanged?.Invoke(task);
            var flushClock = System.Diagnostics.Stopwatch.StartNew();
            await outStream.FlushAsync(ct);
            outStream.Flush(flushToDisk: true);
            task.FlushSeconds = flushClock.Elapsed.TotalSeconds;
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

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

        var actualMd5 = streamedMd5 ?? await Grog.Core.Verify.Hashing.Md5Async(partPath, ct);
        if (!string.Equals(actualMd5, expectedMd5, StringComparison.OrdinalIgnoreCase))
        {
            DeletePartQuietly(task, partPath);
            throw new IOException($"MD5 mismatch for {fileName}");
        }
        task.File.ExpectedMd5 = expectedMd5;
        DebugTrace?.Invoke($"verify: MD5 OK for {fileName} ({expectedMd5})");
        return true;
    }

    private static async Task<string?> FetchMd5Quiet(GogApiClient api, string xmlUrl, CancellationToken ct)
    {
        try { return await api.FetchMd5Async(xmlUrl, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public const long FloorBytes = 2 * 1024 * 1024;
    internal Func<string, long> ClusterBytesProbe { get; set; } = path => Storage.DriveResolver.ClusterBytes(path);
    private long ChargeOn(string rootBase, long bytes) => Storage.DriveResolver.OnDisk(bytes, ClusterBytesProbe(rootBase));

    internal const int WriteBufferBytes = 1 << 20;

    internal Func<string, long?> FreeBytesProbe { get; set; } = path =>
    {
        try { return Storage.DriveResolver.For(path)?.AvailableFreeSpace; } catch { return null; }
    };

    internal Func<string, long> MaxFileBytesProbe { get; set; } = Storage.DriveResolver.MaxFileBytes;

    private static readonly object _roomGate = new();

    private void EnsureRoom(DownloadTask task, Placement place, long real, long existing)
    {
        long ceiling = MaxFileBytesProbe(place.RootBase);
        if (real > ceiling)
            throw NoRoomException($"{Format.ByteFormat.Size(real)} is over the {Format.ByteFormat.Size(ceiling)} a single file can be on this drive's filesystem");

        long others = OthersStillToWrite(task, place.RootBase);

        if (FreeBytesProbe(place.RootBase) is not { } probed) return;

        long free = probed - FloorBytes;   // the floor is nobody's to spend
        long need = Math.Max(0, ChargeOn(place.RootBase, real) - existing);
        DebugTrace?.Invoke($"room {task.File.FileKey}: free={free} others={others} need={need} existing={existing} real={real}");
        if (free - others < need)
            throw NoRoomException($"needs {Format.ByteFormat.Size(need)}, and {Format.ByteFormat.Size(Math.Max(0, free - others))} is free once the transfers already running finish");
    }

    private long OthersStillToWrite(DownloadTask task, string rootBase)
    {
        string? myVolume = VolumeOf(rootBase);
        long others = PendingWrites?.ForVolume(rootBase) ?? 0L;   // a running move's remaining copies count too
        lock (_gate)
            foreach (var t in _queue)
            {
                if (ReferenceEquals(t, task) || t.State != DownloadTaskState.Active) continue;
                var root = Layout?.RootPath(t.TargetRootId ?? "") ?? BackupRoot;
                if (!string.Equals(VolumeOf(root), myVolume, StringComparison.OrdinalIgnoreCase)) continue;
                long total = t.BytesTotal ?? t.File.PlanSizeBytes ?? 0;
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

    private static long? PartLength(DownloadTask task)
    {
        try { if (task.PartPath is { } p && File.Exists(p)) return new FileInfo(p).Length; } catch { /* unreadable: unknown */ }
        return null;
    }

    private static void DeletePartQuietly(DownloadTask task, string partPath)
    {
        try { if (File.Exists(partPath)) File.Delete(partPath); } catch { /* best-effort */ }
        task.File.HasPartial = false; task.File.PartialBytes = null;
    }

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
            // The .part lives on the same root as the resolved final file
            else root = Layout?.RootPath(f.RootId ?? "") ?? BackupRoot;

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
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BackupRoot };
        if (Layout is { } l3) foreach (var r in l3.OnlineRootPaths) all.Add(r);
        foreach (var r in all)
            try { if (!string.IsNullOrEmpty(r)) { var part = PartialPathFor(r, f); if (File.Exists(part)) File.Delete(part); } }
            catch { /* in use or unreadable: left for the sweep */ }
    }

    private async Task<HttpResponseMessage> SendWithStallTimeoutAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new StallException($"No response from the download server within {StallTimeout.TotalSeconds:F0} s");   // a network failure, not Other
        }
    }

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

    private static readonly object _tmpDirGate = new();
    private static void RemoveTmpDirIfEmpty(string dir)
    {
        lock (_tmpDirGate) Volumes.FolderPruner.DeleteIfEmpty(dir);
    }

    public int SweepOrphanPartials() => SweepOrphanPartials(null);

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
            foreach (var r in roots) keep.Add(RootKeyedPartName(r, o));  
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
                    try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < TimeSpan.FromMinutes(10)) continue; } catch { continue; }
                    try { File.Delete(f); n++; } catch { /* in use or gone: leave it */ }
                }
                RemoveTmpDirIfEmpty(dir);
            }
            catch { /* unreadable root: nothing to sweep */ }
        }
        return n;
    }

    internal static string RelPath(string relBase, string fullPath)
        => Path.GetRelativePath(relBase, fullPath).Replace('\\', '/');

    private static string StablePartName(Grog.Core.Models.GameFile f)
        => StableTempName($"part|{f.GameGogId}|{f.FileKey}");
    private static string RootKeyedPartName(string rootBase, Grog.Core.Models.GameFile f)
        => StableTempName($"{rootBase}|{f.GameGogId}|{f.FileKey}");

    public static string PartNameFor(Grog.Core.Models.GameFile f) => StablePartName(f);

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
        return Convert.ToHexString(hash, 0, 8) + ".part";   
    }

    public const string OldVersionsFolder = "Old Versions";

    public bool KeepOldVersions { get; set; }

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

    private string? CurrentCopyPath(DownloadTask task)
    {
        var prevRel = task.File.LocalRelativePath;
        if (string.IsNullOrWhiteSpace(prevRel)) return null;
        var oldBase = (task.File.RootId is { } orid ? Layout?.RootPath(orid) : null) ?? BackupRoot;
        var prev = Path.Combine(oldBase, prevRel!);
        return File.Exists(prev) ? prev : null;
    }

    internal Action<string, string>? BeforeFinalMove;

    internal const string HeldOldBuildSuffix = ".grogold";

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

    private void ArchiveHeldBuild(DownloadTask task, string held, string finalPath, string relBase, string? rootId)
    {
        try { ArchiveOldVersion(task, held, relBase, task.GameTitle, rootId, Path.GetFileName(finalPath)); }
        catch { /* best effort: archiving the old build must never fail the new download */ }
    }

    private void ArchiveOldVersion(DownloadTask task, string prev, string relBase, string gameTitle, string? rootId, string? archiveAs = null)
    {
        // Archived on the drive it ALREADY sits on: an update that spilled to another drive dragged
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

internal sealed class BackoffException : Exception
{
    public TimeSpan? RetryAfter { get; }
    public BackoffException(TimeSpan? retryAfter) : base("Server asked to slow down.") => RetryAfter = retryAfter;
}

internal sealed class RateLimiter
{
    private readonly Func<long> _bytesPerSec;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    private long _bytesSinceReset;
    private long _lastRate;

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

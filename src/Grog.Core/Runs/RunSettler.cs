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
    private readonly object _notifyGate = new();
    private DateTime _lastSaveUtc = DateTime.MinValue;

    public int Completed { get; private set; }
    public int Failed => _failedKeys.Count;
    private readonly HashSet<(long, string)> _failedKeys = new();
    public int Retrying { get; private set; }
    public int Refused { get; private set; }
    public int Skipped { get; private set; }
    public int NoRoom { get; private set; }
    public int DriveOffline { get; private set; }
    private readonly HashSet<(long, string)> _driveOfflineKeys = new();
    public bool CountedDriveOffline(long gogId, string fileKey) => _driveOfflineKeys.Contains((gogId, fileKey));

    public Sync.Scope? ClearNewScope { get; set; }

    public event Action<DownloadTask, DownloadTaskState>? Transition;

    public RunSettler(IManifestStore manifest, DownloadEngine engine, RunJournal? journal, IBackupHost host,
                      TimeSpan? checkpointEvery = null)
    {
        _manifest = manifest; _engine = engine; _journal = journal; _host = host;
        _checkpointEvery = checkpointEvery ?? TimeSpan.FromSeconds(10);
        _loop = Task.Run(LoopAsync);
        _engine.TaskChanged += OnTaskChanged;
    }

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

    public async Task SettledAsync()
    {
        var barrier = SettledBarrier();
        await Task.WhenAny(barrier, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
    }

    private Task SettledBarrier()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_stop.IsCancellationRequested) { tcs.TrySetResult(); return tcs.Task; }   // disposed: nothing will settle
        lock (_barrierLock) _barriers.Add((Interlocked.Increment(ref _requestedGen), tcs));
        Interlocked.Exchange(ref _dirty, 1);
        try { _wake.Release(); } catch (ObjectDisposedException) { tcs.TrySetResult(); }
        return tcs.Task;
    }

    private void SettlePass()
    {
        UiThreadGuard.NotOnUi("a settle pass");
        bool changed = false;
        var notify = new List<(DownloadTask Task, DownloadTaskState Prev, DownloadSettlement.Outcome? Outcome)>();
        var journal = new List<Action<RunJournal>>();
        lock (_gate)
        using (_manifest.Gate.Enter())   
        {
            _manifest.Gate.AssertHeld();   
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
                    { var (n, b, st, e) = (x.ResolvedFileName ?? x.File.Name, x.BytesReceived, x.State.ToString(), x.Error);
                      journal.Add(j => j.FileNotCounted(n, "canceled", b, st, e)); }
                    notify.Add((x, prev, DownloadSettlement.Outcome.NotFinished));
                    continue;
                }

                var m = _manifest.Current;
                var file = m.ItemById(x.File.GameGogId)?.Files.Find(f => f.FileKey == x.File.FileKey);
                var (outcome, manifestChanged) = DownloadSettlement.Settle(m, x, file);
                changed |= manifestChanged;
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
                    case DownloadSettlement.Outcome.Unavailable:
                        { var r = x.UnavailableReason; journal.Add(j => j.FileNotCounted(jname, "unavailable", jbytes, jstate, r)); break; }
                    case DownloadSettlement.Outcome.DriveOffline:
                        { var r = x.Error; journal.Add(j => j.FileNotCounted(jname, "drive offline", jbytes, jstate, r)); break; }
                    case DownloadSettlement.Outcome.NoRoom:
                        { var r = x.Error; journal.Add(j => j.FileNotCounted(jname, "no room", jbytes, jstate, r)); break; }
                    case DownloadSettlement.Outcome.Skipped:
                        { var r = x.SkipReason; journal.Add(j => j.FileNotCounted(jname, "skipped", jbytes, jstate, r)); break; }
                    case DownloadSettlement.Outcome.NotFinished:
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

        if (notify.Count > 0)
            lock (_notifyGate)
                foreach (var (task, prev, outcome) in notify)
                {
                    try { Transition?.Invoke(task, prev); } catch (Exception ex) { try { _host.Log($"Settle notify failed: {ex.Message}"); } catch { } }
                    if (outcome is { } o)
                        try { _host.TaskSettled(task, o); } catch (Exception ex) { try { _host.Log($"Settle notify failed: {ex.Message}"); } catch { } }
                }
        if (changed) RequestCheckpoint();
    }

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

    internal TimeSpan CheckpointEvery => _checkpointEvery;

    internal TimeSpan FlushBound { get; set; } = TimeSpan.FromSeconds(10);

    public bool HasUnsettledWork => Volatile.Read(ref _unsettled) != 0;
    private int _unsettled;

    public async Task FlushAsync()
    {
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

        try { _loop.Wait(TimeSpan.FromSeconds(1)); } catch { /* the loop logs its own failures */ }

        List<TaskCompletionSource> left;
        lock (_barrierLock) { left = _barriers.Select(b => b.Done).ToList(); _barriers.Clear(); }

        foreach (var d in left) d.TrySetResult();
        if (_loop.IsCompleted) { _wake.Dispose(); _stop.Dispose(); }
    }
}

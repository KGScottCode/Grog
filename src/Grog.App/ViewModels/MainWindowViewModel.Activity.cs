// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Download;
using Grog.Core.Format;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Verify;

// Activity (session orchestration): the derived-activity map and the engine progress feed, run control
// (Pause / Clear / Resume), the log file and Log(), per-file inventory actions, the activity slot. (The queue
// pane, the log list and the toast moved to OverviewViewModel.Activity in S3.3.)
public partial class MainWindowViewModel
{
    // The single derived-Activity map: every "what's happening" surface reads this, computed from the
    // persisted queues + live engine. Sole writer of per-row activity, so navigation/restart cannot desync.
    private IReadOnlyDictionary<Grog.Core.Sync.FileRef, Grog.Core.Sync.FileActivity> _activityMap
        = new Dictionary<Grog.Core.Sync.FileRef, Grog.Core.Sync.FileActivity>();
    /// <summary>The library's detail rows read the same map when they are rebuilt.</summary>
    internal IReadOnlyDictionary<Grog.Core.Sync.FileRef, Grog.Core.Sync.FileActivity> ActivityMap => _activityMap;

    private IEnumerable<Grog.Core.Volumes.ReorgMove> CurrentMoves()
        => Storage.ActiveReorgJob?.Moves ?? System.Linq.Enumerable.Empty<Grog.Core.Volumes.ReorgMove>();
    private bool MoveQueuePaused => Storage.ActiveReorgJob?.Paused ?? false;

    /// <summary>Re-derive activity for every file and push it to every surface. Cheap and idempotent;
    /// called at the end of each Action and on navigation. Reads facts, writes only derived display.</summary>
    internal void RecomputeActivity() => Services.UiWatch.Time("RecomputeActivity", RecomputeActivityCore);
    private void RecomputeActivityCore()
    {
        if (_manifest is null) return;
        _activityMap = Grog.Core.Sync.ActivityMap.Derive(
            _manifest.Current.Downloads, _liveEngine?.Snapshot, CurrentMoves(), MoveQueuePaused);

        // Only games with queue/move activity now, or last pass (must reset to Idle), can change their dot;
        // update just those, not all ~600 rows -- every other game is Idle->Idle and SetActivity is a no-op.
        var activeGames = new HashSet<long>();
        foreach (var k in _activityMap.Keys) activeGames.Add(k.GogId);
        var toUpdate = new HashSet<long>(activeGames);
        toUpdate.UnionWith(_prevActiveGames);
        foreach (var gid in toUpdate)
            if (Library._gameById.TryGetValue(gid, out var g) && g.Item is not null)
                g.SetActivity(Grog.Core.Sync.ActivityMap.ForGame(gid, g.Item.Files, _activityMap));
        _prevActiveGames = activeGames;

        foreach (var d in Library.DetailFiles)
            d.SetActivity(Grog.Core.Sync.ActivityMap.ForFile(
                new Grog.Core.Sync.FileRef(d.GogId, d.File.FileKey), _activityMap));
        Library.RaiseSectionDone();   // section "download all" buttons follow the files' queued/done state

        Overview.RebuildQueue();   // incremental; sort is applied on enqueue/click, not here
        // While an engine is live the 1 Hz progress tick reseeds the rollup (O(in-flight) on a valid base, one
        // full walk after a transition); a second full Rollups.Library here per transition was the same walk
        // twice (UI-thread sweep 09-06 r2). Idle, this is the only path, so it still runs.
        if (_liveEngine is null) Library.RaiseLibraryProgress();
        Library.RefreshBackUpMenuCounts();   // keep the right-click submenu counts fresh as files finish/queue
    }

    /// <summary>Handle one engine progress event -- the only place download progress touches the UI (both paths).
    /// Byte updates are lightweight; a STATE transition settles the persisted queue and re-derives every surface.</summary>
    private long _lastDriveProbe;   // throttle for the in-flight folder-bar refresh below
    private string? _lastPillPhase;

    private void OnDownloadProgress(DownloadEngine engine, DownloadTask t, Dictionary<Guid, DownloadTaskState> lastState)
        => Services.UiWatch.Time("OnDownloadProgress", () => OnDownloadProgressCore(engine, t, lastState));
    private void OnDownloadProgressCore(DownloadEngine engine, DownloadTask t, Dictionary<Guid, DownloadTaskState> lastState)
    {
        // Persist the partial byte count (memory each tick; saved on pause/stop/complete) so bars stay
        // accurate while paused and instant on resume.
        // Only once Core has a .part for this file (it sets HasPartial on the first write): before that, BytesReceived
        // is the resumed offset of a .part the engine may have just discarded, and writing it back revived a
        // partial that no longer existed (QA 09-30 A10). HasPartial is Core's fact, never set here.
        if (t.State == DownloadTaskState.Active && t.BytesReceived > 0 && t.File.HasPartial && t.RoomChecked)
            t.File.PartialBytes = t.BytesReceived;

        // One snapshot, one pass per tick: speed sum, in-flight bytes, any-active and the per-file lookup together.
        var snap = engine.Snapshot;
        double speedSum = 0; long inFlightBytes = 0; bool anyActive = false;
        long unsettledBytes = 0;   // finished on the worker, not yet settled on this thread: still "moving" for the meter
        var inFlightMap = new Dictionary<(long, string), long>();
        var inflightFiles = new List<(GameFile File, long Live)>();   // the only files whose bytes changed this tick
        foreach (var x in snap)
        {
            if (x.State == DownloadTaskState.Active) { speedSum += x.BytesPerSecond; inFlightBytes += x.BytesReceived; anyActive = true; }
            else if (x.State == DownloadTaskState.Completed && !_settledTaskIds.Contains(x.Id)) unsettledBytes += x.BytesReceived;
            if (x.State is DownloadTaskState.Active or DownloadTaskState.Pending && x.BytesReceived > 0)
            {
                inFlightMap[(x.File.GameGogId, x.File.FileKey)] = x.BytesReceived;
                inflightFiles.Add((x.File, x.BytesReceived));
            }
        }
        Grog.Core.Sync.Rollups.InFlightBytes inFlight = f => inFlightMap.TryGetValue((f.GameGogId, f.FileKey), out var b) ? b : 0;

        // Live byte readouts (cheap; every tick). O(1) row lookup via the index.
        if (Library._gameById.TryGetValue(t.File.GameGogId, out var grow) && grow.Item is not null)
        {
            grow.RowProgress = Grog.Core.Sync.Rollups.GameFilePercent(grow.Item, Library.EffectiveScope, inFlight);   // the bar's own unit: files
            grow.RaiseCompletion();
        }
        var dfr = Library.DetailFiles.FirstOrDefault(d => ReferenceEquals(d.File, t.File));
        if (dfr is not null && t.State == DownloadTaskState.Active) dfr.DownloadPercent = t.PercentComplete;
        // EVERY active row, not just the event's task: with coalescing only the latest event arrives, and
        // with simultaneous downloads > 1 the other in-flight rows must keep their readouts live too.
        foreach (var qr in Overview.ActiveRows) qr.Refresh();
        // The pill's eyebrow carries the phase (DOWNLOADING -> WRITING TO DISK -> VERIFYING -> SAVING) and
        // must follow every tick, not only a pill swap, or the phase never shows on the one row people watch.
        if (Overview.DownloadPill?.Task.FinishingPhase != _lastPillPhase) { _lastPillPhase = Overview.DownloadPill?.Task.FinishingPhase; Overview.RaisePillState(); }

        DownloadSpeedBytes = speedSum;
        // Feed the throughput meter a monotonic run-cumulative byte count (done + in-flight partials).
        // "Active" for the meter means THE RUN is moving, not that a task happens to be mid-flight on this
        // tick. anyActive alone goes false in the gap between two files, and the meter treats that as idle:
        // it clears its rolling window and zeroes the rate. With small files nearly every tick lands in such
        // a gap, so the speed read "--" and the graph drew isolated spikes over a flat zero. Pausing still
        // reads idle, so a pause never counts as active time in the average.
        bool runMoving = anyActive || (!DownloadPaused && !Overview.QueueIsEmpty);
        // The meter's cumulative is THIS RUN's bytes: settled + in flight + finished-but-unsettled. It used to be
        // the library tally plus in-flight, and at every file end the in-flight part dropped at once while the
        // tally caught up a dashboard pass later: the count dipped (meter rebased) and then the whole file
        // landed in one tick, drawn as a spike of file-size-over-tick that no link ever delivered (owner 09-08).
        // Settle moves a file's bytes from the unsettled term into _runBytesSettled in the same UI callback,
        // so the sum is continuous across every file boundary.
        _meter.Sample(DateTime.UtcNow, _runBytesSettled + inFlightBytes + unsettledBytes, runMoving, transferring: anyActive);
        // Plot the METER, not speedSum: speedSum is the instantaneous sum over tasks that are Active right
        // now, so a run of small files (each finishing inside a tick) samples zero again and again and the
        // graph flatlines while bytes are plainly moving. The meter reads a rolling window over cumulative
        // bytes, so it survives file boundaries -- and it is what the SPEED readout above already shows.
        Overview.SampleSpeedGraph(_meter.CurrentBytesPerSec);   // ~1 Hz internally; cheap
        // Folder bars during a LONG file: free space falls while a big installer is still in flight, so
        // re-probe the drives every few seconds rather than only when a file settles. DriveInfo is a
        // syscall, not a scan - 5 s keeps it invisible.
        var nowTicks = System.Environment.TickCount64;
        if (nowTicks - _lastDriveProbe >= 1000)
        {
            _lastDriveProbe = nowTicks;
            RaiseDashboard();   // includes RaiseHealth (via RaiseState); probes the drives: free space falls while a big installer
                                // is mid-flight. 1 Hz, same cadence as the Backup Task card, so the two never visibly disagree.
        }

        Overview.RaiseQueueStats();
        Overview.RaiseRunStats();
        Library.RaiseLibraryProgress(inflightFiles);   // O(in-flight): cached base + this tick's in-flight files

        // State transitions = triggers. SCAN THE WHOLE SNAPSHOT, not just the task this call carries:
        // TaskChanged events are coalesced (250ms, owner call 09-01), so intermediate tasks' events are
        // dropped by design -- a per-t check here left every file completing inside a window UNSETTLED
        // (stuck in the queue, manifest not updated; owner-hit same day). The lastState dict makes each
        // transition fire exactly once no matter which event triggered the pass.
        bool anyTransition = false;
        foreach (var x in snap)
        {
            lastState.TryGetValue(x.Id, out var prev);
            if (prev == x.State) continue;
            lastState[x.Id] = x.State;
            anyTransition = true;
            Library.InvalidateRollupBase();   // in-flight/present state changed; next progress call rebuilds once
            // The ETA's cost model: wall time per file, from first byte to settled.
            if (x.State == DownloadTaskState.Active) _meter.FileStarted(x.Id);
            else if (x.State == DownloadTaskState.Completed)
            {
                // Pending at the last pass, done at this one: the whole file fit inside one coalesced tick.
                if (prev == DownloadTaskState.Pending) _meter.FileStartedSincePreviousSample(x.Id);
                _meter.FileCompleted(x.Id, x.BytesReceived);
            }

            // Settlement (record, journal, checkpoint, tallies, log line) is Core's RunSettler + the host now.
            // (09-22) The row's STATUS is re-derived only in RaiseCompletion, and above it ran for the event's task
            // alone: a file that completed inside the same coalesced tick as another task's event left its game
            // reading Partial with a full green bar and 5/5 beside it (owner-hit, walk pass 4). Every transition
            // refreshes its own game's row.
            if (x.State == DownloadTaskState.Completed && Library._gameById.TryGetValue(x.File.GameGogId, out var doneRow) && doneRow.Item is not null)
            {
                doneRow.RowProgress = Grog.Core.Sync.Rollups.GameFilePercent(doneRow.Item, Library.EffectiveScope, inFlight);
                doneRow.RaiseCompletion();
            }
        }
        if (!anyTransition) return;
        Settings.ApplyAutoConcurrency(engine);   // Auto: the queue head just moved; re-pick the worker count
        RecomputeActivity();   // re-derive dots + queue (incremental, cheap)
    }

    // ---- Download run control (Pause / Stop / Resume) -- shared by the CTA and the Activity pane.
    // Works for both download paths (bulk + per-file); Pause persists across a restart.
    [ObservableProperty] private bool _downloadRunning;   // a download is actively running (either path)
    [ObservableProperty] private bool _downloadPaused;    // paused by the user (persisted); shows Resume

    // Show whenever there's queued work (even an idle-after-restart queue), so Stop/Resume stay reachable.
    public bool ShowDownloadControls => Overview.HasQueuedWork || DownloadRunning || DownloadPaused;
    public bool CanPauseDownload => Overview.HasQueuedWork && DownloadRunning && !DownloadPaused;
    partial void OnDownloadRunningChanged(bool value)
    {
        Overview.RaisePillState();   // STARTING <-> UP NEXT follows the run flag
        RaiseDownloadControls();     // the pane's Pause/Resume pair keys on the run flag too (09-17)
        Overview.RaiseRunStats();    // the lede's clock and the meter readouts stop with the run
        RaiseBarState();
        Overview.RaiseRailOverviewSub();   // (09-22) "downloading N files" follows the run flag
        Storage.RaiseInventory();    // (09-19) the Move buttons are live during a backup, dead during a scan: they key on this flag too
    }
    partial void OnDownloadPausedChanged(bool value)
    {
        Overview.RaiseRailOverviewSub();   // (09-22) "paused, N files queued"
        RaiseDownloadControls();
        Overview.RaisePillState();
        RaiseBarState();
    }

    /// <summary>The download queue's pause/resume button states as ONE set (queue rebuild + pause flip): the
    /// shell's own pair here, the queue pane's trio on the Overview.</summary>
    internal void RaiseDownloadControls()
    {
        OnPropertyChanged(nameof(QueueNeedsAccount));
        OnPropertyChanged(nameof(ShowDownloadControls)); OnPropertyChanged(nameof(CanPauseDownload));
        Overview.RaiseDownloadControls();
    }

    /// <summary>Pause the queue: flip its persisted Paused fact and stop the current transfer (partial kept).
    /// Membership is untouched, so every surface derives "Paused" from the queue, on any screen or restart.</summary>
    /// <summary>(09-19) "Finish current files": the clean stop. The files transferring finish, nothing new starts,
    /// no partial is made, and the queue ends Paused. Pause stays live as "stop right now". Core owns the state.</summary>
    internal readonly Grog.Core.Runs.RunDrain Drain = new();
    internal bool FinishingCurrent => Drain.Finishing;
    internal bool PausedAfterFinish => Drain.PausedAfterFinish;
    internal bool CanDrain => Drain.CanRequest && DownloadRunning && !DownloadPaused;
    internal string FinishingLabel
    {
        get
        {
            int n = Drain.InFlight;
            return n > 0 ? $"Finishing {Grog.Core.Format.Plural.Of(n, "file")}…" : "Finishing…";
        }
    }
    [RelayCommand]
    private void FinishCurrentFiles()
    {
        if (!CanDrain || !Drain.Request()) return;
        Log($"Finishing the {Grog.Core.Format.Plural.Of(Drain.InFlight, "file")} in progress; nothing new will start. Pause still stops right now.", category: LogCategory.Download);
        RaiseBarState(); Overview.RaiseRunStats();
    }
    [RelayCommand]
    private void KeepDownloading()
    {
        if (!Drain.Cancel()) return;   // too late: the files already landed and the run is ending as a pause
        Log("Keep downloading: the queue carries on.", category: LogCategory.Download);
        RaiseBarState(); Overview.RaiseRunStats();
    }
    internal void DrainEngineReady(DownloadEngine engine) => Drain.EngineReady(engine);

    /// <summary>(09-25, owner) The files a paused queue has part-way down (a partial on disk) that still fit: what
    /// "Finish current files" finishes from a pause. Read from the manifest, so it holds across a restart too.</summary>
    internal IReadOnlySet<(long, string)> PausedInProgress()
    {
        var set = new HashSet<(long, string)>();
        if (_manifest is null || !DownloadPaused) return set;
        // Under the gate, one index: O(queue + items), never a walk per queued file (review 09-25).
        return _manifest.Read(m =>
        {
            var byId = new Dictionary<long, Grog.Core.Models.LibraryItem>();
            foreach (var it in m.Items) byId.TryAdd(it.GogId, it);
            foreach (var q in m.Downloads.Snapshot())
                if (q.Fits && byId.TryGetValue(q.GogId, out var it)
                    && it.Files.FirstOrDefault(f => f.FileKey == q.FileKey) is { HasPartial: true, PartialBytes: > 0 })
                    set.Add((q.GogId, q.FileKey));
            return set;
        });
    }
    /// <summary>How many files a paused queue has part-way down; refreshed once per bar raise, read by the bar.</summary>
    private int _pausedInProgressCount;
    internal void RefreshPausedInProgress() => _pausedInProgressCount = DownloadPaused ? PausedInProgress().Count : 0;

    /// <summary>Paused, then "Finish current files": resume ONLY the files in progress; the queue pauses again when
    /// they land, exactly as a running "Finish current files" ends. "Keep downloading" opens the whole queue.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task FinishPausedFiles()
    {
        if (!RequireAccount("finish the files in progress")) return;
        var files = PausedInProgress();
        if (files.Count == 0) return;
        Drain.ArmFinishOnly(files);
        Log($"Finishing the {Grog.Core.Format.Plural.Of(files.Count, "file")} in progress; nothing new will start. Pause still stops right now.", category: LogCategory.Download);
        // The arm is for THIS Resume's run: if the Resume bails before its run begins, it must not ride along with a
        // later Back up or scheduled run (review 09-25). A run that began has consumed it already.
        try { await ResumeDownloadsCommand.ExecuteAsync(null); }
        finally { Drain.Disarm(); }
    }
    internal void BackupRunBegan() { Drain.RunBegan(); lock (_heldLogged) _heldLogged.Clear(); }
    private readonly HashSet<(long, string)> _heldLogged = new();
    /// <summary>True the first time a file is held in this run (the log says it once).</summary>
    internal bool NoteHeldOnce(long gogId, string fileKey) { lock (_heldLogged) return _heldLogged.Add((gogId, fileKey)); }
    internal void BackupRunOver() => Drain.RunOver();

    /// <summary>Pause the queue: flip its persisted Paused fact and stop the current transfer (partial kept).
    /// Membership is untouched, so every surface derives "Paused" from the queue, on any screen or restart.</summary>
    [RelayCommand]
    internal void PauseDownloads()
    {
        if (_manifest is null || _manifest.Current.Downloads.IsEmpty) return;
        // Already paused: nothing to stop, and a second "Downloads paused" line read as two pauses (a shutdown
        // pause after the button, measured 09-18 12:04:39).
        if (DownloadPaused) return;
        _stopRequested = true;
        SetDownloadPaused(true);
        _cts?.Cancel(); _downloadCts?.Cancel();
        // The coalescer stops sampling once paused, so the meter would hold the last live rate under PAUSED
        // (speed, ETA, graph frozen at full tilt). One idle sample zeroes it (owner-hit class, 09-04).
        _meter.MarkIdle();
        DownloadSpeedBytes = 0;
        Log("Downloads paused. Partial files are kept -- Resume to continue where it left off.", category: LogCategory.Download);
        RecomputeActivity();
        Overview.RaiseRunStats();
        // The settle coalescer's trailing pass is skipped once _stopRequested is set: raise the health tally and
        // dashboard now so files that landed in the last second are counted (final check 09-06). RaiseDashboard
        // includes the storage rows.
        Overview.RaiseHealth(); RaiseDashboard();
        // The in-run inventory coalescer skips its trailing rebuild too, so the files that landed in the last
        // two seconds would be missing from the Storage grids (review 09-06).
        if (CurrentView == View.Drives) Storage.RebuildInventory();
    }

    /// <summary>Mirror the queue's Paused fact onto the bindable flag + persist it. One place sets both,
    /// so they can't drift.</summary>
    private void SetDownloadPaused(bool paused)
    {
        if (_manifest is not null)
        {
            _manifest.Mutate(m => m.Downloads.Paused = paused);   // (manifest gate 09-08)
            _manifest.SaveSoon();   // a flag: debounced, Pause/Resume taps coalesce
        }
        DownloadPaused = paused;
        QueueRow.QueuePaused = paused;
        if (!paused) Storage.ForgetDrivePause();   // (review 09-25) a Resume or a fresh run: a later Pause is the user's, never Grog's
        foreach (var r in Overview.DownloadTail.OfType<QueueRow>()) r.Refresh();   // every in-flight row flips with the button, not with its worker
        Overview.DownloadPill?.Refresh();
    }

    /// <summary>Clearing throws away queue ORDER and any hand-picked selection, which can be minutes of
    /// work to rebuild -- so the queue's Clear asks first. The bytes are never at risk (partials stay), and
    /// the dialog says so rather than implying danger it does not have.</summary>
    [ObservableProperty] private bool _showClearQueueConfirm;

    // ---- Out of space: files planned to land nowhere ----
    // Counted at placement time, not from download failures: a file with no room is never attempted, so
    // there is no failure to count. Both dialogs read from the same two numbers.
    private int _heldNoSpaceCount;
    private long _heldNoSpaceBytes;

    /// <summary>End-of-run summary: some files fit, these did not.</summary>
    [ObservableProperty] private bool _showNoSpaceSummary;
    /// <summary>Nothing fit at all -- shown INSTEAD of starting a run (the Resume-on-a-full-drive case).</summary>
    [ObservableProperty] private bool _showNoSpaceAtAll;

    public string NoSpaceCountText => $"{_heldNoSpaceCount}";
    public string NoSpaceBytesText => Grog.Core.Format.ByteFormat.Size(_heldNoSpaceBytes);
    public string NoSpaceSummaryLine =>
        $"{Grog.Core.Format.Plural.Of(_heldNoSpaceCount, "file")} ({NoSpaceBytesText}) had no room on any "
        + "storage and were not downloaded.";

    [RelayCommand] private void DismissNoSpaceSummary() => ShowNoSpaceSummary = false;
    [RelayCommand] private void DismissNoSpaceAtAll() => ShowNoSpaceAtAll = false;

    /// <summary>Both dialogs point at the one place that fixes this: add or free space on a folder.</summary>
    [RelayCommand]
    private void ManageFoldersFromNoSpace()
    {
        ShowNoSpaceSummary = false;
        ShowNoSpaceAtAll = false;
        Navigate("Drives");   // the rail's Folders page; "Drives" is its navigation key
    }

    /// <summary>Push the freshly-counted numbers at the dialogs before either is shown.</summary>
    private void RaiseNoSpaceText()
    {
        OnPropertyChanged(nameof(NoSpaceCountText));
        OnPropertyChanged(nameof(NoSpaceBytesText));
        OnPropertyChanged(nameof(NoSpaceSummaryLine));
    }

    [RelayCommand] private void RequestClearQueue() => ShowClearQueueConfirm = true;
    [RelayCommand] private void DismissClearQueueConfirm() => ShowClearQueueConfirm = false;

    [RelayCommand]
    private void ConfirmClearQueue()
    {
        ShowClearQueueConfirm = false;
        CancelDownloads();
    }

    /// <summary>Stop: clear the download queue (the action) and stand the engine down. Partials stay on
    /// disk (nothing is deleted).</summary>
    [RelayCommand]
    internal void CancelDownloads()
    {
        // Capture BEFORE tearing the run down: once the queue is emptied and the engine dropped, nothing
        // says a run was under way, and Stop on an already-idle app must not stamp an outcome.
        bool wasRunning = Overview.RunActive;
        _stopRequested = true;
        // One batched cancel: a per-task loop raised two events PER FILE, each running a full settle pass and
        // a full queue rebuild (measured 09-10 at 1100 files: 2200 events, 1.2M snapshot rows walked).
        _liveEngine?.CancelRange(
            _liveEngine.Snapshot.Where(t => t.State is DownloadTaskState.Active or DownloadTaskState.Pending).ToList());
        while (_fileQueue.Count > 0) { _fileQueue.Dequeue(); }
        if (_manifest is not null)
        {
            _manifest.Mutate(m => m.Downloads.Clear());   // empties items + clears Paused (manifest gate 09-08)
            SaveInBackground("queue clear");
        }
        SetDownloadPaused(false);   // the one mutator: also forgets Grog's drive pause
        Storage.ForgetDriveWait();  // an empty queue waits for no drive: the disconnected card goes with it
        _cts?.Cancel(); _downloadCts?.Cancel();
        _liveEngine = null;
        // The run is over: reset the session meter, or a surviving meter reads 100% (SessionBytes/
        // (SessionBytes+0 remaining) = 1) and leaves a stale avg-speed readout under an idle card.
        ResetRunCounters();
        RecomputeActivity();
        Overview.RaiseRunStats();
        // The storage rows carry "queued here" from the last in-flight rebuild; with the queue gone they
        // must be rebuilt NOW, not on the next navigation (owner saw a phantom queued segment, 09-04).
        Overview.RaiseHealth(); RaiseDashboard();   // RaiseDashboard rebuilds the storage rows
        if (CurrentView == View.Drives) Storage.RebuildInventory();   // the coalescer's trailing rebuild is skipped after a stop
        Log("Download queue cleared. The current file was stopped; partial files stay on disk (nothing is deleted).", category: LogCategory.Download);
        if (wasRunning) Schedule.RecordRunOutcome(Grog.Core.Scheduling.ScheduleRunOutcome.Canceled);
        Overview.MarkLastRunDirty();
        Overview.RaiseHealth();   // every failed file just left the queue: the card counts them again (QA 09-30 C7)
    }

    /// <summary>Transient: true from Resume click until the run marks itself Running (or fails to start).
    /// Holds the primary button on its Running look through that async gap.</summary>
    private bool _resuming;

    /// <summary>Resume the paused download queue: clear the Paused fact and re-run the executor over the
    /// remaining queued files (they resume from their on-disk partials via HTTP range).</summary>
    private bool _resumeWaiting; private int _resumeGen;
    /// <summary>A Resume that found the app busy with a scan or verify: fired again once Busy drops.</summary>
    private bool _resumeWhenIdle;
    private void ResumeIfArmed()
    {
        if (!_resumeWhenIdle || Busy) return;
        _resumeWhenIdle = false;
        if (DownloadPaused && ResumeDownloadsCommand.CanExecute(null))
            Dispatcher.UIThread.Post(() => ResumeDownloadsCommand.Execute(null));   // off this Busy-changed stack
        else _resuming = false;
    }
    // Concurrent: the command awaits the whole run it starts, so without this Resume greyed out (and the app's own
    // resume after a move was refused by CanExecute) until that run ended, even after a Pause (review 09-22).
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ResumeDownloads()
    {
        if (!RequireAccount("resume downloads")) return;
        if (_resumeWaiting) return;   // a second click while the first is waiting out the unwind
        _resumeWaiting = true;
        int gen = ++_resumeGen;
        _resuming = true;
        Drain.Reset();   // resumed: a finished-then-paused queue is running again
        // (09-19) The paused run may still be UNWINDING (flushing partials to a slow drive). Resume is offered the
        // moment Pause is clicked, so wait that out here: started over a live run it was swallowed (Busy), and with
        // the paused flag already cleared the old run's tail reported itself as a finished backup.
        try { for (int i = 0; i < 600 && DownloadRunning; i++) await Task.Delay(100); }
        finally { _resumeWaiting = false; }
        if (DownloadRunning)
        {
            // (09-26) Still unwinding after 60 s: stay paused (starting now would be refused as a second run).
            if (gen == _resumeGen) _resuming = false;
            Log("The paused run is still stopping. Click Resume again in a moment.", category: LogCategory.Download);
            RaiseBarState();
            return;
        }
        if (Busy)
        {
            // Busy for another reason (scan, verify, sign-out): the resume re-arms when that ends instead of giving up.
            _resumeWhenIdle = true;
            Log("Downloads resume when the current task finishes.", category: LogCategory.Download);
            RaiseBarState();
            return;
        }
        SetDownloadPaused(false);   // raises BarState; _resuming holds it on "Running", not "WorkToDo"
        try { await DownloadFiltered(false, fromQueue: true); }   // drain the persisted queue, don't rescan
        finally { if (gen == _resumeGen) _resuming = false; RaiseBarState(); }   // a later Resume owns the bridge now
    }

    // ---- Activity log. Log() appends structured entries to LogEntries (newest first, capped). Health's
    // issue COUNT derives from concrete backup state, not log lines, so info chatter never badges the rail.

    private Grog.Core.Storage.LogFile? _logFile;
    private readonly object _logInit = new();
    /// <summary>The on-disk log. Config dir is always available (it never waits on a backup root), so this
    /// resolves lazily on first use and then stays put. Locked: Log() is called from scan and download
    /// threads, and two concurrent first calls must not build two writers over one file.</summary>
    private Grog.Core.Storage.LogFile LogFileOnDisk
    {
        get
        {
            if (_logFile is { } lf) return lf;
            lock (_logInit)
                // Config dir only: the backup-root legacy probe belongs to the bind path (UI-thread sweep 09-06 r2).
                return _logFile ??= new Grog.Core.Storage.LogFile(
                    _configDir.Length > 0 ? _configDir : Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot).ConfigDir);
        }
    }

    /// <summary>Account (name, alias) pairs for log masking, longest first. Rebuilt on the UI thread by
    /// RefreshAccounts and read from any thread: an immutable array swap, so a background Log() never
    /// enumerates the live manifest Accounts list while the UI mutates it.</summary>
    private volatile (string name, string alias)[] _maskPairs = System.Array.Empty<(string, string)>();
    internal void RebuildMaskPairs()
    {
        var accounts = _manifest?.Current.Accounts;
        if (accounts is null || accounts.Count == 0) { _maskPairs = System.Array.Empty<(string, string)>(); return; }
        var pairs = new System.Collections.Generic.List<(string name, string alias)>();
        for (int i = 0; i < accounts.Count; i++)
        {
            var alias = $"Account_{i + 1:00}";
            foreach (var n in new[] { accounts[i].Username, accounts[i].Login, accounts[i].Id })
                if (!string.IsNullOrWhiteSpace(n)) pairs.Add((n, alias));
        }
        _maskPairs = pairs.OrderByDescending(p => p.name.Length).ToArray();
    }

    public string LogFolderPath => System.IO.Path.GetDirectoryName(LogFileOnDisk.Path) ?? "";

    /// <summary>Replaces every registered account's Username/Login/Id in a log line with a stable
    /// "Account_NN" (manifest order, 1-based), longest name first so "kevin2" never half-matches "kevin".</summary>
    internal string MaskAccountNames(string message)
    {
        var pairs = _maskPairs;
        if (pairs.Length == 0) return message;
        foreach (var (name, alias) in pairs)
        {
            // A username the alias itself contains (e.g. someone literally named "Account") would loop.
            if (alias.Contains(name, System.StringComparison.OrdinalIgnoreCase)) continue;
            int idx;
            while ((idx = message.IndexOf(name, System.StringComparison.OrdinalIgnoreCase)) >= 0)
                message = message[..idx] + alias + message[(idx + name.Length)..];
        }
        return message;
    }

    public void Log(string message, bool isError = false, LogCategory category = LogCategory.General)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var entry = new LogEntry(System.DateTimeOffset.Now, category, isError ? LogSeverity.Error : LogSeverity.Info, message);
        // Persist BEFORE the UI hop: the in-memory list dies with the process, and an unattended 03:00 run is
        // exactly the one nobody is watching. Best-effort and non-throwing by contract.
        // The ON-DISK copy masks account names (owner 08-30): grog.log ends up pasted into public issues,
        // and the GOG username is the one identifying datum in it (same call as the rail scrub). The
        // in-app Activity list keeps real names - it never leaves the machine on its own.
        LogFileOnDisk.Post(entry.Time, category.ToString(), isError, MaskAccountNames(message));   // writer thread: never disk IO on the dispatcher
        // The in-app list (and the issues modal's RECENT ERRORS) lives on the Overview; a line logged before the
        // page view models exist (ctor-time probes) still reaches the file above.
        void Add() => Overview?.AppendLog(entry, isError);
        if (Dispatcher.UIThread.CheckAccess()) Add();
        else Dispatcher.UIThread.Post(Add);
    }

    /// <summary>Open grog.log itself in the machine's text handler; falls back to revealing the folder when
    /// the log does not exist yet (fresh profile).</summary>
    [RelayCommand] private async Task OpenLogFile()
    {
        // (UI-thread sweep 09-06) File.Exists (and the lazy LogFile/config-dir resolve) off the dispatcher.
        var target = await Task.Run(() => System.IO.File.Exists(LogFileOnDisk.Path) ? LogFileOnDisk.Path : LogFolderPath);
        Grog.App.Services.FileManager.Open(target);
    }

    /// <summary>Shipped third-party license texts (glyph data, typefaces, MIT libraries). Rides beside the
    /// executable; falls back to the app folder rather than a dead click if the copy is missing.</summary>
    [RelayCommand] private async Task OpenThirdPartyNotices()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        var exists = await Task.Run(() => System.IO.File.Exists(path));   // (UI-thread sweep 09-06)
        Grog.App.Services.FileManager.Open(exists ? path : AppContext.BaseDirectory);
    }

    /// <summary>The point-in-time GPL-3.0 text shipped with the app -- the copy the user is licensed under.</summary>
    [RelayCommand] private async Task OpenLicenseFile()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "LICENSE");
        var exists = await Task.Run(() => System.IO.File.Exists(path));   // (UI-thread sweep 09-06)
        if (exists) Grog.App.Services.FileManager.Open(path);
        else OpenUrl("https://www.gnu.org/licenses/gpl-3.0.html");   // build without the copy: the canonical text
    }

    // ---- Folders inventory: per-file right-click actions (reveal, verify, re-fetch a single file).

    /// <summary>Absolute path of an inventory row's file, or null when it isn't resolvable (offline root,
    /// or a file with no recorded location yet).</summary>
    private static string? PathOf(Grog.Core.Volumes.BackupLayout layout, InventoryRow? row)
    {
        if (row is null) return null;
        try { return layout.ResolvePath(row.File); }
        catch { return null; }
    }

    /// <summary>(UI-thread sweep 09-06) Resolve the paths of a set of rows off the dispatcher with ONE
    /// BackupLayout (its ctor probes every root on disk). Empty when there is no manifest/root.</summary>
    private Task<System.Collections.Generic.List<string>> PathsOfAsync(System.Collections.Generic.IEnumerable<InventoryRow?> rows)
    {
        var manifest = _manifest; var root = _backupRoot;
        var list = rows.ToList();
        if (manifest is null || string.IsNullOrEmpty(root)) return Task.FromResult(new System.Collections.Generic.List<string>());
        return Task.Run(() =>
        {
            var layout = new Grog.Core.Volumes.BackupLayout(manifest.Current, root);
            return list.Select(r => PathOf(layout, r)).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        });
    }

    private async Task<string?> PathOfAsync(InventoryRow? row)
        => (await PathsOfAsync(new[] { row })).FirstOrDefault();

    /// <summary>Reveal the file's folder. Selects the file itself on Windows, where explorer supports it.</summary>
    [RelayCommand]
    private async Task OpenContainingFolder(InventoryRow? row)
    {
        // (UI-thread sweep 09-06) path resolve + the File.Exists on the backup drive off the dispatcher.
        var path = await PathOfAsync(row);
        if (string.IsNullOrEmpty(path)) { Log("That file has no resolved location yet.", isError: true); return; }
        var exists = await Task.Run(() => System.IO.File.Exists(path));
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            && exists)
        {
            // /select, puts the cursor ON the file rather than just opening the directory.
            try
            {
                var selectPath = path;
                _ = Task.Run(() =>   // ShellExecute off the dispatcher (final check 09-06)
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{selectPath}\"") { UseShellExecute = true }); }
                    catch { Services.FileManager.Open(System.IO.Path.GetDirectoryName(selectPath)); }   // plain reveal
                });
                return;
            }
            catch { /* fall through to plain reveal */ }
        }
        Grog.App.Services.FileManager.Open(System.IO.Path.GetDirectoryName(path));
    }

    /// <summary>Reveal a GAME's folder. A game's files normally share one directory, but the extras-location
    /// setting can split them, so this opens each DISTINCT directory rather than assuming one.</summary>
    [RelayCommand]
    private async Task OpenGameFolder(InventoryGroupRow? group)
    {
        if (group is null) return;
        var paths = await PathsOfAsync(group.Files);   // (UI-thread sweep 09-06) one layout, off the dispatcher
        var dirs = paths.Select(System.IO.Path.GetDirectoryName).Where(d => !string.IsNullOrEmpty(d))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (dirs.Count == 0) { Log("That game has no files in this folder yet.", isError: true); return; }
        foreach (var d in dirs) Grog.App.Services.FileManager.Open(d);
    }

    /// <summary>Copy every resolved path in the game, newline separated.</summary>
    [RelayCommand]
    private async Task CopyGamePaths(InventoryGroupRow? group)
    {
        if (group is null) return;
        var paths = await PathsOfAsync(group.Files);   // (UI-thread sweep 09-06)
        if (paths.Count == 0) { Log("That game has no files in this folder yet.", isError: true); return; }
        var clipboard = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } w2 }
            ? w2.Clipboard : null;
        if (clipboard is null) return;
        try { await clipboard.SetTextAsync(string.Join(Environment.NewLine, paths!)); Log($"Copied {paths.Count} path{(paths.Count == 1 ? "" : "s")}."); }
        catch { /* clipboard can be held by another app */ }
    }

    /// <summary>Re-fetch EVERY file of the game, present ones included. Can cost tens of gigabytes, so the
    /// label carries the size; it only queues, and the run is visible and cancellable, so no modal confirm.</summary>
    [RelayCommand]
    private async Task RedownloadGame(InventoryGroupRow? group)
    {
        if (_manifest is null || group is null) return;
        // (S2.2) Core's FixRun: every file of the game reset for a fresh fetch and saved.
        var r = await Grog.Core.Runs.FixRun.RequeueAsync(_manifest, Grog.Core.Runs.FixSelector.Game(group.GogId));
        Log($"Queued all {r.Count} file(s) of {group.Title} for re-download.", category: LogCategory.Download);
        _ = ReconcileAndRefresh();
    }

    [RelayCommand]
    private async Task CopyFilePath(InventoryRow? row)
    {
        var path = await PathOfAsync(row);   // (UI-thread sweep 09-06)
        if (string.IsNullOrEmpty(path)) { Log("That file has no resolved location yet.", isError: true); return; }
        var clipboard = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } w }
            ? w.Clipboard : null;
        if (clipboard is null) return;
        try { await clipboard.SetTextAsync(path); Log($"Copied path: {path}"); }
        catch { /* clipboard can be held by another app */ }
    }

    /// <summary>Re-check THIS file against disk (presence + size, and MD5 when we hold a checksum).</summary>
    [RelayCommand]
    private async Task VerifyInventoryFile(InventoryRow? row)
    {
        if (Busy || _manifest is null || row is null || Storage.VerifyBlockedByMove()) return;
        ShowToast($"Verifying {row.FileName}…", 0);
        await RunBusy("Verifying…", async () =>
        {
            // (S2.2) Core's VerifyRun over the one file; the per-file verdict line stays this view's, so the
            // run's own summary goes to a silent host.
            var r = (await Grog.Core.Runs.VerifyRun.RunAsync(_manifest, _backupRoot,
                // (09-19) FullRehash: the same words on the game row read the bytes, so this does too.
                Grog.Core.Runs.VerifyRequest.ForFiles(new[] { (row.Item.GogId, row.File.FileKey) }, Grog.Core.Verify.VerifyMode.FullRehash)
                    with { FetchCurrentServerMd5 = ServerChecksumFetcher() },
                new Grog.Core.Runs.NullBackupHost(), _cts?.Token ?? default)).Verify;
            await ReconcileAndRefresh();
            Log($"Verified {row.FileName}: {(r.Missing > 0 ? "missing from disk" : r.Corrupt > 0 ? "corrupt (wrong size or checksum)" : r.Verified > 0 ? "checksum matches" : "size matches (GOG gives no checksum)")}",
                isError: r.Missing > 0 || r.Corrupt > 0, category: LogCategory.Verify);
        });
    }

    /// <summary>Queue this one file for a clean re-fetch. Goes through ManualRetry so a file that had burned
    /// its strikes isn't re-condemned on its very first attempt.</summary>
    [RelayCommand]
    private async Task RedownloadInventoryFile(InventoryRow? row)
    {
        if (_manifest is null || row is null) return;
        // (S2.2) Core's FixRun over the one file (strikes cleared, fresh copy, saved).
        await Grog.Core.Runs.FixRun.RequeueAsync(_manifest, Grog.Core.Runs.FixSelector.File(row.Item.GogId, row.File.FileKey));
        Log($"Queued {row.FileName} for re-download.", category: LogCategory.Download);
        _ = ReconcileAndRefresh();
        Overview.RaiseHealth();
    }

    /// <summary>Refuse a move because one or both drives it needs are offline/missing, and tell the user
    /// which to reconnect. Nothing is started -- a partial move that silently omits stranded files is the
    /// black hole we avoid.</summary>
    /// <summary>A second launch was folded into this instance. Name WHERE this copy runs from: with a
    /// portable copy and an installed one on the same machine the two windows are identical, so "already
    /// running" alone would leave the user unsure which one they are looking at. The running instance
    /// knows its own location, so nothing has to be passed between the processes.</summary>
    [ObservableProperty] private bool _showAlreadyRunning;

    /// <summary>Composed here, not via StringFormat: a comma in a XAML format string is parsed as a
    /// property-list separator (AVLN2000).</summary>
    public string AlreadyRunningText
    {
        get
        {
            var where = Grog.Core.Storage.GrogPaths.PortableInstallDir() is { Length: > 0 } p
                ? p : "your installed copy";
            return $"This window is the copy running from {where}.";
        }
    }

    public void NotifyAlreadyRunning()
    {
        OnPropertyChanged(nameof(AlreadyRunningText));
        ShowAlreadyRunning = true;
    }

    [RelayCommand]
    private void DismissAlreadyRunning() => ShowAlreadyRunning = false;

    /// <summary>The shell's toast route: every partial and page raises through here, the Overview page owns
    /// the toast surface. Kept on the root so a child never needs a sibling to say something.</summary>
    internal void ShowToast(string message, bool isError = true)
        => ShowToast(message, isError ? 2 : 0);
    internal void ShowToast(string message, int severity, ToastFix? fix = null)
        => Overview.ShowToast(message, severity, fix);

    /// <summary>CrashGuard's UI-thread hook: a handled unhandled exception surfaces as a red toast and an
    /// Activity error line, so the user knows something slipped and where the stack went.</summary>
    public void ReportUnhandled(string message)
    {
        try { Log(message, isError: true); ShowToast(message, 2); } catch { /* the crash path never throws */ }
    }

    // ---- Single activity slot: downloads and moves are serialized so they can never race the manifest.
    // Non-blocking: a move runs without the app-wide Busy lock, and pausing a move frees the slot.
    private readonly System.Threading.SemaphoreSlim _activityGate = new(1, 1);

    /// <summary>Take the single activity slot without waiting. Returns false (and tells the user) when a
    /// download or move holds it. Only call <see cref="EndActivitySlot"/> when this returned true.</summary>
    internal bool TryBeginActivitySlot(string couldNotStart)
    {
        if (_activityGate.Wait(0)) return true;
        Dispatcher.UIThread.Post(() =>
            Log($"{couldNotStart} -- a download or move is already running. Pause or finish it first.", isError: true));
        return false;
    }

    internal void EndActivitySlot()
    {
        try { _activityGate.Release(); } catch (System.Threading.SemaphoreFullException) { }
        Storage.StartDeferredMove();   // a move that waited for a scan or verify starts now (posted, never inline)
    }

    /// <summary>Take the slot without waiting and without a message: the move pane defers (never refuses) when
    /// it cannot have it.</summary>
    internal bool TryBeginActivitySlotQuiet() => _activityGate.Wait(0);
}

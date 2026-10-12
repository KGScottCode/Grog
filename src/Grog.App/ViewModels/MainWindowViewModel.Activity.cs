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

public partial class MainWindowViewModel
{
    private IReadOnlyDictionary<Grog.Core.Sync.FileRef, Grog.Core.Sync.FileActivity> _activityMap
        = new Dictionary<Grog.Core.Sync.FileRef, Grog.Core.Sync.FileActivity>();
    internal IReadOnlyDictionary<Grog.Core.Sync.FileRef, Grog.Core.Sync.FileActivity> ActivityMap => _activityMap;

    private IEnumerable<Grog.Core.Volumes.ReorgMove> CurrentMoves()
        => Storage.ActiveReorgJob?.Moves ?? System.Linq.Enumerable.Empty<Grog.Core.Volumes.ReorgMove>();
    private bool MoveQueuePaused => Storage.ActiveReorgJob?.Paused ?? false;

    internal void RecomputeActivity() => Services.UiWatch.Time("RecomputeActivity", RecomputeActivityCore);
    private void RecomputeActivityCore()
    {
        if (_manifest is null) return;
        _activityMap = Grog.Core.Sync.ActivityMap.Derive(
            _manifest.Current.Downloads, _liveEngine?.Snapshot, CurrentMoves(), MoveQueuePaused);

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
        if (_liveEngine is null) Library.RaiseLibraryProgress();
        Library.RefreshBackUpMenuCounts();   // keep the right-click submenu counts fresh as files finish/queue
    }

    private long _lastDriveProbe;   // throttle for the in-flight folder-bar refresh below
    private string? _lastPillPhase;

    private void OnDownloadProgress(DownloadEngine engine, DownloadTask t, Dictionary<Guid, DownloadTaskState> lastState)
        => Services.UiWatch.Time("OnDownloadProgress", () => OnDownloadProgressCore(engine, t, lastState));
    private void OnDownloadProgressCore(DownloadEngine engine, DownloadTask t, Dictionary<Guid, DownloadTaskState> lastState)
    {
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
        foreach (var qr in Overview.ActiveRows) qr.Refresh();
        if (Overview.DownloadPill?.Task.FinishingPhase != _lastPillPhase) { _lastPillPhase = Overview.DownloadPill?.Task.FinishingPhase; Overview.RaisePillState(); }

        DownloadSpeedBytes = speedSum;
        bool runMoving = anyActive || (!DownloadPaused && !Overview.QueueIsEmpty);
        _meter.Sample(DateTime.UtcNow, _runBytesSettled + inFlightBytes + unsettledBytes, runMoving, transferring: anyActive);
        Overview.SampleSpeedGraph(_meter.CurrentBytesPerSec);   // ~1 Hz internally; cheap
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

    [ObservableProperty] private bool _downloadRunning;   // a download is actively running (either path)
    [ObservableProperty] private bool _downloadPaused;    // paused by the user (persisted); shows Resume

    public bool ShowDownloadControls => Overview.HasQueuedWork || DownloadRunning || DownloadPaused;
    public bool CanPauseDownload => Overview.HasQueuedWork && DownloadRunning && !DownloadPaused;
    partial void OnDownloadRunningChanged(bool value)
    {
        Overview.RaisePillState();   // STARTING <-> UP NEXT follows the run flag
        RaiseDownloadControls();     // the pane's Pause/Resume pair keys on the run flag too
        Overview.RaiseRunStats();    // the lede's clock and the meter readouts stop with the run
        RaiseBarState();
        Overview.RaiseRailOverviewSub();   // "downloading N files" follows the run flag
        Storage.RaiseInventory();    // the Move buttons are live during a backup, dead during a scan: they key on this flag too
    }
    partial void OnDownloadPausedChanged(bool value)
    {
        Overview.RaiseRailOverviewSub();   // "paused, N files queued"
        RaiseDownloadControls();
        Overview.RaisePillState();
        RaiseBarState();
    }

    internal void RaiseDownloadControls()
    {
        OnPropertyChanged(nameof(QueueNeedsAccount));
        OnPropertyChanged(nameof(ShowDownloadControls)); OnPropertyChanged(nameof(CanPauseDownload));
        Overview.RaiseDownloadControls();
    }

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

    internal IReadOnlySet<(long, string)> PausedInProgress()
    {
        var set = new HashSet<(long, string)>();
        if (_manifest is null || !DownloadPaused) return set;
        // Under the gate, one index: O(queue + items), never a walk per queued file.
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
    private int _pausedInProgressCount;
    internal void RefreshPausedInProgress() => _pausedInProgressCount = DownloadPaused ? PausedInProgress().Count : 0;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task FinishPausedFiles()
    {
        if (!RequireAccount("finish the files in progress")) return;
        var files = PausedInProgress();
        if (files.Count == 0) return;
        Drain.ArmFinishOnly(files);
        Log($"Finishing the {Grog.Core.Format.Plural.Of(files.Count, "file")} in progress; nothing new will start. Pause still stops right now.", category: LogCategory.Download);
        // The arm is for THIS Resume's run: if the Resume bails before its run begins, it must not ride along with a
        // later Back up or scheduled run. A run that began has consumed it already.
        try { await ResumeDownloadsCommand.ExecuteAsync(null); }
        finally { Drain.Disarm(); }
    }
    internal void BackupRunBegan() { Drain.RunBegan(); lock (_heldLogged) _heldLogged.Clear(); }
    private readonly HashSet<(long, string)> _heldLogged = new();
    internal bool NoteHeldOnce(long gogId, string fileKey) { lock (_heldLogged) return _heldLogged.Add((gogId, fileKey)); }
    internal void BackupRunOver() => Drain.RunOver();

    [RelayCommand]
    internal void PauseDownloads()
    {
        if (_manifest is null || _manifest.Current.Downloads.IsEmpty) return;
        if (DownloadPaused) return;
        _stopRequested = true;
        SetDownloadPaused(true);
        _cts?.Cancel(); _downloadCts?.Cancel();
        _meter.MarkIdle();
        DownloadSpeedBytes = 0;
        Log("Downloads paused. Partial files are kept -- Resume to continue where it left off.", category: LogCategory.Download);
        RecomputeActivity();
        Overview.RaiseRunStats();
        Overview.RaiseHealth(); RaiseDashboard();
        if (CurrentView == View.Drives) Storage.RebuildInventory();
    }

    internal bool PausedSinceLastClose { get; private set; }
    private bool _startupPauseSeen;
    internal void NotePausedAtStartup(bool paused)
    {
        if (_startupPauseSeen) return;
        _startupPauseSeen = true;
        PausedSinceLastClose = paused;
    }

    private void SetDownloadPaused(bool paused)
    {
        PausedSinceLastClose = false;
        if (_manifest is not null)
        {
            _manifest.Mutate(m => m.Downloads.Paused = paused);   // 
            _manifest.SaveSoon();   // a flag: debounced, Pause/Resume taps coalesce
        }
        DownloadPaused = paused;
        QueueRow.QueuePaused = paused;
        if (!paused) Storage.ForgetDrivePause();   // a Resume or a fresh run: a later Pause is the user's, never Grog's
        foreach (var r in Overview.DownloadTail.OfType<QueueRow>()) r.Refresh();   // every in-flight row flips with the button, not with its worker
        Overview.DownloadPill?.Refresh();
    }

    [ObservableProperty] private bool _showClearQueueConfirm;

    private int _heldNoSpaceCount;
    private long _heldNoSpaceBytes;

    [ObservableProperty] private bool _showNoSpaceSummary;
    [ObservableProperty] private bool _showNoSpaceAtAll;

    public string NoSpaceCountText => $"{_heldNoSpaceCount}";
    public string NoSpaceBytesText => Grog.Core.Format.ByteFormat.Size(_heldNoSpaceBytes);
    public string NoSpaceSummaryLine =>
        $"{Grog.Core.Format.Plural.Of(_heldNoSpaceCount, "file")} ({NoSpaceBytesText}) had no room on any "
        + "storage and were not downloaded.";

    [RelayCommand] private void DismissNoSpaceSummary() => ShowNoSpaceSummary = false;
    [RelayCommand] private void DismissNoSpaceAtAll() => ShowNoSpaceAtAll = false;

    [RelayCommand]
    private void ManageFoldersFromNoSpace()
    {
        ShowNoSpaceSummary = false;
        ShowNoSpaceAtAll = false;
        Navigate("Drives");   // the rail's Folders page; "Drives" is its navigation key
    }

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

    [RelayCommand]
    internal void CancelDownloads()
    {
        bool wasRunning = Overview.RunActive;
        _stopRequested = true;
        _liveEngine?.CancelRange(
            _liveEngine.Snapshot.Where(t => t.State is DownloadTaskState.Active or DownloadTaskState.Pending).ToList());
        while (_fileQueue.Count > 0) { _fileQueue.Dequeue(); }
        if (_manifest is not null)
        {
            _manifest.Mutate(m => m.Downloads.Clear());   // empties items + clears Paused
            SaveInBackground("queue clear");
        }
        SetDownloadPaused(false);   // the one mutator: also forgets Grog's drive pause
        Storage.ForgetDriveWait();  // an empty queue waits for no drive: the disconnected card goes with it
        _cts?.Cancel(); _downloadCts?.Cancel();
        _liveEngine = null;
        ResetRunCounters();
        RecomputeActivity();
        Overview.RaiseRunStats();
        Overview.RaiseHealth(); RaiseDashboard();   // RaiseDashboard rebuilds the storage rows
        if (CurrentView == View.Drives) Storage.RebuildInventory();   // the coalescer's trailing rebuild is skipped after a stop
        Log("Download queue cleared. The current file was stopped; partial files stay on disk (nothing is deleted).", category: LogCategory.Download);
        if (wasRunning) Schedule.RecordRunOutcome(Grog.Core.Scheduling.ScheduleRunOutcome.Canceled);
        Overview.MarkLastRunDirty();
        Overview.RaiseHealth();   // every failed file just left the queue: the card counts them again
    }

    private bool _resuming;

    private bool _resumeWaiting; private int _resumeGen;
    private bool _resumeWhenIdle;
    private void ResumeIfArmed()
    {
        if (!_resumeWhenIdle || Busy) return;
        _resumeWhenIdle = false;
        if (DownloadPaused && ResumeDownloadsCommand.CanExecute(null))
            Dispatcher.UIThread.Post(() => ResumeDownloadsCommand.Execute(null));   // off this Busy-changed stack
        else _resuming = false;
    }
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ResumeDownloads()
    {
        if (!RequireAccount("resume downloads")) return;
        if (_resumeWaiting) return;   // a second click while the first is waiting out the unwind
        _resumeWaiting = true;
        int gen = ++_resumeGen;
        _resuming = true;
        Drain.Reset();   // resumed: a finished-then-paused queue is running again
        try { for (int i = 0; i < 600 && DownloadRunning; i++) await Task.Delay(100); }
        finally { _resumeWaiting = false; }
        if (DownloadRunning)
        {
            // Still unwinding after 60 s: stay paused (starting now would be refused as a second run).
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

    
    private Grog.Core.Storage.LogFile? _logFile;
    private readonly object _logInit = new();
    private Grog.Core.Storage.LogFile LogFileOnDisk
    {
        get
        {
            if (_logFile is { } lf) return lf;
            lock (_logInit)
                // Config dir only: the backup-root legacy probe belongs to the bind path.
                return _logFile ??= new Grog.Core.Storage.LogFile(
                    _configDir.Length > 0 ? _configDir : Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot).ConfigDir);
        }
    }

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
        LogFileOnDisk.Post(entry.Time, category.ToString(), isError, MaskAccountNames(message));   // writer thread: never disk IO on the dispatcher
        void Add() => Overview?.AppendLog(entry, isError);
        if (Dispatcher.UIThread.CheckAccess()) Add();
        else Dispatcher.UIThread.Post(Add);
    }

    [RelayCommand] private async Task OpenLogFile()
    {
        // File.Exists (and the lazy LogFile/config-dir resolve) off the dispatcher.
        var target = await Task.Run(() => System.IO.File.Exists(LogFileOnDisk.Path) ? LogFileOnDisk.Path : LogFolderPath);
        Grog.App.Services.FileManager.Open(target);
    }

    [RelayCommand] private async Task OpenThirdPartyNotices()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        var exists = await Task.Run(() => System.IO.File.Exists(path));   // 
        Grog.App.Services.FileManager.Open(exists ? path : AppContext.BaseDirectory);
    }

    [RelayCommand] private async Task OpenLicenseFile()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "LICENSE");
        var exists = await Task.Run(() => System.IO.File.Exists(path));   // 
        if (exists) Grog.App.Services.FileManager.Open(path);
        else OpenUrl("https://www.gnu.org/licenses/gpl-3.0.html");   // build without the copy: the canonical text
    }

    private static string? PathOf(Grog.Core.Volumes.BackupLayout layout, InventoryRow? row)
    {
        if (row is null) return null;
        try { return layout.ResolvePath(row.File); }
        catch { return null; }
    }

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

    [RelayCommand]
    private async Task OpenContainingFolder(InventoryRow? row)
    {
        // path resolve + the File.Exists on the backup drive off the dispatcher.
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
                _ = Task.Run(() =>   // ShellExecute off the dispatcher
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

    [RelayCommand]
    private async Task OpenGameFolder(InventoryGroupRow? group)
    {
        if (group is null) return;
        var paths = await PathsOfAsync(group.Files);   // one layout, off the dispatcher
        var dirs = paths.Select(System.IO.Path.GetDirectoryName).Where(d => !string.IsNullOrEmpty(d))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (dirs.Count == 0) { Log("That game has no files in this folder yet.", isError: true); return; }
        foreach (var d in dirs) Grog.App.Services.FileManager.Open(d);
    }

    [RelayCommand]
    private async Task CopyGamePaths(InventoryGroupRow? group)
    {
        if (group is null) return;
        var paths = await PathsOfAsync(group.Files);   // 
        if (paths.Count == 0) { Log("That game has no files in this folder yet.", isError: true); return; }
        var clipboard = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } w2 }
            ? w2.Clipboard : null;
        if (clipboard is null) return;
        try { await clipboard.SetTextAsync(string.Join(Environment.NewLine, paths!)); Log($"Copied {paths.Count} path{(paths.Count == 1 ? "" : "s")}."); }
        catch { /* clipboard can be held by another app */ }
    }

    [RelayCommand]
    private async Task RedownloadGame(InventoryGroupRow? group)
    {
        if (_manifest is null || group is null) return;
        // Core's FixRun: every file of the game reset for a fresh fetch and saved.
        var r = await Grog.Core.Runs.FixRun.RequeueAsync(_manifest, Grog.Core.Runs.FixSelector.Game(group.GogId));
        Log($"Queued all {r.Count} file(s) of {group.Title} for re-download.", category: LogCategory.Download);
        _ = ReconcileAndRefresh();
    }

    [RelayCommand]
    private async Task CopyFilePath(InventoryRow? row)
    {
        var path = await PathOfAsync(row);   // 
        if (string.IsNullOrEmpty(path)) { Log("That file has no resolved location yet.", isError: true); return; }
        var clipboard = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } w }
            ? w.Clipboard : null;
        if (clipboard is null) return;
        try { await clipboard.SetTextAsync(path); Log($"Copied path: {path}"); }
        catch { /* clipboard can be held by another app */ }
    }

    [RelayCommand]
    private async Task VerifyInventoryFile(InventoryRow? row)
    {
        if (Busy || _manifest is null || row is null || Storage.VerifyBlockedByMove()) return;
        ShowToast($"Verifying {row.FileName}…", 0);
        await RunBusy("Verifying…", async () =>
        {
            var r = (await Grog.Core.Runs.VerifyRun.RunAsync(_manifest, _backupRoot,
                // FullRehash: the same words on the game row read the bytes, so this does too.
                Grog.Core.Runs.VerifyRequest.ForFiles(new[] { (row.Item.GogId, row.File.FileKey) }, Grog.Core.Verify.VerifyMode.FullRehash)
                    with { FetchCurrentServerMd5 = ServerChecksumFetcher() },
                new Grog.Core.Runs.NullBackupHost(), _cts?.Token ?? default)).Verify;
            await ReconcileAndRefresh();
            Log($"Verified {row.FileName}: {(r.Missing > 0 ? "missing from disk" : r.Corrupt > 0 ? "corrupt (wrong size or checksum)" : r.Verified > 0 ? "checksum matches" : "size matches (GOG gives no checksum)")}",
                isError: r.Missing > 0 || r.Corrupt > 0, category: LogCategory.Verify);
        });
    }

    [RelayCommand]
    private async Task RedownloadInventoryFile(InventoryRow? row)
    {
        if (_manifest is null || row is null) return;
        // Core's FixRun over the one file (strikes cleared, fresh copy, saved).
        await Grog.Core.Runs.FixRun.RequeueAsync(_manifest, Grog.Core.Runs.FixSelector.File(row.Item.GogId, row.File.FileKey));
        Log($"Queued {row.FileName} for re-download.", category: LogCategory.Download);
        _ = ReconcileAndRefresh();
        Overview.RaiseHealth();
    }

    [ObservableProperty] private bool _showAlreadyRunning;

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

    internal void ShowToast(string message, bool isError = true)
        => ShowToast(message, isError ? 2 : 0);
    internal void ShowToast(string message, int severity, ToastFix? fix = null)
        => Overview.ShowToast(message, severity, fix);

    public void ReportUnhandled(string message)
    {
        try { Log(message, isError: true); ShowToast(message, 2); } catch { /* the crash path never throws */ }
    }

    // ---- Single activity slot: downloads and moves are serialized so they can never race the manifest.
    // Non-blocking: a move runs without the app-wide Busy lock, and pausing a move frees the slot.
    private readonly System.Threading.SemaphoreSlim _activityGate = new(1, 1);

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

    internal bool TryBeginActivitySlotQuiet() => _activityGate.Wait(0);
}

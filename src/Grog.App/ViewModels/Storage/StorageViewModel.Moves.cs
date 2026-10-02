// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Download;
using Grog.Core.Format;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Verify;
using Grog.App.Runtime;
using QueueSortKey = Grog.App.ViewModels.OverviewViewModel.QueueSortKey;

namespace Grog.App.ViewModels;

// Moves: the Moving pane (active card, waiting tail, sort pills), the pane-driven reorg runner shared by every
// move (folder change, Storage grid moves, migration, resume), pause / resume / cancel, and the drive guards.
public sealed partial class StorageViewModel
{
    // ---- Live reorg / move activity (surfaced inside the Activity tab, alongside downloads) ---------
    public System.Collections.ObjectModel.ObservableCollection<ReorgMoveRow> ReorgRows { get; } = new();

    // Active-card + reorderable-list model for the MOVING section (mirrors DOWNLOADING's shape).
    // ReorgActive is the in-flight move; ReorgWaiting is the not-yet-started tail, reorderable by drag.
    [ObservableProperty] private ReorgMoveRow? _reorgActive;
    public System.Collections.ObjectModel.ObservableCollection<ReorgMoveRow> ReorgWaiting { get; } = new();
    public bool HasReorgActive => ReorgActive is not null;
    public bool HasReorgWaiting => ReorgWaiting.Count > 0;

    // Pinned pill at the top of the Moving pane (mirrors Downloading): in-flight move, else next-up move,
    // else an idle placeholder; ReorgTail is the reorderable remainder shown below the pill.
    public System.Collections.ObjectModel.ObservableCollection<ReorgMoveRow> ReorgTail { get; } = new();
    public bool HasReorgTail => ReorgTail.Count > 0;
    public ReorgMoveRow? ReorgPillNext => HasReorgActive ? null : ReorgWaiting.FirstOrDefault();
    public bool HasReorgPillNext => !HasReorgActive && HasReorgWaiting;
    public bool ReorgPillIdle => !HasReorgActive && !HasReorgWaiting;
    public string ReorgPillState => ReorgPausing ? "PAUSING…"
        : ReorgPaused ? "PAUSED"
        : HasReorgActive ? "MOVING"
        : HasReorgWaiting ? "UP NEXT" : "";

    // ---- "Order by" for the Moving pane: the same pills, glyphs and semantics as the download queue's, over
    //      the reorg job's Pending moves. A manual drag reverts to the job's own order, as it does for downloads.
    private readonly SortState<QueueSortKey> _moveSortState = new(QueueSortKey.Order);
    public QueueSortKey MoveSort => _moveSortState.Key;
    public string MoveSortGlyphGame => _moveSortState.Glyph(QueueSortKey.Game);
    public string MoveSortGlyphKind => _moveSortState.Glyph(QueueSortKey.Kind);
    public string MoveSortGlyphSize => _moveSortState.Glyph(QueueSortKey.Size);
    public bool MoveSortIsGame => MoveSort == QueueSortKey.Game;
    public bool MoveSortIsKind => MoveSort == QueueSortKey.Kind;
    public bool MoveSortIsSize => MoveSort == QueueSortKey.Size;
    // Kind is two pills here too, so both panes read the same way.
    public bool MoveSortIsInstallers => MoveSort == QueueSortKey.Kind && _moveSortState.Asc;
    public bool MoveSortIsExtras => MoveSort == QueueSortKey.Kind && !_moveSortState.Asc;
    public string MoveSortGlyphInstallers => MoveSortIsInstallers ? MoveSortGlyphKind : "";
    public string MoveSortGlyphExtras => MoveSortIsExtras ? MoveSortGlyphKind : "";
    private void RaiseMoveSortGlyphs()
    {
        OnPropertyChanged(nameof(MoveSortGlyphGame));
        OnPropertyChanged(nameof(MoveSortGlyphKind)); OnPropertyChanged(nameof(MoveSortGlyphSize));
        OnPropertyChanged(nameof(MoveSortIsGame));
        OnPropertyChanged(nameof(MoveSortIsKind)); OnPropertyChanged(nameof(MoveSortIsSize));
        OnPropertyChanged(nameof(MoveSortIsInstallers)); OnPropertyChanged(nameof(MoveSortIsExtras));
        OnPropertyChanged(nameof(MoveSortGlyphInstallers)); OnPropertyChanged(nameof(MoveSortGlyphExtras));
    }

    [RelayCommand]
    private void SortMoves(string column)
    {
        var k = column switch
        {
            "Game" => QueueSortKey.Game, "Kind" or "Extras" => QueueSortKey.Kind, "Size" => QueueSortKey.Size, _ => QueueSortKey.Order,
        };
        // The job's own order is the resting state: it has no reverse, so it never toggles.
        if (k == QueueSortKey.Order) _moveSortState.Reset(QueueSortKey.Order, asc: true);
        // Each Kind pill pins its direction rather than toggling (see the queue's SortQueue).
        else if (column == "Kind") _moveSortState.Reset(QueueSortKey.Kind, asc: true);
        else if (column == "Extras") _moveSortState.Reset(QueueSortKey.Kind, asc: false);
        else _moveSortState.Toggle(k);
        RaiseMoveSortGlyphs();
        ApplyMoveSortToJob();
    }

    /// <summary>Re-sequence the running job's Pending moves by the active sort. The in-flight move keeps its
    /// slot (a copy in progress is never preempted by a re-sort; unlike a download it has no resumable partial
    /// worth abandoning). Event-driven: header click only.</summary>
    private void ApplyMoveSortToJob()
    {
        var job = _activeReorgJob;
        if (job is null || MoveSort == QueueSortKey.Order) return;
        bool asc = _moveSortState.Asc;
        bool changed = MoveSort switch
        {
            QueueSortKey.Game => Grog.Core.Volumes.ReorgQueueOps.SortPending(job, m => (m.Title ?? "").ToUpperInvariant(), asc),
            QueueSortKey.Kind => Grog.Core.Volumes.ReorgQueueOps.SortPending(job, m => _root.Library.Resolve(m.GogId, m.FileKey).File is { Kind: FileKind.Extra } ? 1 : 0, asc),
            QueueSortKey.Size => Grog.Core.Volumes.ReorgQueueOps.SortPending(job, m => m.SizeBytes, asc),
            _ => false,
        };
        if (changed) SyncReorgRows(job);
    }

    /// <summary>Keep ReorgTail (pill-excluded remainder) in sync with the active/waiting model. Rebuilds
    /// only on a real membership/order change, so a progress tick never clobbers an in-place reorder.</summary>
    private void RebuildReorgTail()
    {
        var want = (HasReorgActive ? ReorgWaiting : ReorgWaiting.Skip(1)).ToList();
        if (!want.Select(r => r.FileKey).SequenceEqual(ReorgTail.Select(r => r.FileKey)))
        {
            ReorgTail.Clear();
            foreach (var r in want) ReorgTail.Add(r);
            OnPropertyChanged(nameof(HasReorgTail));
        }
        OnPropertyChanged(nameof(ReorgPillNext)); OnPropertyChanged(nameof(HasReorgPillNext));
        OnPropertyChanged(nameof(ReorgPillIdle)); OnPropertyChanged(nameof(ReorgPillState));
    }

    /// <summary>Build a Moving-list row from a plan move: subtext is the real file name, colored by type
    /// (game accent for installers, Extra bucket hue for extras), looked up by (GogId, FileKey).</summary>
    private ReorgMoveRow MakeReorgRow(Grog.Core.Volumes.ReorgMove m, string state)
    {
        var f = _root.Library.Resolve(m.GogId, m.FileKey).File;
        var fileName = f?.ResolvedFileName
            ?? (!string.IsNullOrWhiteSpace(m.ToRel) ? System.IO.Path.GetFileName(m.ToRel) : null)
            ?? f?.Name ?? m.Title;
        var brush = f is { Kind: FileKind.Extra }
            ? ExtraTypeSegment.ColorFor(Grog.Core.Sync.ExtraClassifier.Classify(f.Name, f.ExtraType))
            : ExtraTypeSegment.ColorFor("Game");
        // (09-19) A hidden game's move is a row like any other (order, count and bytes are facts), unnamed.
        bool nameHidden = QueueRow.IsHiddenGame(m.GogId);
        return new ReorgMoveRow
        {
            Title = nameHidden ? QueueRow.HiddenLabel : m.Title, PathText = nameHidden ? "" : m.ToRel,
            FileName = nameHidden ? "" : fileName, TypeBrush = brush,
            GogId = m.GogId, FileKey = m.FileKey, SizeText = ByteFormat.Size(m.SizeBytes),
            SizeBytes = m.SizeBytes, StateText = state,
            OsBadge = f is { Kind: FileKind.Installer } ? PlatformNames.Badge(f.Os) : "",
        };
    }

    /// <summary>The Move queue's count/summary readouts as ONE set (active-row change + list rebuild).</summary>
    private void RaiseMoveCount()
    {
        _root.Overview.RaiseMoveCount();   // MoveQueueCount / MoveCountText / ShowMoveCount live on the shell's Activity tab
        OnPropertyChanged(nameof(MoveStatsText));
    }

    partial void OnReorgActiveChanged(ReorgMoveRow? value)
    {
        OnPropertyChanged(nameof(HasReorgActive));
        RaiseMoveCount();
    }

    /// <summary>Move footer summary ("124 of 214 files - 760 GB of 1.3 TB").</summary>
    public string ReorgFooterText { get; private set; } = "";
    [ObservableProperty] private bool _isReorgRunning;
    /// <summary>A consented move is holding, waiting for the single activity slot. Not "running": nothing is
    /// being copied yet.</summary>
    [ObservableProperty] private bool _moveWaitingForSlot;
    [ObservableProperty] private bool _reorgFinished;
    [ObservableProperty] private string _reorgHeaderText = "";
    [ObservableProperty] private string _reorgProgressText = "";
    [ObservableProperty] private double _reorgPercent;
    private Grog.Core.Volumes.ReorgControl? _reorgControl;
    private Grog.Core.Volumes.ReorgJob? _activeReorgJob;
    private string? _activeReorgConfigDir;
    private double _reorgFileFraction;   // 0..1 progress of the currently-copying move file
    private long _lastReorgDriveProbe;   // 1 Hz throttle for live drive/dashboard refresh during a move

    // Two-way with the Activity tab's IsSelected, so a starting move can bring the tab forward.
    [ObservableProperty] private bool _activityTabSelected;

    // A paused move keeps its card up with a Resume control, and (crucially) frees the activity slot
    // below so a download can slip in while it's parked.
    [ObservableProperty] private bool _reorgPaused;

    // A waiting move changes the Moving footer and earns the tab dot, same as a running one.
    partial void OnMoveWaitingForSlotChanged(bool value)
    {
        OnPropertyChanged(nameof(MoveStatsText));
        _root.Overview.RaiseActivityTabHeader();
        OnPropertyChanged(nameof(ShowMoveTransport));   // Cancel lives in the transport, so it must appear too
    }

    /// <summary>Always-present footer summary for the Moving section (mirrors the Downloading footer):
    /// live progress while a move runs, else the queued count, else an idle line.</summary>
    public string MoveStatsText => IsReorgRunning ? ReorgFooterText
        : MoveWaitingForSlot ? "Waiting - this move starts when the current move, scan or verify finishes"   // (09-22) never behind downloads
        : _root.Overview.MoveQueueCount > 0 ? $"{Grog.Core.Format.Plural.Of(_root.Overview.MoveQueueCount, "file")} queued to move"
        : "No moves queued";

    partial void OnIsReorgRunningChanged(bool value)
    {
        _root.Overview.RaiseRailOverviewSub();   // (09-22) the rail line says "moving N files"
        _root.Overview.RaiseActivityTabHeader();
        OnPropertyChanged(nameof(ShowPauseReorg));
        OnPropertyChanged(nameof(ShowMoveTransport));
        OnPropertyChanged(nameof(MoveStatsText));
    }

    // Pause is offered only while a move is actively running (not finished, not already paused).
    public bool ShowPauseReorg => IsReorgRunning && !ReorgFinished && !ReorgPaused;
    /// <summary>While the in-flight chunk drains, the Pause button STAYS VISIBLE as a disabled "Pausing…"
    /// (owner 08-30: hiding it left only Stop for a second, which read as unresponsive).</summary>
    public string ReorgPauseLabel => ReorgPausing ? "Pausing…" : "Pause";
    // The Moving transport cluster is present whenever there's a running or paused move to control.
    public bool ShowMoveTransport => IsReorgRunning || ReorgPaused || MoveWaitingForSlot;
    partial void OnReorgFinishedChanged(bool value) => OnPropertyChanged(nameof(ShowPauseReorg));
    partial void OnReorgPausedChanged(bool value) { OnPropertyChanged(nameof(ShowPauseReorg)); OnPropertyChanged(nameof(ShowMoveTransport)); OnPropertyChanged(nameof(ReorgPillState)); }

    /// <summary>A consented move waiting for the slot. Exactly ONE is held: a newer request replaces an
    /// older one, because the newer plan matches what the settings now say.</summary>
    private DeferredMove? _deferredMove;
    /// <summary>A move waiting for the slot, with everything needed to start it. <paramref name="AllFrom"/>: it was
    /// "Move all" off that root, so it is re-planned over what the drive holds when it finally starts.</summary>
    private sealed record DeferredMove(Grog.Core.Volumes.ReorgJob Job, string ConfigDir, Func<Task>? OnReachedEnd,
                                       ReorgExecutor? Execute, (string From, string To)? AllFrom);

    /// <summary>How a pane-driven move executes its job: given the pane's progress callback and the pane's
    /// control, run to completion. Null = the plain ReorgRunner (folder change, Storage moves, resume); the
    /// migrate prompt passes Core's LayoutMigrationRun. (S2.2)</summary>
    private delegate Task<Grog.Core.Runs.LayoutMigrationOutcome> ReorgExecutor(
        Action<Grog.Core.Volumes.ReorgProgress> progress, Grog.Core.Volumes.ReorgControl control);

    // When set, this runs once the active reorg reaches the end of its plan (Completed or
    // CompletedWithErrors), before the reconcile -- used by a Change-folder move to flip the root's
    // PathHint to the new folder so the just-moved files resolve there instead of being flagged missing.
    private Func<Task>? _onReorgReachedEnd;
    /// <summary>The job the hook belongs to. A hook outlives a pause (Resume re-enters with no hook of its own)
    /// but never crosses to a DIFFERENT job: a paused "Move & Forget" followed by a plain grid move ran the
    /// forget on the grid move's completion, with half the drive's files still on it (QA 09-18).</summary>
    private Grog.Core.Volumes.ReorgJob? _onReorgReachedEndJob;

    /// <summary>The in-progress downloads (.part files) of every drive this job clears, carried to the drive the
    /// files are going to. Off the dispatcher: a multi-GB partial leaving a USB stick is minutes of copying.</summary>
    private async Task<int> CarryPartialsForJobAsync(Grog.Core.Volumes.ReorgJob job)
    {
        if (_session.Manifest is not { } store) return 0;
        int carried = 0;
        try
        {
            List<(string FromRootId, string ToRootId)> cleared;
            using (store.Gate.Enter()) cleared = Grog.Core.Volumes.PartialCarry.RootsClearedBy(store.Current, job);
            foreach (var (from, to) in cleared) carried += (await CarryPartialsOffAsync(from, null, to)).Carried;
        }
        catch (Exception ex) { _root.Log($"In-progress downloads were not carried: {ex.Message}", isError: false, category: LogCategory.Move); }
        return carried;
    }

    /// <summary>(QA 09-30 B11) Roots with a carry in flight: a second slot change fired a second carry on the same root,
    /// which found the .parts gone (the first was moving them) and cleared their records.</summary>
    private readonly HashSet<string> _carrying = new();

    /// <summary>Carry the in-progress downloads recorded on <paramref name="fromRootId"/> to the drive each file now
    /// belongs on, and say what happened. <paramref name="fromPath"/> null = ask the layout (a Detached drive is not
    /// bound, so its caller passes the folder). Returns what could not go.</summary>
    internal async Task<Grog.Core.Volumes.PartialCarry.Result> CarryPartialsOffAsync(string fromRootId, string? fromPath, string? preferredRootId)
    {
        var none = Grog.Core.Volumes.PartialCarry.Result.None;
        if (_session.Manifest is not { } store) return none;
        if (!_carrying.Add(fromRootId)) return none;   // one carry per root at a time (B11)
        try { return await CarryPartialsOffCoreAsync(store, fromRootId, fromPath, preferredRootId); }
        finally { _carrying.Remove(fromRootId); }
    }

    private async Task<Grog.Core.Volumes.PartialCarry.Result> CarryPartialsOffCoreAsync(Grog.Core.Manifest.JsonManifestStore store, string fromRootId, string? fromPath, string? preferredRootId)
    {
        var none = Grog.Core.Volumes.PartialCarry.Result.None;
        (int n, long bytes) recorded; List<(long, string)> keys;
        using (store.Gate.Enter()) { recorded = Grog.Core.Volumes.PartialCarry.RecordedOn(store.Current, fromRootId); keys = Grog.Core.Volumes.PartialCarry.RecordedKeysOn(store.Current, fromRootId); }
        if (recorded.n == 0) return none;
        var backupRoot = _session.BackupRoot; var engine = _session.LiveEngine;
        // (B6) No worker may open one of these .parts while the carry copies into it: held for the carry's duration.
        engine?.HoldFiles(keys);
        string label = store.Current.Roots.FirstOrDefault(r => r.Id == fromRootId)?.Label ?? "the drive";
        _root.Log($"Carrying {recorded.n} in-progress download{(recorded.n == 1 ? "" : "s")} ({ByteFormat.Size(recorded.bytes)}) off {label} first…", category: LogCategory.Move);
        Grog.Core.Volumes.PartialCarry.Result result;
        try
        {
            result = await Task.Run(() =>
            {
                var layout = new Grog.Core.Volumes.BackupLayout(store.Current, backupRoot);
                var path = fromPath ?? layout.RootPath(fromRootId);
                if (string.IsNullOrEmpty(path)) return new Grog.Core.Volumes.PartialCarry.Result(0, 0, recorded.n, recorded.bytes, 0);
                var devices = new Grog.Core.Volumes.VolumeService(store, _session.PendingWrites).DeviceSpaces(layout);
                bool Transferring(Grog.Core.Models.GameFile f) => engine?.Snapshot.Any(t => ReferenceEquals(t.File, f) && t.State == Grog.Core.Download.DownloadTaskState.Active) == true;
                return Grog.Core.Volumes.PartialCarry.Carry(store, layout, fromRootId, path, preferredRootId, devices, Transferring,
                                                           msg => Dispatcher.UIThread.Post(() => _root.Log(msg, isError: false, category: LogCategory.Move)));
            });
        }
        finally { engine?.ReleaseFiles(keys); }
        if (result.Carried > 0)
            _root.Log($"Carried {result.Carried} in-progress download{(result.Carried == 1 ? "" : "s")} ({ByteFormat.Size(result.CarriedBytes)}): Resume continues from there.", category: LogCategory.Move);
        if (result.Left > 0)
            _root.Log($"{result.Left} in-progress download{(result.Left == 1 ? "" : "s")} ({ByteFormat.Size(result.LeftBytes)}) stayed on {label} (no room elsewhere, or the drive is not reachable): {(result.Left == 1 ? "it restarts" : "they restart")} from zero unless {label} comes back.", category: LogCategory.Move);
        if (result.Any) await store.SaveAsync();
        return result;
    }

    private bool _activeReorgIsPlain;
    /// <summary>Files the running move has not settled yet (the rail's "moving N files").</summary>
    internal int MovesLeft => _activeReorgJob?.Unsettled ?? 0;
    /// <summary>(09-22) One move at a time; independent of the download slot.</summary>
    private readonly SemaphoreSlim _moveGate = new(1, 1);
    private bool control_IsStopped(Grog.Core.Volumes.ReorgJob job) => _reorgControl is { IsCanceled: true } || ReferenceEquals(job, _supersededJob);

    /// <summary>Add a freshly planned move's files to the move that is running. Only a plain Storage-page move can
    /// take them (a layout migration runs through Core's own executor with its own bookkeeping); a paused job
    /// cannot (its Resume owns it). A file already waiting in the job is not added twice. True = joined.</summary>
    private bool TryJoinRunningMove(Grog.Core.Volumes.ReorgJob planned, string targetRootId, string targetLabel)
    {
        if (!IsReorgRunning || ReorgPaused || !_activeReorgIsPlain || _activeReorgJob is not { } running) return false;
        if (planned.Reason != Grog.Core.Volumes.ReorgReason.Relocate || running.Reason != Grog.Core.Volumes.ReorgReason.Relocate
            || ReferenceEquals(running, _supersededJob)) return false;
        var added = running.TryAppend(planned);
        if (added.Count == 0) { _root.Log("Move: those files are already in the move that is running.", category: LogCategory.Move); return true; }
        foreach (var a in added) ReorgRows.Add(MakeReorgRow(a, "Waiting"));
        SyncReorgRows(running); RebuildInventory(); _root.RecomputeActivity();
        _root.Log($"Added {Grog.Core.Format.Plural.Of(added.Count, "file")} to the move in progress (to {targetLabel}).", category: LogCategory.Move);
        return true;
    }

    /// <param name="allFrom">This is "Move all" off a root: if it has to wait, it is re-planned over what the drive
    /// holds when it starts. A parameter, never a field: a field outlived its move and leaked into the next one
    /// that waited (QA 09-22).</param>
    private async Task RunReorgWithPaneAsync(Grog.Core.Volumes.ReorgJob job, string configDir, Func<Task>? onReachedEnd = null,
                                             ReorgExecutor? execute = null, (string From, string To)? allFrom = null)
    {
        // A paused job owns the pane, the journal and its hook. A new job on top of it overwrote all three and
        // made the paused plan unrecoverable (QA 09-18): the user resumes or stops it first. Inform, never choose.
        if (ReorgPaused && _activeReorgJob is not null && !ReferenceEquals(_activeReorgJob, job))
        {
            _root.ShowToast("A paused move is waiting. Resume or Stop it before starting another.", 1);
            return;
        }
        // (owner 09-22) Moves and downloads run SIDE BY SIDE: a move takes its own gate, not the download slot, and
        // PendingWrites keeps the two honest on space. What still defers a move: another move in flight (a new plan
        // joins it through TryJoinRunningMove before reaching here, or waits), and a scan or verify (Busy with no
        // download), because verify reads files a move would be relocating under it.
        bool busyElsewhere = _root.Busy && !_root.DownloadRunning;
        if (busyElsewhere || !_moveGate.Wait(0))
        {
            _deferredMove = new DeferredMove(job, configDir, onReachedEnd, execute, allFrom);
            MoveWaitingForSlot = true;
            ReorgRows.Clear();
            foreach (var mv in job.Moves) ReorgRows.Add(MakeReorgRow(mv, "Waiting"));
            ActivityTabSelected = true;
            _root.Log($"Move queued -- it starts on its own when the current {(busyElsewhere ? "scan or verify" : "move")} finishes.", category: LogCategory.Move);
            _root.ShowToast($"Move queued - it starts when the current {(busyElsewhere ? "scan or verify" : "move")} finishes.", 0);
            return;
        }
        try
        {
            _activeReorgJob = job;
            _activeReorgConfigDir = configDir;
            if (onReachedEnd is not null) { _onReorgReachedEnd = onReachedEnd; _onReorgReachedEndJob = job; }
            else if (!ReferenceEquals(_onReorgReachedEndJob, job)) { _onReorgReachedEnd = null; _onReorgReachedEndJob = null; }
            _reorgControl = new Grog.Core.Volumes.ReorgControl();

            ReorgRows.Clear();
            foreach (var mv in job.Moves)
                ReorgRows.Add(MakeReorgRow(mv, "Waiting"));

            IsReorgRunning = true;
            ReorgFinished = false;
            ReorgPaused = false;
            ReorgPausing = false;
            ReorgHeaderText = "Reorganizing your backups";
            // (09-22) The download planner and the engine subtract what this move still has to copy onto each volume.
            // Registered BEFORE the carry (QA 09-30 B8): the carry priced its room from drives that did not yet know
            // about the job's own writes.
            _session.PendingWrites.Register(job.RemainingWrites);
            // (09-19) A move that CLEARS a drive takes that drive's in-progress downloads first, so they are safe
            // early and Resume continues from them whatever happens to the drive afterwards.
            if (await CarryPartialsForJobAsync(job) > 0)
            {
                // The carried .parts took room on the target the plan did not price: re-check, drop what no longer fits (B8).
                int dropped = await Task.Run(() => DropWhatNoLongerFitsFor(job));
                NoteDroppedOnResume(job, dropped);
                if (dropped > 0) { ReorgRows.Clear(); foreach (var mv in job.Moves) ReorgRows.Add(MakeReorgRow(mv, "Waiting")); }
            }
            ActivityTabSelected = true;   // bring the Activity tab forward so the move is visible
            // A move of real size runs for minutes in the background with no modal of its own: say once, up
            // front, where it can be watched. EVERY entry point (folder change, Storage page moves, migrate,
            // resume) comes through here, so this is the one place the notice belongs (owner 09-04: a Storage
            // page move started with no word about where to look). Not when Overview is already on screen.
            if (job.Moves.Count > 0 && _root.CurrentView != MainWindowViewModel.View.Overview) ShowMoveStartedNotice = true;
            SyncReorgRows(job);
            RebuildInventory();           // flip affected inventory rows to "Moving"
            _root.RecomputeActivity();          // Library rows + detail pane show QueuedToMove / Moving from the first second

            // Capture the per-file fraction so the active-move bar tracks THIS file's copy (like a download),
            // instead of sitting flat at whole-job percent until a file settles.
            int lastFilesDone = 0;
            // Coalesced to 250 ms, latest value wins: per-chunk posts rebuilt the rows for every megabyte copied.
            Grog.Core.Volumes.ReorgProgress? latest = null;
            var progressUi = new Services.UiCoalescer(TimeSpan.FromMilliseconds(250), () =>
            {
                // A trailing tick can land after this job finished or a new job took over: never let it repaint.
                if (ReorgFinished || !ReferenceEquals(_activeReorgJob, job)) return;
                if (latest is not { } p) return;
                _reorgFileFraction = p.CurrentFileFraction;
                SyncReorgRows(job);
                // The Storage grids hold the per-file "Moving" status and the destination's row list: rebuild
                // as each file lands, while the page is on screen, so it reads as live as the cards above it.
                if (p.FilesDone != lastFilesDone)
                {
                    lastFilesDone = p.FilesDone;
                    _root.Overview.RaiseRailOverviewSub();   // (09-22) "moving N files" counts down
                    if (_root.CurrentView == MainWindowViewModel.View.Drives) RebuildInventory();
                    _root.RecomputeActivity();   // a landed file's Library/detail dot goes from Moving to settled
                }
                // Same live treatment downloads got (1032-1041): a move changes drive free space and per-
                // drive Grog share while a big file is still copying, so re-probe at 1 Hz - not only on
                // settle - or the Folders/Overview cards sit frozen through a long copy.
                var nowTicks = System.Environment.TickCount64;
                if (nowTicks - _lastReorgDriveProbe >= 1000)
                {
                    _lastReorgDriveProbe = nowTicks;
                    _root.Overview.RaiseHealth(); _root.RaiseDashboard();   // RaiseDashboard re-probes the drives
                }
            });
            Action<Grog.Core.Volumes.ReorgProgress> onProgress = p => { latest = p; Dispatcher.UIThread.Post(progressUi.Request); };
            // (S2.2) Off the dispatcher either way: Core's LayoutMigrationRun for the migrate prompt, the plain
            // runner for every other job. Both hand back the same outcome shape.
            var control = _reorgControl!;
            _activeReorgIsPlain = execute is null;
            var run = Task.Run(() => execute is not null
                ? execute(onProgress, control)
                : RunPlainReorgAsync(job, configDir, onProgress, control));
            if (execute is not null) _liveMoveTask = run;   // the migrate path too: a quit or a reset waits on it (review 09-30)
            var result = await run;
            if (result.Error is { } fail)
            {
                // A runner-level failure (e.g. the journal file not writable) must SURFACE, never crash
                // the app: the journal on disk still holds the plan, so a later resume can pick it up.
                ReorgPausing = false;
                ReorgPaused = false;
                ReorgFinished = true;
                IsReorgRunning = false;
                ReorgHeaderText = "Reorganize failed";
                ReorgProgressText = fail.Message;
                OnPropertyChanged(nameof(ReorgHeaderText)); OnPropertyChanged(nameof(ReorgProgressText));
                _root.Log($"Reorganize failed: {fail.Message}", isError: true, category: LogCategory.Move);
                _root.ShowToast("Reorganize failed - see the Activity log.", 2);
                return;
            }

            // STATE FLIP FIRST, before any reconcile work: the transport buttons (Pause -> Resume) must
            // reflect the runner the instant it stops. This used to sit after ReconcileAndRefresh, so a
            // slow or throwing reconcile left a paused move still showing Pause (owner-hit 08-30).
            var paused = result.Outcome == Grog.Core.Volumes.ReorgOutcome.Paused;
            SyncReorgRows(job);
            ReorgPausing = false;
            ReorgPaused = paused;
            ReorgFinished = !paused;   // paused keeps the card live with a Resume control
            IsReorgRunning = paused;   // a finished (non-paused) move is no longer "running" -- clears
                                       // the green dot, footer, Cancel, and the Activity tab dot
            ReorgHeaderText = result.Outcome switch
            {
                Grog.Core.Volumes.ReorgOutcome.Completed => "Reorganize complete",
                Grog.Core.Volumes.ReorgOutcome.CompletedWithErrors => "Reorganize finished with problems",
                Grog.Core.Volumes.ReorgOutcome.Canceled => "Reorganize canceled",
                Grog.Core.Volumes.ReorgOutcome.Paused when result.PauseReason is not null => "Move paused: " + result.PauseReason,
                Grog.Core.Volumes.ReorgOutcome.Paused => "Reorganize paused -- resume any time",
                _ => "Reorganize"
            };
            ReorgProgressText = $"{result.Moved} moved"
                + (result.Skipped > 0 ? $", {result.Skipped} skipped" : "")
                + (result.Failed > 0 ? $", {result.Failed} failed" : "");
            // (09-14) The counts go to the log, not only the pane header: the header is gone next launch and the
            // log is the record of what actually landed (an AddOffer line once claimed 337 moved with 0 on disk).
            _root.Log($"{ReorgHeaderText}: {ReorgProgressText}.", isError: result.Failed > 0, category: LogCategory.Move);
            _moveWaitsForRootId = paused ? result.PausedForRootId : null;
            if (result.PauseReason is { } why)
            {
                // The runner stopped itself (a drive went away, failures in a row). Nothing was lost: the
                // source files are untouched and the move that tripped it is back in the queue. A drive pause
                // resumes on its own when the drive is back (walk 5.6); anything else waits for Resume.
                bool forDrive = _moveWaitsForRootId is not null;
                _root.Log($"Move paused: {why}. Nothing was lost; " + (forDrive ? "it continues when the drive is back." : "Resume retries it."), isError: true, category: LogCategory.Move);
                _root.ShowToast($"Move paused: {why}. " + (forDrive ? "It continues when the drive is back." : "Resume retries it."), 1);   // warning tier: an action is waiting, not a loss
            }
            else if (result.Failed > 0)
            {
                // Failed moves leave their source files where they were; Resume re-admits and retries them.
                _root.ShowToast($"{Grog.Core.Format.Plural.Of(result.Failed, "file")} couldn't be moved. Sources are untouched; Resume retries them.");
            }

            var reachedEnd = result.Outcome is Grog.Core.Volumes.ReorgOutcome.Completed
                                             or Grog.Core.Volumes.ReorgOutcome.CompletedWithErrors;
            if (reachedEnd && _onReorgReachedEnd is not null && ReferenceEquals(_onReorgReachedEndJob, job))
            {
                var hook = _onReorgReachedEnd;
                _onReorgReachedEnd = null; _onReorgReachedEndJob = null;
                try { await hook(); } catch (Exception ex) { _root.Log($"Post-move step failed: {ex.Message}", isError: true); }
            }
            else if (reachedEnd && job.RepointRootId is { } repointId && job.RepointPath is { } repointPath)
            {
                // A Change Folder job finished with no hook: it was resumed after a restart. The follow-up lives on the
                // job (QA 09-30 B2): flip the root to its new folder, or every file reads Missing at the old one.
                var row = BackupLocations.FirstOrDefault(r => r.RootId == repointId);
                try
                {
                    if (row is not null && await RepointRootAsync(row, repointPath))
                        Dispatcher.UIThread.Post(() => { RefreshBackupLocations(); _root.Log($"Moved {row.Label} to {repointPath}", category: LogCategory.Move); });
                    else if (row is null) _root.Log($"Move finished, but the storage to re-point ({repointId}) is no longer tracked.", isError: true, category: LogCategory.Move);
                }
                catch (Exception ex) { _root.Log($"Post-move step failed: {ex.Message}", isError: true); }
            }
            // Heavy refresh AFTER the state flip, and guarded: a reconcile failure must never strand the UI.
            try { await _root.ReconcileAndRefresh(); }
            catch (Exception ex) { _root.Log($"Refresh after the move failed: {ex.Message}", isError: true); }
            Dispatcher.UIThread.Post(() =>
            {
                SyncReorgRows(job);
                RefreshBackupLocations();   // device-card counts / free space reflect the moved files
                RebuildInventory();        // settled files drop the "Moving" status

                // A canceled job leaves real files somewhere the manifest may not expect. Don't just close the
                // pane -- ask what to do about them while the user is still here and still has the context.
                if (result.Outcome == Grog.Core.Volumes.ReorgOutcome.Canceled) { _onReorgReachedEnd = null; _onReorgReachedEndJob = null; if (!ReferenceEquals(job, _supersededJob)) OpenStopChoice(); }
            });
        }
        finally
        {
            _session.PendingWrites.Clear();
            // Release the move gate whatever the outcome (a pause too: Resume re-takes it).
            try { _moveGate.Release(); } catch (SemaphoreFullException) { }
            // (09-20) Files that joined the job in the instant the runner was finishing were never picked up: run
            // the same job again for them. Not for a paused or stopped job, whose Resume does this.
            bool lateJoiners; lock (job.SyncRoot) lateJoiners = job.Moves.Any(x => x.State == Grog.Core.Volumes.ReorgMoveState.Pending);
            if (lateJoiners && !ReorgPaused && !control_IsStopped(job))
                Dispatcher.UIThread.Post(async () => await RunReorgWithPaneAsync(job, configDir));
            else StartDeferredMove();   // a move that waited for this one (or for a scan) starts now
        }
    }

    /// <summary>The non-migrate jobs (folder change, Storage moves, resume) run the ReorgRunner directly; a
    /// runner-level throw becomes the outcome's Error so the pane renders it once. (S2.2)</summary>
    private async Task<Grog.Core.Runs.LayoutMigrationOutcome> RunPlainReorgAsync(Grog.Core.Volumes.ReorgJob job, string configDir,
        Action<Grog.Core.Volumes.ReorgProgress> progress, Grog.Core.Volumes.ReorgControl control)
    {
        var runner = new Grog.Core.Volumes.ReorgRunner(_session.Manifest, new Grog.Core.Volumes.ReorgJournalStore(configDir));
        runner.Progress += progress;
        try
        {
            var run = runner.RunAsync(job, control);
            _liveMoveTask = run;   // a quit waits on this so the file being copied lands or is discarded first
            var r = await run;
            return new Grog.Core.Runs.LayoutMigrationOutcome(r.Outcome, r.Moved, r.Skipped, r.Failed,
                r.Outcome == Grog.Core.Volumes.ReorgOutcome.Canceled, r.PauseReason, null, r.PausedForRootId);
        }
        catch (Exception ex)
        {
            return new Grog.Core.Runs.LayoutMigrationOutcome(Grog.Core.Volumes.ReorgOutcome.CompletedWithErrors, 0, 0, 0, false, null, ex);
        }
    }

    // Target drive labels for the active card, resolved once per job from the loaded manifest (no disk).
    private Grog.Core.Volumes.ReorgJob? _reorgLabelJob;
    private readonly Dictionary<string, string> _reorgLabels = new();
    private string ReorgTargetLabel(Grog.Core.Volumes.ReorgJob job, Grog.Core.Volumes.ReorgMove mv)
        => ReorgRootLabel(job, mv.ToRootId ?? mv.FromRootId);
    private string ReorgSourceLabel(Grog.Core.Volumes.ReorgJob job, Grog.Core.Volumes.ReorgMove mv)
        => ReorgRootLabel(job, mv.FromRootId);
    private string ReorgRootLabel(Grog.Core.Volumes.ReorgJob job, string? rootIdOrNull)
    {
        if (!ReferenceEquals(_reorgLabelJob, job)) { _reorgLabelJob = job; _reorgLabels.Clear(); }
        var rootId = rootIdOrNull ?? _session.Manifest?.Current.PrimaryRootId ?? "";
        if (!_reorgLabels.TryGetValue(rootId, out var label)) _reorgLabels[rootId] = label = SlotNameOf(rootId);
        return label;
    }

    private void SyncReorgRows(Grog.Core.Volumes.ReorgJob job)
    {
        for (int i = 0; i < ReorgRows.Count && i < job.Moves.Count; i++)
        {
            var st = job.Moves[i].State;
            ReorgRows[i].IsDone = st is Grog.Core.Volumes.ReorgMoveState.Done or Grog.Core.Volumes.ReorgMoveState.Skipped;
            ReorgRows[i].StateText = st switch
            {
                Grog.Core.Volumes.ReorgMoveState.Done => "Moved",
                Grog.Core.Volumes.ReorgMoveState.Skipped => "Skipped",
                Grog.Core.Volumes.ReorgMoveState.Failed => "Failed",
                Grog.Core.Volumes.ReorgMoveState.Copied or Grog.Core.Volumes.ReorgMoveState.Verified => "Moving…",
                _ => "Waiting"
            };
        }
        ReorgPercent = job.Total == 0 ? 100 : 100.0 * job.Settled / job.Total;
        ReorgProgressText = $"{job.Settled} of {job.Total} files";

        // Active-card + waiting-list model (mirrors DOWNLOADING). The in-flight move is the card; the
        // not-yet-started tail is the reorderable list. Settled moves drop off (like finished downloads).
        var cur = job.Current;
        // A pause clears the runner's Current, but a file part-way through its copy is still the active card: keep it
        // with its bar (the download card does the same) until the file lands or the move is stopped.
        if (cur is null && ReorgActive is { } held && (job.Paused || ReorgPaused) && job.Pending().Any(p => p.FileKey == held.FileKey && p.GogId == held.GogId))
            cur = job.Moves.First(p => p.FileKey == held.FileKey && p.GogId == held.GogId);
        if (cur is not null && !cur.IsSettled)
        {
            ReorgActive ??= new ReorgMoveRow();
            if (ReorgActive.FileKey != cur.FileKey || ReorgActive.GogId != cur.GogId)
            {
                ReorgActive = MakeReorgRow(cur, "Moving…");
                _reorgFileFraction = 0;   // new file starts its own bar at 0
            }
            // Per-file progress (this file's copy), like a download. Verified state = fully copied, bar full.
            ReorgActive.Percent = cur.State == Grog.Core.Volumes.ReorgMoveState.Verified
                ? 100.0
                : System.Math.Clamp(100.0 * _reorgFileFraction, 0, 100);
            // The download card's readout and, bottom-right, where it is going: "1.3 GB / 2.38 GB · 55%"  "Primary → Small".
            var copied = (long)(cur.SizeBytes * ReorgActive.Percent / 100.0);
            ReorgActive.ProgressText = cur.SizeBytes > 0 ? $"{ByteFormat.Size(copied)} / {ByteFormat.Size(cur.SizeBytes)} · {ReorgActive.Percent:F0}%" : "";
            ReorgActive.RouteText = $"{ReorgSourceLabel(job, cur)} \u2192 {ReorgTargetLabel(job, cur)}";
            ReorgActive.StateText = ReorgActive.ProgressText.Length > 0 ? ReorgActive.ProgressText : "Moving…";
        }
        else ReorgActive = null;

        // The runner sets job.Current while the move is still Pending (it only leaves that state when the
        // copy starts), so for that window the same file is BOTH the active card and a Pending entry --
        // it rendered twice and was counted twice in the header's file count and byte total.
        var pending = job.Pending()
            .Where(p => ReorgActive is null || p.FileKey != ReorgActive.FileKey || p.GogId != ReorgActive.GogId)
            .ToList();
        // Rebuild the waiting list only when its membership changes, so an in-place reorder isn't clobbered.
        var wantKeys = pending.Select(p => p.FileKey).ToList();
        var haveKeys = ReorgWaiting.Select(r => r.FileKey).ToList();
        if (!wantKeys.SequenceEqual(haveKeys))
        {
            ReorgWaiting.Clear();
            foreach (var p in pending)
                ReorgWaiting.Add(MakeReorgRow(p, "Waiting"));
        }
        OnPropertyChanged(nameof(HasReorgWaiting));
        RaiseMoveCount();
        RebuildReorgTail();

        // Bytes include the in-flight file's copied fraction (download parity: coverage GB creeps during a
        // big file); the FILE count stays settled-only - half a file is not a file you have moved.
        var cur2 = job.Current;
        long inFlight = cur2 is not null && !cur2.IsSettled ? (long)(_reorgFileFraction * cur2.SizeBytes) : 0;
        ReorgFooterText = $"{job.Settled} of {job.Total} files · {ByteFormat.Size(job.BytesSettled + inFlight)} of {ByteFormat.Size(job.BytesTotal)}";
        OnPropertyChanged(nameof(ReorgFooterText));
        OnPropertyChanged(nameof(MoveStatsText));
    }

    /// <summary>Drag-reorder a waiting move to a new position -- reprioritizes which pending file the
    /// runner picks next. Maps the row to its plan entry and repositions it in the Pending subset.</summary>
    public void MoveReorgRow(ReorgMoveRow row, int newIndex)
    {
        var job = _activeReorgJob;
        if (job is null) return;
        var mv = job.Moves.FirstOrDefault(m => m.FileKey == row.FileKey && m.GogId == row.GogId);
        if (mv is null) return;
        Grog.Core.Volumes.ReorgQueueOps.MovePendingTo(job, mv, newIndex);
        // A hand-placed row is the user's own order again: the sort pills drop back to "Queued".
        if (MoveSort != QueueSortKey.Order) { _moveSortState.Reset(QueueSortKey.Order, asc: true); RaiseMoveSortGlyphs(); }
        SyncReorgRows(job);
        _root.Log($"Moved {row.Title} up the move queue.", category: LogCategory.Move);
    }

    /// <summary>True from the Pause click until the runner actually stops: the UI acknowledges the click
    /// INSTANTLY (button swaps out, header says Pausing) while the in-flight chunk drains - on a slow USB
    /// target that can be a second, and a button that sits inert for a second reads as broken.</summary>
    [ObservableProperty] private bool _reorgPausing;
    partial void OnReorgPausingChanged(bool value)
    { OnPropertyChanged(nameof(ShowPauseReorg)); OnPropertyChanged(nameof(ReorgPauseLabel)); OnPropertyChanged(nameof(ReorgPillState)); }

    [RelayCommand]
    private void PauseReorg()
    {
        if (_reorgControl is null) return;
        _reorgControl.Pause();
        ReorgPausing = true;
        ReorgHeaderText = "Pausing…";
        OnPropertyChanged(nameof(ReorgHeaderText));
    }

    /// <summary>The root whose missing drive paused the move, while that pause stands.</summary>
    private string? _moveWaitsForRootId;

    /// <summary>The presence watch saw a storage come back: a move that paused for that drive continues on its own,
    /// the way downloads do (walk 5.6: the drive returned, Resume restarted downloads, the move sat at 0%).</summary>
    internal void OnStorageBackForMove(string rootId)
    {
        if (_moveWaitsForRootId != rootId || !ReorgPaused || _activeReorgJob is null) return;
        _moveWaitsForRootId = null;
        _root.Log($"{DriveLabel(rootId)} is back. The move continues.", category: LogCategory.Move);
        _root.ShowToast($"{DriveLabel(rootId)} is back. The move continues.", 0);   // replaces the standing "Move paused" warning; info toasts self-dismiss
        _ = ResumePausedReorg();
    }

    [RelayCommand]
    private async Task ResumePausedReorg()
    {
        if (_activeReorgJob is null || _activeReorgConfigDir is null) return;
        _moveWaitsForRootId = null;
        var job = _activeReorgJob;
        // Downloads may have filled the target while the move was paused: the same room re-check startup's Resume
        // runs, off the dispatcher (the layout probes every root; QA 09-30 B12).
        int dropped = await Task.Run(() => DropWhatNoLongerFitsFor(job));
        NoteDroppedOnResume(job, dropped);
        if (dropped > 0) SyncReorgRows(job);
        await RunReorgWithPaneAsync(job, _activeReorgConfigDir);
    }

    [RelayCommand] private void CancelActiveReorg() => _reorgControl?.Cancel();

    /// <summary>Fresh Library: stop a running move and wait for it to let go of its files before the manifest is wiped.</summary>
    internal async Task StopMoveForResetAsync()
    {
        if (_activeReorgJob is not { } job) return;
        _supersededJob = job;   // its end-of-run pass must not open the "put back / accept" choice over a wiped library
        var configDir = _activeReorgConfigDir;
        _reorgControl?.Cancel();
        await WhenMoveStopped;
        ClearReorgSurfaces();
        if (configDir is not null) try { new Grog.Core.Volumes.ReorgJournalStore(configDir).Delete(); } catch { /* best effort: the wipe drops what it described */ }
    }

    /// <summary>The move job is gone (dismissed, reverted, orphans accepted): every surface derived from it
    /// goes with it - the pane rows, the active card + waiting tail, the "N files queued to move" count and
    /// the Library/detail move dots (owner, 09-04: dots and the count outlived the job until the next
    /// download event, because navigation never re-derives the activity map).</summary>
    private void ClearReorgSurfaces()
    {
        _onReorgReachedEnd = null;   // (09-15 review) a hook from a cancelled/dismissed move must not fire at the end of the next one
        IsReorgRunning = false; ReorgFinished = false; ReorgPaused = false;
        ReorgRows.Clear();
        _activeReorgJob = null;
        ReorgActive = null; ReorgWaiting.Clear();
        OnPropertyChanged(nameof(HasReorgWaiting));
        RebuildReorgTail(); RaiseMoveCount();
        _root.RecomputeActivity();
        if (_root.CurrentView == MainWindowViewModel.View.Drives) RebuildInventory();   // after the job is gone, or rows keep "Moving"
    }

    [RelayCommand]
    private void DismissReorg()
    {
        ClearReorgSurfaces();
        // Dismissing the pane also abandons a move that was only WAITING -- otherwise it would spring to life
        // later with no pane on screen to explain why files started moving. A waiting move that carried a
        // follow-up (make the new storage primary, 09-13) loses that too: say so, or the primary never changes
        // and nothing explains why.
        if (_deferredMove is { OnReachedEnd: not null })
            _root.Log("The waiting move was dismissed; its follow-up (changing the primary storage) will not run. Use Make Primary if you still want it.", category: LogCategory.Move);
        _deferredMove = null;
        MoveWaitingForSlot = false;
    }

    // Cancel is deliberately two-step: a reorg stopped mid-move shouldn't be abandoned by a stray click.
    [RelayCommand] private void CancelReorgResume() => ShowReorgCancelConfirm = true;
    [RelayCommand] private void BackFromReorgCancel() => ShowReorgCancelConfirm = false;

    [RelayCommand]
    private void ConfirmCancelReorg()
    {
        ShowReorgCancelConfirm = false;
        ShowReorgResumePrompt = false;
        // Stopping never silently abandons the plan: files were physically moved, and the honest options
        // (put them back, or accept them as orphans) both need the plan. Ask.
        OpenStopChoice();
    }

    /// <summary>The shell just released the activity slot: start the move that was waiting for it, if any.
    /// Posted, not inline: this runs inside the finally of the job that just ended, and starting the next one
    /// on this stack would nest the two runs and their gate handling.</summary>
    internal void StartDeferredMove()
    {
        if (_deferredMove is not { } next) return;
        _deferredMove = null;
        var all = next.AllFrom;
        Dispatcher.UIThread.Post(async () =>
        {
            MoveWaitingForSlot = false;
            var job = next.Job;
            // (09-19) "Move all" waited for files in progress: they have landed on the drive being emptied since the
            // plan was made. Plan again over what the drive holds NOW, so "all" is all.
            if (all is { } intent && _session.Manifest is { } store)
                try
                {
                    var root = _session.BackupRoot; var pending = _session.PendingWrites;
                    var again = await Task.Run(() =>
                    {
                        var layout = new Grog.Core.Volumes.BackupLayout(store.Current, root);
                        List<(LibraryItem, GameFile)> pairs;
                        using (store.Gate.Enter())
                            pairs = store.Current.Items.SelectMany(i => i.Files.Concat(i.OldVersionFiles).Select(f => (i, f)))
                                .Where(x => BackupScope.HoldsBytes(x.f) && (x.f.RootId ?? store.Current.PrimaryRootId) == intent.From).ToList();
                        var room = MoveRoomFor(store, pending, layout, intent.To);   // files that cannot fit are left in place
                        return Grog.Core.Volumes.ReorgPlanner.ForFileMoves(store.Current, layout, pairs, intent.To, room);
                    });
                    if (again.Moves.Count > 0 && again.UnavailableDrives.Count == 0)
                    {
                        job = again;
                        LogLeftInPlace(again, intent.To);
                    }
                }
                catch (Exception ex) { _root.Log($"Move: could not re-plan after the wait ({ex.Message}); moving the files planned earlier.", category: LogCategory.Move); }
            await RunReorgWithPaneAsync(job, next.ConfigDir, next.OnReachedEnd, next.Execute);
        });
    }

    /// <summary>Shutdown: pause the in-flight move so a close never corrupts a half-copied file.</summary>
    internal void PauseReorgForShutdown() { try { _reorgControl?.Pause(); } catch { } }
    private Task? _liveMoveTask;
    /// <summary>The move in flight, for a quit to wait on; completed when none is running.</summary>
    internal Task WhenMoveStopped => _liveMoveTask is { IsCompleted: false } t ? t : Task.CompletedTask;

    /// <summary>The move job in flight (or paused), for the shell's activity map.</summary>
    internal Grog.Core.Volumes.ReorgJob? ActiveReorgJob => _activeReorgJob;

    private bool BlockIfDrivesUnavailable(Grog.Core.Volumes.ReorgJob job)
    {
        if (!job.HasUnavailableDrives) return false;
        BlockMoveForDrives(job.UnavailableDrives);
        return true;
    }

    private void BlockMoveForDrives(System.Collections.Generic.IReadOnlyList<string> drives)
    {
        var names = drives.Count == 0 ? "backup storage" : string.Join(" and ", drives);
        var verb = drives.Count > 1 ? "are" : "is";
        _root.ShowToast($"Can't move: {names} {verb} offline or missing. Reconnect and try again - nothing was moved.", isError: true);
    }
}

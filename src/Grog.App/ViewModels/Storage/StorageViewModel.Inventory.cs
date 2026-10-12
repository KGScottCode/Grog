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

namespace Grog.App.ViewModels;

// Inventory: the two device grids, grouped selection, and the center move buttons.
public sealed partial class StorageViewModel
{
    // ---- Backups inventory: what's actually on each device, and moving it between them.
    // Placement policy decides where NEW files land; a manual move just overrides that for these files.
    public DeviceGridViewModel PrimaryGrid { get; } = new();
    public DeviceGridViewModel SecondaryGrid { get; } = new();

    public bool HasInventory => PrimaryGrid.HasFiles || SecondaryGrid.HasFiles;

    // Move affordances. Right sends Primary -> Secondary, left the reverse; "Selection" acts on highlighted
    // rows, "All" on the whole grid. Each lights up only when it can act -- the enabled state is the hint.
    private bool MovesBlocked => _root.Busy && !_root.DownloadRunning;
    public bool CanMoveSelToSecondary => PrimaryGrid.HasSelection && HasSecondary && !MovesBlocked;
    public bool CanMoveAllToSecondary => PrimaryGrid.HasFiles && HasSecondary && !MovesBlocked;
    public bool CanMoveSelToPrimary => SecondaryGrid.HasSelection && HasSecondary && !MovesBlocked;
    public bool CanMoveAllToPrimary => SecondaryGrid.HasFiles && HasSecondary && !MovesBlocked;

    // Imperative, counted menu labels: verbs with an object and a destination read as actions, and "All"
    // states the size of what it is about to move.
    private static string N(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";
    public string MoveAllToSecondaryLabel => $"Move all {N(PrimaryGrid.FileCount, "file")} to Secondary  \u2192";
    public string MoveSelToSecondaryLabel => $"Move {N(PrimaryGrid.SelectedCount, "selected file")}  \u2192";
    public string MoveAllToPrimaryLabel   => $"\u2190  Move all {N(SecondaryGrid.FileCount, "file")} to Primary";
    public string MoveSelToPrimaryLabel   => $"\u2190  Move {N(SecondaryGrid.SelectedCount, "selected file")}";
    internal void RebuildInventory() => Services.UiWatch.Time("RebuildInventory", RebuildInventoryCore);
    private void RebuildInventoryCore()
    {
        if (_session.Manifest is null) { PrimaryGrid.Clear(); SecondaryGrid.Clear(); RaiseInventory(); return; }
        InventoryRow.DeviceLookup = Grog.Core.Sync.ConditionRules.LookupFrom(_session.Manifest.Current);   // Missing vs Disconnected

        string primaryId = _session.Manifest.Current.PrimaryRootId ?? "";
        // The secondary SLOT is the first non-primary root that is not on the shelf: a Detached root has no
        // slot, and binding the grid to it listed its files as "elsewhere" beside an empty Secondary card.
        var secondaryRoot = _session.Manifest.Current.Roots.FirstOrDefault(r => r.Id != primaryId && r.State != RootState.Detached);
        PrimaryGrid.RootId = primaryId;
        PrimaryGrid.Label = _session.Manifest.Current.Roots.FirstOrDefault(r => r.Id == primaryId)?.Label ?? "Primary";
        SecondaryGrid.RootId = secondaryRoot?.Id ?? "";
        SecondaryGrid.Label = secondaryRoot?.Label ?? "Secondary";

        // unsettled moves indexed ONCE per rebuild: the per-file lookup was a scan of the
        // whole job (Files x Moves) on the dispatcher.
        _movingKeys = IsReorgRunning && _activeReorgJob is not null
            ? new HashSet<(long, string)>(_activeReorgJob.Moves.Where(mv => !mv.IsSettled).Select(mv => (mv.GogId, mv.FileKey)))
            : null;
        BuildGridRows(PrimaryGrid);
        BuildGridRows(SecondaryGrid);
        _movingKeys = null;
        RaiseInventory();
    }

    private void BuildGridRows(DeviceGridViewModel grid)
    {
        // The rebuild runs after every file that lands (a run of small files = one every second), and it
        // replaced the rows with fresh objects: every expanded game folded and every tick was lost, so the
        // user could not keep a game open long enough to pick a file. Keep what is the
        // user's -- expansion and selection -- by game id / file key and put it back on the new rows.
        var wasExpanded = new HashSet<long>(grid.GroupedRows.OfType<InventoryGroupRow>().Where(g => g.IsExpanded).Select(g => g.GogId));
        var wasSelected = new HashSet<(long, string)>(grid.Selected.Select(r => (r.Item.GogId, r.File.FileKey)));
        grid.Clear();
        if (_session.Manifest is null || string.IsNullOrEmpty(grid.RootId)) return;

        var groups = new List<InventoryGroupRow>();
        var vol = new Grog.Core.Volumes.VolumeService(_session.Manifest);
        var gridName = LabelOfRoot(grid.RootId);
        // Where else a game's files sit: the other slot by its role name, a detached drive by its label + "(detached)".
        var rootsById = _session.Manifest.Current.Roots.ToDictionary(r => r.Id);
        string ElsewhereLabel(string rootId)
            => rootsById.TryGetValue(rootId, out var r) && r.State == RootState.Detached ? $"{r.Label} (detached)" : SlotNameOf(rootId);
        long? gridFree = BackupLocations.FirstOrDefault(l => l.RootId == grid.RootId)?.FreeBytes;
        // Hiding conceals NAMES: a hidden game (the mature toggle, a per-game Hide) has no row here. Its
        // BYTES stay in the card's totals above the grid, which read the manifest, never these rows. "Move all"
        // moves the rows SHOWN, as it already does under the type filter, and its label states that count.
        var hiddenPolicy = _root.Library.Hidden;
        foreach (var item in _session.Manifest.Current.Items)
        {
            if (hiddenPolicy.IsHidden(item)) continue;
            var fileRows = new List<InventoryRow>();
            var elsewhere = new List<InventoryElsewhereRow>();
            foreach (var f in item.Files)
            {
                if (!Grog.Core.Sync.BackupScope.HasLocalCopy(f)) continue;   // inventory = bytes on disk, superseded included
                var on = f.RootId ?? _session.Manifest.Current.PrimaryRootId;
                if (on == grid.RootId) fileRows.Add(new InventoryRow(item, f) { IsMoving = IsFileMoving(item.GogId, f.FileKey) });
                else if (!string.IsNullOrEmpty(on)) elsewhere.Add(new InventoryElsewhereRow(f, ElsewhereLabel(on!)));   // the other slot, or a detached drive, named
            }
            if (fileRows.Count == 0) continue;

            // Completeness measures the game as a whole ("have" = present anywhere, "of" = in-scope total),
            // via the one scope predicate -- never a re-derived copy. A game split across devices still reads
            // Complete on each grid. File rows stay unscoped on purpose: bytes on disk are never hidden;
            // only the completeness fraction is scoped.
            var invScope = _root.Library.EffectiveScope;
            int ofCount = item.Files.Count(invScope.Includes);
            int haveCount = item.Files.Count(f => invScope.Includes(f)
                                                 && Grog.Core.Sync.BackupScope.HasLocalCopy(f));

            fileRows.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
            groups.Add(new InventoryGroupRow(item.Title, item.GogId, fileRows, haveCount, ofCount, elsewhere)
                {
                    Grid = grid,
                    DeviceName = gridName,
                    ConsolidateCommand = ConsolidateGameCommand,
                    QueueRemainingFilteredCommand = _root.QueueRemainingFilteredCommand,
                    QueueRemainingAllCommand = _root.QueueRemainingAllCommand,
                    MoveRemainingTopCommand = _root.MoveRemainingToTopCommand,
                    VerifyGameCommand = _root.VerifyGameCommand,
                    OpenGameFolderCommand = _root.OpenGameFolderCommand,
                    CopyGamePathsCommand = _root.CopyGamePathsCommand,
                    RedownloadGameCommand = _root.RedownloadGameCommand,
                    DeleteGameCommand = _root.Library.DeleteInventoryGameCommand,
                });
        }

        // Consolidate needs room HERE for the other device's bytes; measured against the card's free space.
        foreach (var g in groups)
        {
            if (!g.HasElsewhere) continue;
            if (g.Elsewhere.Any(e => rootsById.TryGetValue(e.File.RootId ?? "", out var r) && r.State == RootState.Detached))
                g.ConsolidateBlockedReason = "Some of its files are on Other storage. Give it a slot first.";
            else if (g.Elsewhere.Select(e => BackupLocations.FirstOrDefault(l => l.RootId == (e.File.RootId ?? _session.Manifest.Current.PrimaryRootId)))
                              .FirstOrDefault(l => l is { IsOnline: false }) is { } off)
                g.ConsolidateBlockedReason = $"{off.Label} is not connected. Reconnect it first.";
            else if (gridFree is null) g.ConsolidateBlockedReason = "This storage's free space is unknown.";
            else if (g.ElsewhereBytes > gridFree.Value)
                g.ConsolidateBlockedReason = $"Needs {Grog.Core.Format.ByteFormat.Size(g.ElsewhereBytes)}, {Grog.Core.Format.ByteFormat.Size(gridFree.Value)} free here.";
        }
        // FileRows is filled by SetGroups: it holds only the rows the type filter leaves visible, and only
        // the grid knows that state.
        foreach (var g in groups)
        {
            if (wasExpanded.Contains(g.GogId)) g.IsExpanded = true;
            foreach (var f in g.Files) if (wasSelected.Contains((f.Item.GogId, f.File.FileKey))) f.IsSelected = true;
            g.ReflectSelection();
        }
        grid.SetGroups(groups);   // lays out GroupedRows honoring the grid's current (sticky) sort + type filter
        foreach (var r in grid.FileRows) if (r.IsSelected) grid.Selected.Add(r);
        grid.Raise();   // header count/size, IsEmpty, HasFiles now reflect the populated rows
    }

    private DeviceGridViewModel Other(DeviceGridViewModel g) => g == PrimaryGrid ? SecondaryGrid : PrimaryGrid;

    [RelayCommand]
    private async Task ConsolidateGame(InventoryGroupRow? g)
    {
        if (g?.Grid is null || !g.HasElsewhere || _session.Manifest is null) return;
        if (!g.ConsolidateCanRun) { _root.ShowToast(g.ConsolidateTip, 1); return; }
        var item = _session.Manifest.Current.ItemById(g.GogId); if (item is null) return;
        var pairs = g.Elsewhere.Select(e => (item, e.File)).ToList();
        await MoveFilesTo(pairs, g.Grid.RootId, $"Consolidate {g.Title}");
    }

    // ---- Grouped-grid selection. Click a game selects it + all its files; click a file selects just it;
    // Ctrl+click on either extends/toggles. Un-checking any file drops its game out of "all selected".
    // Selecting in one grid clears the other, so the move direction is always unambiguous.
    public void ClickGroup(InventoryGroupRow g, bool ctrl, bool shift = false)
    {
        var grid = g.Grid; if (grid is null) return;
        if (shift && grid.SelAnchor is not null) { SelectRange(grid, grid.SelAnchor, g, additive: ctrl); return; }
        if (!ctrl) ClearSelection(grid);
        bool select = !g.Files.All(f => f.IsSelected);
        foreach (var f in g.Files) f.IsSelected = select;
        g.ReflectSelection();
        grid.SelAnchor = g;   // this click becomes the anchor for a subsequent Shift+click range
        SyncSelection(grid);
    }

    public void ClickFile(InventoryRow f, bool ctrl, bool shift = false)
    {
        var grid = f.Owner?.Grid; if (grid is null) return;
        if (shift && grid.SelAnchor is not null) { SelectRange(grid, grid.SelAnchor, f, additive: ctrl); return; }
        if (ctrl) f.IsSelected = !f.IsSelected;
        else { ClearSelection(grid); f.IsSelected = true; }
        f.Owner?.ReflectSelection();
        grid.SelAnchor = f;
        SyncSelection(grid);
    }

    private void SelectRange(DeviceGridViewModel grid, object anchor, object target, bool additive)
    {
        var rows = grid.GroupedRows;
        int i1 = rows.IndexOf(anchor), i2 = rows.IndexOf(target);
        if (i1 < 0 || i2 < 0) return;
        if (i1 > i2) (i1, i2) = (i2, i1);
        if (!additive) ClearSelection(grid);
        for (int i = i1; i <= i2; i++)
        {
            if (rows[i] is InventoryRow fr) fr.IsSelected = true;
            else if (rows[i] is InventoryGroupRow gr) foreach (var ff in gr.Files) ff.IsSelected = true;
        }
        foreach (var o in rows) if (o is InventoryGroupRow g) g.ReflectSelection();
        SyncSelection(grid);
    }

    public void ToggleGroupExpanded(InventoryGroupRow g)
    {
        g.IsExpanded = !g.IsExpanded;
        // The strip's caret derives from EVERY group, so a single row toggling changes it too; raise here --
        // a bound computed property with no raise never updates.
        PrimaryGrid.RaiseExpandAll(); SecondaryGrid.RaiseExpandAll();
    }

    private static void ClearSelection(DeviceGridViewModel grid)
    {
        foreach (var r in grid.FileRows) r.IsSelected = false;
        foreach (var o in grid.GroupedRows) if (o is InventoryGroupRow g) g.IsSelected = false;
        grid.Selected.Clear();
    }

    private void SyncSelection(DeviceGridViewModel grid)
    {
        grid.Selected.Clear();
        foreach (var r in grid.FileRows) if (r.IsSelected) grid.Selected.Add(r);
        if (grid.Selected.Count > 0) ClearSelection(Other(grid));
        grid.Raise();
        Other(grid).Raise();
        RaiseInventory();
    }

    private HashSet<(long GogId, string FileKey)>? _movingKeys;   // valid only during RebuildInventoryCore
    private bool IsFileMoving(long gogId, string fileKey)
        => _movingKeys is not null && _movingKeys.Contains((gogId, fileKey));

    internal void RaiseInventory()
    {
        OnPropertyChanged(nameof(HasInventory));
        OnPropertyChanged(nameof(CanMoveSelToSecondary)); OnPropertyChanged(nameof(CanMoveAllToSecondary));
        OnPropertyChanged(nameof(MoveAllToSecondaryLabel)); OnPropertyChanged(nameof(MoveSelToSecondaryLabel));
        OnPropertyChanged(nameof(MoveAllToPrimaryLabel)); OnPropertyChanged(nameof(MoveSelToPrimaryLabel));
        OnPropertyChanged(nameof(CanMoveSelToPrimary)); OnPropertyChanged(nameof(CanMoveAllToPrimary));
    }

    public bool ClearInventorySelectionIfAny()
    {
        if (PrimaryGrid.Selected.Count == 0 && SecondaryGrid.Selected.Count == 0) return false;
        ClearSelection(PrimaryGrid); ClearSelection(SecondaryGrid);
        PrimaryGrid.Raise(); SecondaryGrid.Raise(); RaiseInventory();
        return true;
    }

    // ---- Center move buttons. Right = Primary->Secondary, left = Secondary->Primary.
    // AllowConcurrentExecutions: an async command is disabled while its last run is awaited, and that run is
    // the whole move, so "Selected" went dead until the first move ended. More files join the move in progress.
    [RelayCommand(AllowConcurrentExecutions = true)] private async Task MoveSelToSecondary() => await MoveRowsTo(PrimaryGrid.Selected.ToList(), SecondaryGrid.RootId);
    [RelayCommand(AllowConcurrentExecutions = true)] private async Task MoveAllToSecondary() { _moveAllFromRootId = PrimaryGrid.RootId; try { await MoveRowsTo(PrimaryGrid.FileRows.ToList(), SecondaryGrid.RootId); } finally { _moveAllFromRootId = null; } }
    [RelayCommand(AllowConcurrentExecutions = true)] private async Task MoveSelToPrimary() => await MoveRowsTo(SecondaryGrid.Selected.ToList(), PrimaryGrid.RootId);
    [RelayCommand(AllowConcurrentExecutions = true)] private async Task MoveAllToPrimary() { _moveAllFromRootId = SecondaryGrid.RootId; try { await MoveRowsTo(SecondaryGrid.FileRows.ToList(), PrimaryGrid.RootId); } finally { _moveAllFromRootId = null; } }
    private string? _moveAllFromRootId;

    public async Task MoveInventoryTo(DeviceGridViewModel source, string? targetRootId)
        => await MoveRowsTo(source.Selected.ToList(), targetRootId);

    public async Task MoveRowsTo(IReadOnlyList<InventoryRow> rows, string? targetRootId)
    {
        // A click that does nothing is a bug ("All" did nothing, no toast, no log). Every way
        // out of here says why, in the log at least.
        if (_session.Manifest is null) return;
        if (string.IsNullOrEmpty(targetRootId)) { _root.Log("Move: no destination storage is registered.", isError: true, category: LogCategory.Move); return; }
        // Every row must actually leave its current device -- a no-op move (already on target) is skipped.
        var moving = rows.Where(r => (r.File.RootId ?? _session.Manifest.Current.PrimaryRootId) != targetRootId).ToList();
        var pairs = moving.Select(x => (x.Item, x.File)).ToList();
        // "Move all" empties a drive: its Old Versions archive goes too, or the irreplaceable old builds are
        // stranded there and read Missing once the drive is forgotten. The grid lists current files only, so they are
        // added here, for the whole-drive move alone (a selection is the user's pick), before the empty checks: a drive
        // whose grid is empty can still hold the archive.
        if (_moveAllFromRootId is { } fromRoot)
        {
            var m = _session.Manifest.Current;
            foreach (var item in m.Items)
                foreach (var ov in item.OldVersionFiles)
                    if (Grog.Core.Sync.BackupScope.HasLocalCopy(ov) && m.EffectiveRootId(ov) == fromRoot) pairs.Add((item, ov));
        }
        if (pairs.Count == 0)
        {
            _root.Log(rows.Count == 0 ? "Move: nothing selected to move." : "Move: every selected file is already on that storage.", category: LogCategory.Move);
            return;
        }
        await MoveFilesTo(pairs, targetRootId!, "Move");
    }

    internal static Grog.Core.Volumes.MoveRoom MoveRoomFor(JsonManifestStore manifest, Grog.Core.Volumes.PendingWrites? pending,
        Grog.Core.Volumes.IBackupLayout layout, string targetRootId)
    {
        var free = new Grog.Core.Volumes.VolumeService(manifest, pending).FreeBytesAfterPendingForRoot(targetRootId, layout) ?? long.MaxValue;
        var path = layout.RootPath(targetRootId);
        var max = path is null ? long.MaxValue : Grog.Core.Storage.DriveResolver.MaxFileBytes(path);
        var cluster = path is null ? 0L : Grog.Core.Storage.DriveResolver.ClusterBytes(path);
        if (free != long.MaxValue) free = Math.Max(0, free - Grog.Core.Download.DownloadEngine.FloorBytes);   // the floor nobody spends
        // Downloads queued onto the target land there too: the room is what is left once they have, at the disk's arithmetic.
        long queuedThere = 0;
        using (manifest.Gate.Enter())
        {
            var mm = manifest.Current;
            foreach (var q in mm.Downloads.Snapshot())
                if (q.Fits && (q.TargetRootId ?? mm.PrimaryRootId) == targetRootId && mm.ItemById(q.GogId)?.Files.FirstOrDefault(f => f.FileKey == q.FileKey) is { } qf)
                    queuedThere += Grog.Core.Storage.DriveResolver.OnDisk(Grog.Core.Volumes.PlacementPlanner.StillNeeds(qf), cluster);
        }
        if (free != long.MaxValue) free = Math.Max(0, free - queuedThere);
        return new Grog.Core.Volumes.MoveRoom(free, max, cluster);
    }

    private void LogLeftInPlace(Grog.Core.Volumes.ReorgJob job, string? targetRootId)
    {
        if (job.NotFitting.Count == 0) return;
        _root.Log($"{job.NotFitting.Count} file(s) left in place: no room on {LabelOfRoot(targetRootId)}", category: LogCategory.Move);
    }

    // ---- "Move what fits" confirm: the planner left files out; the user says whether the rest goes.
    [ObservableProperty] private bool _showMoveRoomConfirm;
    [ObservableProperty] private string _moveRoomText = "";
    private TaskCompletionSource<bool>? _moveRoomAnswer;

    private async Task<bool> AskMoveWhatFitsAsync(Grog.Core.Volumes.ReorgJob job, string label)
    {
        var total = job.Moves.Count + job.NotFitting.Count;
        var noRoom = job.NotFitting.Count(s => s.Reason == Grog.Core.Volumes.MoveRoom.NoRoomReason);
        var overLimit = job.NotFitting.Count - noRoom;
        var moveBytes = job.Moves.Sum(x => x.SizeBytes);
        var freeLeft = Math.Max(0L, (_lastMoveRoom?.FreeBytes ?? 0L) - job.Moves.Where(x => x.Kind == Grog.Core.Volumes.ReorgMoveKind.CopyVerifyDelete).Sum(x => x.SizeBytes));
        var parts = new List<string>();
        if (noRoom > 0) parts.Add($"{noRoom} {(noRoom == 1 ? "has" : "have")} no room ({ByteFormat.Size(freeLeft)} free)");
        if (overLimit > 0) parts.Add($"{overLimit} {(overLimit == 1 ? "is" : "are")} over {label}'s {ByteFormat.Size(_lastMoveRoom?.MaxFileBytes ?? 0L)} per-file limit");
        var lines = new List<string>
        {
            $"{job.Moves.Count} of {total} files will move to {label} ({ByteFormat.Size(moveBytes)}). {job.NotFitting.Count} will not: {string.Join(", ", parts)}.",
            "",
        };
        foreach (var s in job.NotFitting.Take(5)) lines.Add($"{s.FileName} ({ByteFormat.Size(s.SizeBytes)})");
        if (job.NotFitting.Count > 5) lines.Add($"and {job.NotFitting.Count - 5} more");
        MoveRoomText = string.Join("\n", lines);

        _moveRoomAnswer?.TrySetResult(false);   // an earlier move still asking is answered Cancel: one question at a time
        _moveRoomAnswer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ShowMoveRoomConfirm = true;
        return await _moveRoomAnswer.Task;
    }

    private Grog.Core.Volumes.MoveRoom? _lastMoveRoom;

    [RelayCommand] private void ConfirmMoveRoom() { ShowMoveRoomConfirm = false; _moveRoomAnswer?.TrySetResult(true); }
    [RelayCommand] private void CancelMoveRoom() { ShowMoveRoomConfirm = false; _moveRoomAnswer?.TrySetResult(false); }

    private async Task MoveFilesTo(List<(LibraryItem Item, GameFile File)> pairs, string targetRootId, string why)
    {
        if (_session.Manifest is null) return;
        Grog.Core.Volumes.ReorgJob job;
        // BackupLayout probes every root on disk: build it and plan off the dispatcher
        // (the planner is pure). The activity slot is taken inside RunReorgWithPaneAsync, as before.
        // captured once, used for the plan AND the config dir below.
        var manifest = _session.Manifest; var root = _session.BackupRoot;
        var pendingWrites = _session.PendingWrites;
        try
        {
            Grog.Core.Volumes.MoveRoom room;
            (job, room) = await Task.Run(() =>
            {
                var layout = new Grog.Core.Volumes.BackupLayout(manifest.Current, root);
                // A file that cannot fit is never queued: room is what is free after the writes a running move
                // still owes this drive, and the drive's per-file limit (4 GiB on FAT32).
                var r = MoveRoomFor(manifest, pendingWrites, layout, targetRootId);
                return (Grog.Core.Volumes.ReorgPlanner.ForFileMoves(manifest.Current, layout, pairs, targetRootId!, r), r);
            });
            _lastMoveRoom = room;
        }
        catch (Exception ex)
        {
            _root.Log($"Move: could not plan the move: {ex.Message}", isError: true, category: LogCategory.Move);
            _root.ShowToast($"Can't plan that move: {ex.Message}", 2);
            return;
        }
        if (BlockIfDrivesUnavailable(job)) return;
        if (job.NotFitting.Count > 0)
        {
            var label = LabelOfRoot(targetRootId);
            if (job.Moves.Count == 0)
            {
                _root.Log($"{why}: none of the {job.NotFitting.Count} file(s) fit on {label}: {job.NotFitting[0].Reason}.", isError: true, category: LogCategory.Move);
                _root.ShowToast($"Nothing to move: none of those files fit on {label}: {job.NotFitting[0].Reason}.", 1);
                return;
            }
            // Inform, never choose: the user says whether the part that fits goes.
            if (!await AskMoveWhatFitsAsync(job, label)) { _root.Log($"{why}: you canceled; {job.NotFitting.Count} of {job.Moves.Count + job.NotFitting.Count} file(s) would not fit on {label}.", category: LogCategory.Move); return; }
            _root.Log($"You moved what fits: {job.Moves.Count} of {job.Moves.Count + job.NotFitting.Count} files; {job.NotFitting.Count} left in place.", category: LogCategory.Move);
        }
        if (job.Moves.Count == 0)
        {
            _root.Log($"{why}: {pairs.Count} file(s) selected but none could be planned onto that storage.", isError: true, category: LogCategory.Move);
            _root.ShowToast("Nothing to move: none of those files could be planned onto that storage. See the Activity log.", 1);
            return;
        }

        // RunReorgWithPaneAsync populates the Moving pane on the UI thread BEFORE the copy starts, then
        // updates it per file as the runner settles each one.
        if (!ReferenceEquals(_session.Manifest, manifest) || _session.BackupRoot != root) { _root.Log("Move: storage changed while planning; not started.", category: LogCategory.Move); return; }   // 
        var configDir = await Task.Run(() => Grog.Core.Storage.GrogPaths.Resolve(root).ConfigDir);   // (same captured root as the layout)
        var targetLabel = LabelOfRoot(targetRootId);
        // The other half of running side by side: a move onto a drive that queued downloads will also land on
        // is priced against the room left AFTER them. Said up front; the copy itself is the last gate (a file that
        // does not fit stays where it was, and Resume retries it).

        // A move is already running: these files JOIN it, whichever way they are going. The runner
        // picks its next file from the job's list on every pass, so the list can grow under it.
        if (TryJoinRunningMove(job, targetRootId, targetLabel)) return;
        var allFrom = _moveAllFromRootId is { } from ? (from, targetRootId) : ((string, string)?)null;
        await RunReorgWithPaneAsync(job, configDir, allFrom: allFrom);
    }
}

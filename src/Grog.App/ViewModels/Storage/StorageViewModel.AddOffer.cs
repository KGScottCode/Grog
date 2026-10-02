// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Format;
using Grog.Core.Volumes;

namespace Grog.App.ViewModels;

/// <summary>
/// "New storage added" (owner, 09-13). Right after a second storage is added while the queue held files with
/// nowhere to go, the queue has just been re-planned onto the new drive (RefreshFromManifest -> the idle
/// RefreshBackupLocations branch runs ReplanWholeQueue). The one open question is what happens to the files
/// already on the old primary, and this asks it once, with measured numbers:
///   1. Leave them (default; nothing moves; K games end up split across the two drives).
///   2. Move the split games: move only the K split games' present files to the new drive.
///   3. Move everything to the new drive and make it primary.
/// Choices 2 and 3 run through the ordinary move engine (copy, verify, delete source, resumable, visible in the
/// Moving pane). The arithmetic is Core's (<see cref="StorageAdditionOffer"/>); this file only asks and dispatches.
/// </summary>
public sealed partial class StorageViewModel
{
    [ObservableProperty] private bool _showStorageAddOffer;
    [ObservableProperty] private int _addOfferChoice;   // 0 = leave, 1 = split games, 2 = move all + primary
    private StorageAdditionOffer.Result? _addOffer;
    private string? _addOfferNewRootId, _addOfferOldRootId;
    private string _addOfferNewPath = "", _addOfferOldPath = "", _addOfferNewLabel = "", _addOfferOldLabel = "";
    private long _addOfferFree;
    /// <summary>Captured when the add begins: the offer exists only when the queue HAD rows with nowhere to go.</summary>
    private bool _addOfferQueueHadWontFit;

    public string AddOfferTitle => $"New storage added: {_addOfferNewPath} · {ByteFormat.Size(_addOfferFree)} free";
    public string AddOfferBody => _addOffer is { } r
        ? $"The {r.QueuedToNewCount} queued file{(r.QueuedToNewCount == 1 ? "" : "s")} ({ByteFormat.Size(r.QueuedToNewBytes)}) will download there when you Resume. "
        + $"What about the {r.PrimaryFileCount} file{(r.PrimaryFileCount == 1 ? "" : "s")} ({ByteFormat.Size(r.PrimaryBytes)}) already on {_addOfferOldLabel} ({_addOfferOldPath})?"
        : "";
    public string AddOfferLeaveText => _addOffer is { } r
        ? (r.SplitGameCount > 0
            ? $"Nothing moves. {r.SplitGameCount} game{(r.SplitGameCount == 1 ? "" : "s")} will end up with files on both drives."
            : "Nothing moves. No game will be split across the two drives.")
        : "";
    public string AddOfferWholeText => _addOffer is { } r
        ? $"Move only the {r.SplitGameCount} game{(r.SplitGameCount == 1 ? "" : "s")} that still {(r.SplitGameCount == 1 ? "has" : "have")} files to download "
        + $"({r.SplitGameFiles.Count} file{(r.SplitGameFiles.Count == 1 ? "" : "s")}, {ByteFormat.Size(r.SplitGameBytes)}) to the new storage. Later downloads follow your Settings."
        + (r.ExtrasHeldBackByPin > 0 ? $" {r.ExtrasHeldBackByPin} extra{(r.ExtrasHeldBackByPin == 1 ? " stays" : "s stay")} put: your Extras setting keeps them on {_addOfferOldLabel}." : "")
        : "";
    public string AddOfferMoveAllText => _addOffer is { } r
        ? $"{r.PrimaryFileCount} file{(r.PrimaryFileCount == 1 ? "" : "s")}, {ByteFormat.Size(r.PrimaryBytes)}. {_addOfferOldLabel} ({_addOfferOldPath}) ends up empty and can be detached."
        : "";
    public bool AddOfferCanKeepWhole => _addOffer is { HasSplitGames: true };
    public bool AddOfferCanMoveAll => _addOffer is { PrimaryFileCount: > 0 };
    public string AddOfferActionLabel => AddOfferChoice switch { 1 => "Move Split Games", 2 => "Move Everything & Make Primary", _ => "Continue" };

    public bool AddOfferChoiceLeave   { get => AddOfferChoice == 0; set { if (value) AddOfferChoice = 0; } }
    public bool AddOfferChoiceWhole   { get => AddOfferChoice == 1; set { if (value) AddOfferChoice = 1; } }
    public bool AddOfferChoiceMoveAll { get => AddOfferChoice == 2; set { if (value) AddOfferChoice = 2; } }
    [RelayCommand] private void SetAddOfferChoice(string? which) { if (int.TryParse(which, out var n)) AddOfferChoice = n; }
    partial void OnAddOfferChoiceChanged(int value)
    {
        OnPropertyChanged(nameof(AddOfferChoiceLeave)); OnPropertyChanged(nameof(AddOfferChoiceWhole));
        OnPropertyChanged(nameof(AddOfferChoiceMoveAll)); OnPropertyChanged(nameof(AddOfferActionLabel));
    }

    /// <summary>Called by both add paths once the manifest is refreshed. Shows the dialog only when the queue had
    /// won't-fit rows before the add AND at least one of them now targets the new root. Never during an unpaused
    /// run (nothing was re-planned then). Measured, not projected: the numbers are the persisted queue's.</summary>
    private void MaybeOfferStorageAddition(string newRootId)
    {
        if (_session.Manifest is null || !_addOfferQueueHadWontFit) return;
        _addOfferQueueHadWontFit = false;
        if (_session.LiveEngine is not null && !_session.StopRequested) return;
        var m = _session.Manifest.Current;
        var oldPrimary = m.PrimaryRootId;
        if (string.IsNullOrEmpty(oldPrimary) || oldPrimary == newRootId) return;
        if (!StorageAdditionOffer.ShouldOffer(m, newRootId)) return;

        _addOffer = StorageAdditionOffer.Compute(m, newRootId, oldPrimary!);
        _addOfferNewRootId = newRootId; _addOfferOldRootId = oldPrimary;
        var newRow = BackupLocations.FirstOrDefault(d => d.RootId == newRootId);
        var oldRow = BackupLocations.FirstOrDefault(d => d.RootId == oldPrimary);
        _addOfferNewPath = newRow?.Path ?? m.Roots.FirstOrDefault(r => r.Id == newRootId)?.PathHint ?? "";
        _addOfferOldPath = oldRow?.Path ?? m.Roots.FirstOrDefault(r => r.Id == oldPrimary)?.PathHint ?? "";
        _addOfferNewLabel = newRow?.Label ?? "the new storage"; _addOfferOldLabel = oldRow?.Label ?? "Primary";
        _addOfferFree = newRow?.FreeBytes ?? 0;
        AddOfferChoice = 0;
        foreach (var p in new[] { nameof(AddOfferTitle), nameof(AddOfferBody), nameof(AddOfferLeaveText), nameof(AddOfferWholeText),
                 nameof(AddOfferMoveAllText), nameof(AddOfferCanKeepWhole), nameof(AddOfferCanMoveAll), nameof(AddOfferActionLabel),
                 nameof(AddOfferChoiceLeave), nameof(AddOfferChoiceWhole), nameof(AddOfferChoiceMoveAll) })
            OnPropertyChanged(p);
        ShowStorageAddOffer = true;
    }

    /// <summary>HeadlessShots seeds a finished offer to render the dialog.</summary>
    internal void SeedStorageAddOfferForShots(StorageAdditionOffer.Result r, string newPath, string oldPath, long free)
    {
        _addOffer = r; _addOfferNewPath = newPath; _addOfferOldPath = oldPath; _addOfferNewLabel = "Secondary"; _addOfferOldLabel = "Primary"; _addOfferFree = free;
        AddOfferChoice = 0;
        foreach (var p in new[] { nameof(AddOfferTitle), nameof(AddOfferBody), nameof(AddOfferLeaveText), nameof(AddOfferWholeText),
                 nameof(AddOfferMoveAllText), nameof(AddOfferCanKeepWhole), nameof(AddOfferCanMoveAll), nameof(AddOfferActionLabel) })
            OnPropertyChanged(p);
        ShowStorageAddOffer = true;
    }

    [RelayCommand]
    private async Task ContinueStorageAddOffer()
    {
        ShowStorageAddOffer = false;
        var offer = _addOffer; var newId = _addOfferNewRootId; var oldId = _addOfferOldRootId; int choice = AddOfferChoice;
        _addOffer = null; _addOfferNewRootId = _addOfferOldRootId = null;
        if (offer is null || newId is null || oldId is null || _session.Manifest is null) return;
        if (choice == 0) { _root.Log($"New storage added; files already on {_addOfferOldLabel} stay where they are.", category: LogCategory.Move); return; }

        var files = choice == 1 ? offer.SplitGameFiles : offer.AllPrimaryFiles;
        if (files.Count == 0) return;
        var manifest = _session.Manifest; var root = _session.BackupRoot; var pending = _session.PendingWrites;
        Grog.Core.Volumes.ReorgJob job; string configDir;
        try
        {
            // BackupLayout probes every root on disk; the planner is pure. Both off the dispatcher (UI-thread sweep 09-06).
            (job, configDir) = await Task.Run(() =>
            {
                var layout = new BackupLayout(manifest.Current, root);
                var room = MoveRoomFor(manifest, pending, layout, newId);   // files that cannot fit are left in place
                var planned = ReorgPlanner.ForFileMoves(manifest.Current, layout, files, newId, room);
                return (planned, Grog.Core.Storage.GrogPaths.Resolve(root).ConfigDir);
            });
        }
        catch (Exception ex) { ShowLocationError("Couldn't plan the move", ex); return; }
        if (!ReferenceEquals(_session.Manifest, manifest) || _session.BackupRoot != root) return;
        if (BlockIfDrivesUnavailable(job)) return;
        LogLeftInPlace(job, newId);
        if (job.Moves.Count == 0) { _root.Log("New storage: nothing could be planned onto it.", isError: true, category: LogCategory.Move); return; }

        // The move runs (or is DEFERRED behind a live backup) inside RunReorgWithPaneAsync; its per-file
        // outcome is logged there when it ends. The lines below say only what was asked and when it landed.
        var newLabel = _addOfferNewLabel; var oldLabel = _addOfferOldLabel; int splitGames = offer.SplitGameCount;
        _root.Log($"New storage: moving {job.Moves.Count} file{(job.Moves.Count == 1 ? "" : "s")}{(choice == 1 ? $" of {splitGames} split game{(splitGames == 1 ? "" : "s")}" : "")} to {newLabel}"
                  + (choice == 2 ? ", then making it the primary storage." : "."), category: LogCategory.Move);
        if (choice == 1)
        {
            // Only the files already on disk move; the games' remaining downloads follow Settings like any other.
            await RunReorgWithPaneAsync(job, configDir);
            return;
        }

        // MOVE FIRST, PROMOTE AT THE END (architect 09-13): a file with no RootId resolves to the primary, so
        // promoting first would make every such file read as already on the new drive while its bytes sit on the
        // old one. After the move every moved record carries an explicit RootId, so the promotion is safe. The
        // role pins follow the drive; the next reconcile re-plans the queue with the new primary first.
        await RunReorgWithPaneAsync(job, configDir, onReachedEnd: async () =>
        {
            await PromoteToPrimaryAsync(newId);
            new VolumeService(_session.Manifest!).RetargetRoles(oldId, newId);
            await _session.Manifest!.SaveAsync();
            _root.Log($"{newLabel} is now the primary storage. {oldLabel} can be detached once it reads empty.", category: LogCategory.Move);
        });
    }
}

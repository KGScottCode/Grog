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
using Avalonia.Platform.Storage;
using Grog.Core.Download;
using Grog.Core.Format;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Verify;
using Grog.App.Runtime;

namespace Grog.App.ViewModels;

// Roots: add / change / remove / re-point / make primary / removable / lost, and their dialogs.
public sealed partial class StorageViewModel
{
    /// <summary>Toggle a device's persisted "removable" flag; drives Missing-vs-Disconnected everywhere.</summary>
    [RelayCommand]
    private async Task ToggleRemovable(BackupLocationRow? row)
    {
        if (row is null || _session.Manifest is null) return;
        var root = _session.Manifest.Current.Roots.FirstOrDefault(r => r.Id == row.RootId);
        if (root is null) return;
        root.Removable = !root.Removable;
        await _session.Manifest.SaveAsync();
        _root.RefreshFromManifest();   // RebuildRows re-assigns DetailFileRow.DeviceLookup, which is what Missing vs Disconnected reads
        RebuildInventory();
        _root.RecomputeActivity();
        _root.Log($"{row.Label} marked {(root.Removable ? "removable - its files show Disconnected when unplugged" : "not removable")}.");
    }

    [RelayCommand]
    private async Task AddLocation()
    {
        var top = _root.OwnerWindowResolver?.Invoke();
        if (top is null || _session.Manifest is null) return;
        string? path;
        try
        {
            Avalonia.Platform.Storage.IStorageFolder? start = null;
            try
            {
                // (UI-thread sweep 09-06) both Directory.Exists probes can hit a sleeping drive: run them off the dispatcher.
                var seedRoot = _session.BackupRoot;
                var seed = await Task.Run(() =>
                {
                    var s = (!string.IsNullOrEmpty(seedRoot) && System.IO.Directory.Exists(seedRoot))
                        ? seedRoot
                        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    return System.IO.Directory.Exists(s) ? s : null;
                });
                if (seed is not null)
                    start = await top.StorageProvider.TryGetFolderFromPathAsync(seed);
            }
            catch { start = null; }

            var folders = await top.StorageProvider.OpenFolderPickerAsync(
                new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Add a backup folder",
                    AllowMultiple = false,
                    SuggestedStartLocation = start,
                });
            path = folders.FirstOrDefault()?.TryGetLocalPath();
        }
        catch (Exception ex) { ShowLocationError("Couldn't open the folder picker", ex); return; }
        if (string.IsNullOrEmpty(path)) return;

        // Validate at the gate: a rejected folder must fail BEFORE we ask anything or do any work.
        var vol = new Grog.Core.Volumes.VolumeService(_session.Manifest);
        if (vol.FindSamePath(path!) is { } same)
        {
            string role = same.Id == _session.Manifest.Current.PrimaryRootId ? "your Primary storage"
                        : same.State == Grog.Core.Models.RootState.Detached ? "listed under Other storage; use it from there"
                        : "your Secondary storage";
            ShowLocationError($"That folder is already {role}:\n\n{same.PathHint}\n\nPick a different folder.");
            return;
        }
        var nested = vol.FindNestingConflict(path!);
        if (nested is not null)
        {
            ShowLocationError($"That folder is inside an existing backup folder:\n\n{nested.PathHint}\n\n"
                              + "One backup storage can't sit inside another. Pick a folder outside it.");
            return;
        }

        // Same physical drive is allowed but worth saying out loud: one failure loses both copies.
        var manifestAl = _session.Manifest;
        var sameVol = await Task.Run(() => vol.FindSameVolume(path!));   // (UI-thread sweep 09-06 r2) mount-point lookups (DriveInfo) off the dispatcher
        if (!ReferenceEquals(_session.Manifest, manifestAl)) return;   // (UI-thread sweep 09-06 r2) manifest swapped while probing
        PendingSameVolumeWarning = sameVol is not null
            ? "Heads up: this is on the same physical drive as your other storage, so a drive failure would lose both."
            : "";

        _pendingAddPath = path;
        // The "new storage added" offer (AddOffer.cs) exists only when the queue HAD files with nowhere to go.
        _addOfferQueueHadWontFit = _session.Manifest.Current.Downloads.AnyWontFit;
        AddIntentFolderName = System.IO.Path.GetFileName(path!.TrimEnd('/', '\\'));
        ShowAddIntent = true;
    }

    // ---- location errors surface as a modal; a status-bar line is too easy to miss ----
    [ObservableProperty] private bool _showLocationErrorModal;
    [ObservableProperty] private string _locationErrorText = "";
    [ObservableProperty] private string _pendingSameVolumeWarning = "";
    internal void ShowLocationError(string message)
    {
        LocationErrorText = message;
        ShowLocationErrorModal = true;
    }
    /// <summary>A failed storage action: the dialog gets the message, the log gets the whole exception (site
    /// and path), because "Access to the path is denied" alone is undiagnosable (owner-hit 09-17).</summary>
    private void ShowLocationError(string what, Exception ex)
    {
        _root.Log($"{what}: {ex}", isError: true, category: LogCategory.Move);
        ShowLocationError($"{what}: {ex.Message}");
    }
    [RelayCommand] private void CloseLocationError() => ShowLocationErrorModal = false;

    // ---- "Add location" intent: existing backups (register + import-match) vs new empty location ----
    private string? _pendingAddPath;
    [ObservableProperty] private bool _showAddIntent;
    [ObservableProperty] private string _addIntentFolderName = "";
    /// <summary>The add-intent dialog title: "Use <folder>?" (a ConfirmDialog title is one string).</summary>
    public string AddIntentTitle => $"Use {AddIntentFolderName}?";
    partial void OnAddIntentFolderNameChanged(string value) => OnPropertyChanged(nameof(AddIntentTitle));

    [RelayCommand] private void CancelAddIntent() { ShowAddIntent = false; _pendingAddPath = null; }

    /// <summary>Register the folder WITHOUT the import scan (owner 08-30): for a folder the user knows is
    /// empty, forcing a scan is busywork. Rescan &amp; Verify later covers a wrong guess.</summary>
    [RelayCommand]
    private async Task AddIntentSkipScan()
    {
        ShowAddIntent = false;
        if (_session.Manifest is null || string.IsNullOrEmpty(_pendingAddPath)) return;
        var path = _pendingAddPath!;
        _pendingAddPath = null;
        try
        {
            // (UI-thread sweep 09-06 r2) AddRoot's CreateDirectory touches the just-picked drive: pre-create off the
            // dispatcher, then AddRoot (manifest mutation) on the UI thread finds the folder present.
            var manifest = _session.Manifest;
            await Task.Run(() => System.IO.Directory.CreateDirectory(path));
            if (!ReferenceEquals(_session.Manifest, manifest)) return;
            var added = new Grog.Core.Volumes.VolumeService(_session.Manifest).AddRoot(path);
            await _session.Manifest.SaveAsync();
            _root.RefreshFromManifest();   // the secondary grid must learn its root id or the move buttons stay dead
            RebuildInventory();            // (09-14) RefreshFromManifest refreshes the CARD, never the grid rows
            _root.Log("Added storage (scan skipped).");
            await _root.Overview.ReplanQueueAsync("the new storage");   // (09-25) the offer reads the flags: decide them first
            MaybeOfferStorageAddition(added.Id);
        }
        catch (Exception ex) { ShowLocationError("Couldn't add that folder", ex); }
    }

    /// <summary>Register the folder AND scan it for existing GOG backups: confident matches are adopted
    /// (stamped with the new root's id), problems surface in the results screen for Find-match.</summary>
    [RelayCommand]
    private async Task AddIntentExisting()
    {
        ShowAddIntent = false;
        if (_session.Manifest is null || string.IsNullOrEmpty(_pendingAddPath)) return;
        var path = _pendingAddPath!;
        _pendingAddPath = null;
        try
        {
            // (S2.2) Core's ImportRun registers the root and either defers the import (empty library: the flag
            // lives on the manifest so a restart before the first scan does not forget the promise) or plans and
            // adopts now. Disk work throughout: off the dispatcher, applied after an identity re-check.
            var manifest = _session.Manifest;
            var result = await Task.Run(() => Grog.Core.Runs.ImportRun.AddAndImportAsync(manifest, path,
                Grog.Core.Verify.ImportMode.Adopt, new MainWindowViewModel.AppRunHost(_root, LogCategory.General), _session.Cts?.Token ?? default,
                new Grog.Core.Runs.ImportRun.Options { VerifyAdopted = false }));   // (09-19) the App verifies AFTER the refresh, as a visible activity (below)
            if (!ReferenceEquals(_session.Manifest, manifest)) return;
            if (result.Status == Grog.Core.Runs.ImportRunStatus.Deferred) { _root.RefreshFromManifest(); RebuildInventory(); return; }
            _root.RefreshFromManifest();   // re-plans the queue onto the new root (idle branch) before the offer reads it
            RebuildInventory();            // (09-14) the card counted the adopted files while the grid still said "Nothing backed up here yet"
            await ShowImportOutcome(result, interactive: true);
            await _root.Overview.ReplanQueueAsync("the new storage");   // (09-25) the offer reads the flags: decide them first
            MaybeOfferStorageAddition(result.Root.Id);
            await _root.VerifyAdoptedAsync(result, ImportVerifyChecksums);
        }
        catch (Exception ex) { ShowLocationError("Couldn't add that folder", ex); }
    }

    // ---- Make primary: confirm first (changes the default destination for new content) ----
    private BackupLocationRow? _pendingMakePrimary;
    [ObservableProperty] private bool _showMakePrimaryWarning;
    public string MakePrimaryText { get; private set; } = "";

    [RelayCommand]
    private void RequestMakePrimary(BackupLocationRow? row)
    {
        if (row is null || row.IsPrimary) return;
        if (MoveTouchesRoot(row.RootId)) return;
        _pendingMakePrimary = row;
        MakePrimaryText =
            $"Make \"{row.Label}\" the primary backup folder?\n\nNew downloads will go here by default. "
            + "Files already in your other storage stay exactly where they are -- nothing moves.";
        OnPropertyChanged(nameof(MakePrimaryText));
        ShowMakePrimaryWarning = true;
    }

    [RelayCommand]
    private void CancelMakePrimary() { ShowMakePrimaryWarning = false; _pendingMakePrimary = null; }

    [RelayCommand]
    private async Task ConfirmMakePrimary()
    {
        ShowMakePrimaryWarning = false;
        var row = _pendingMakePrimary;
        _pendingMakePrimary = null;
        if (row is null || row.IsPrimary || _session.Manifest is null) return;
        try
        {
            await PromoteToPrimaryAsync(row.RootId);
            await _session.Manifest.SaveAsync();
            // A slot change like any other (QA 09-18): RefreshFromManifest alone left the grids under the old root
            // ids (the PRIMARY column listed the new secondary's files) and a live run kept its old targets.
            await AfterSlotChangeAsync($"{row.DriveName} is now the Primary storage.");
        }
        catch (Exception ex) { ShowLocationError("Couldn't set primary", ex); }
    }

    // ---- Delete a device: 3 choices -- (0) move its files to the other device then delete, (1) leave its
    //      files on the drive, (2) delete the device AND its files (requires typing DELETE). Removing a
    //      device marks ALL its files "Not downloaded" until re-backed-up or re-adopted; an empty device
    //      is a plain remove (no choice, no gate).
    private BackupLocationRow? _pendingDeleteRow;
    [ObservableProperty] private bool _showDeleteDeviceDialog;
    /// <summary>The destructive "delete files and remove storage" path is visible while it runs (09-13): on a slow
    /// USB drive it took 30+ seconds with nothing on screen, and read as broken. Count and bytes tick per file.</summary>
    [ObservableProperty] private bool _showDeleteDeviceProgress;
    [ObservableProperty] private string _deleteDeviceProgressTitle = "";
    [ObservableProperty] private string _deleteDeviceProgressText = "";
    [ObservableProperty] private double _deleteDeviceProgressValue;
    public string DeleteDeviceProgressPercent => $"{DeleteDeviceProgressValue:F0}%";
    partial void OnDeleteDeviceProgressValueChanged(double value) => OnPropertyChanged(nameof(DeleteDeviceProgressPercent));
    [ObservableProperty] private int _deleteChoice;              // 0=move, 1=leave, 2=delete files, 3=detach (keep tracking)
    [ObservableProperty] private string _deleteTypedConfirm = "";

    private BackupLocationRow? OtherDeviceForDelete => _pendingDeleteRow is { } r
        ? BackupLocations.FirstOrDefault(d => d.RootId != r.RootId) : null;

    // VOCABULARY (owner 09-13): "Removable" and "Disconnected" are PHYSICAL words (the drive type, and whether
    // it is plugged in). Grog's own relationship to a folder uses none of them: Detach (shelf, still counted),
    // Forget (stop tracking, files stay), Move & Forget, Delete (erase). The button and this dialog say Detach.
    public string DeleteDeviceTitle => _pendingDeleteRow is { } r ? (r.IsPrimary ? $"Forget the {r.Label} storage?" : $"Detach the {r.Label} storage?") : "";

    public bool DeleteHasFiles => (_pendingDeleteRow?.FileCount ?? 0) > 0;
    public string DeleteOtherLabel => OtherDeviceForDelete?.Label ?? "Primary";
    /// <summary>(09-15 review) "Move & Forget" needs another device; with a lone primary it silently ran as Forget.</summary>
    public bool DeleteCanMove => OtherDeviceForDelete is not null;
    /// <summary>Erase is offered only while the drive is here: on an absent drive every File.Exists is false, so
    /// "Delete" removed 0 files and then forgot the records, stranding the bytes (QA 09-18). A root folder absent
    /// from disk is an unplugged drive, not a missing file.</summary>
    public bool DeleteCanErase => _pendingDeleteRow is { IsOnline: true };
    public string DeleteMoveOptionText => $"Move its files to {DeleteOtherLabel}, then forget this storage";
    public string DeleteSummary
    {
        get
        {
            if (_pendingDeleteRow is not { } r) return "";
            return r.FileCount > 0
                ? $"{r.FileCount} file{(r.FileCount == 1 ? "" : "s")} · {Grog.Core.Format.ByteFormat.Size(r.GrogBytes)} are backed up in this storage ({r.Path}). Choose what happens to them:"
                : r.IsPrimary
                    ? $"This folder holds no tracked files ({r.Path}). Forgetting it just drops it from your storage - nothing on disk is touched."
                    : $"This folder holds no tracked files ({r.Path}). Detaching it moves it to Other storage; nothing on disk is touched, and you can use it in a slot again or forget it from there.";
        }
    }
    public string DeleteLeaveNote => (_pendingDeleteRow is { FileCount: > 0 } r
        ? $"No data touched. But its {r.FileCount} file{(r.FileCount == 1 ? "" : "s")} will show as “Not downloaded” until you back {(r.FileCount == 1 ? "it" : "them")} up again or re-add this storage (Grog re-adopts the files then - no re-download)."
        : "No data touched.") + (_pendingDeleteRow is { } pr ? PartialsStayNote(pr.RootId).Replace("\n\n", " ") : "");
    public string DeleteDestructiveNote => _pendingDeleteRow is { } r && r.FileCount > 0
        ? $"Destructive - the {r.FileCount} file{(r.FileCount == 1 ? "" : "s")} in this storage {(r.FileCount == 1 ? "is" : "are")} permanently deleted. This can't be undone."
        : "Destructive - files in this storage are permanently deleted.";
    /// <summary>Detach (09-13): the drive goes on the shelf with its files still counted as backed up. Not for the
    /// primary (something must hold the slot) and pointless for an empty folder.</summary>
    public bool DeleteCanDetach => _pendingDeleteRow is { IsPrimary: false };
    public string DeleteDetachNote => _pendingDeleteRow is { } r
        ? $"Nothing touched, nothing re-downloaded. Its {r.FileCount} file{(r.FileCount == 1 ? "" : "s")} stay{(r.FileCount == 1 ? "s" : "")} recorded as backed up on {r.Label}, the slot frees for another drive, and it waits under Other storage on this page until you use it again." + PartialsCarryNote(r.RootId)
        : "";
    /// <summary>(09-19) Detach carries the drive's in-progress downloads to where each file belongs now; the dialog says so.</summary>
    private string PartialsCarryNote(string rootId)
    {
        if (_session.Manifest is not { } st) return "";
        var (n, bytes) = Grog.Core.Volumes.PartialCarry.RecordedOn(st.Current, rootId);
        return n == 0 ? "" : $" Its {n} in-progress download{(n == 1 ? "" : "s")} ({Grog.Core.Format.ByteFormat.Size(bytes)}) {(n == 1 ? "is" : "are")} carried to your other storage; any that cannot go restart from zero.";
    }
    public bool DeleteRequiresTyped => DeleteChoice == 2;
    public bool DeleteConfirmed => !DeleteRequiresTyped || string.Equals(DeleteTypedConfirm.Trim(), "DELETE", StringComparison.Ordinal);
    public string DeleteActionLabel => !DeleteHasFiles ? (DeleteCanDetach ? "Detach Storage" : "Forget Storage")
        // Every branch NAMES ITS OBJECT, and the fallback is the SAFE action -- an unnamed destructive
        // verb is the one that gets clicked by accident.
        : DeleteChoice switch { 0 => "Move & Forget", 1 => "Forget Storage", 2 => "Delete Files", 3 => "Detach Storage", _ => "Forget Storage" };
    public bool DeleteActionIsDestructive => DeleteHasFiles && DeleteChoice == 2;

    // Two-way radio helpers (Avalonia has no int-to-bool converter). Setting one to true picks that choice.
    public bool DeleteChoiceMove   { get => DeleteChoice == 0; set { if (value) DeleteChoice = 0; } }
    public bool DeleteChoiceLeave  { get => DeleteChoice == 1; set { if (value) DeleteChoice = 1; } }
    public bool DeleteChoiceDelete { get => DeleteChoice == 2; set { if (value) DeleteChoice = 2; } }
    public bool DeleteChoiceDetach { get => DeleteChoice == 3; set { if (value) DeleteChoice = 3; } }

    partial void OnDeleteChoiceChanged(int value)
    {
        OnPropertyChanged(nameof(DeleteRequiresTyped)); OnPropertyChanged(nameof(DeleteConfirmed));
        OnPropertyChanged(nameof(DeleteActionLabel)); OnPropertyChanged(nameof(DeleteActionIsDestructive));
        OnPropertyChanged(nameof(DeleteChoiceMove)); OnPropertyChanged(nameof(DeleteChoiceLeave)); OnPropertyChanged(nameof(DeleteChoiceDelete));
        OnPropertyChanged(nameof(DeleteChoiceDetach));
    }
    partial void OnDeleteTypedConfirmChanged(string value) => OnPropertyChanged(nameof(DeleteConfirmed));

    [RelayCommand] private void SetDeleteChoice(string? which) { if (int.TryParse(which, out var n)) DeleteChoice = n; }

    /// <summary>Non-blocking heads-up (owner 08-30): re-plan the live queue WITHOUT the device being
    /// removed, and say when the queued backup can no longer finish. Never blocks the removal --
    /// the user may be about to attach a different device.</summary>
    [ObservableProperty] private string _deleteQueueImpactText = "";
    public bool ShowDeleteQueueImpact => DeleteQueueImpactText.Length > 0;

    private string BuildDeleteQueueImpact(BackupLocationRow row)
    {
        try
        {
            if (_session.Manifest is null) return "";
            var m = _session.Manifest.Current;
            var queuedFiles = new List<GameFile>();
            foreach (var q in m.Downloads.Snapshot())
                if (_root.Library.ItemById(q.GogId) is { } qi
                    && qi.Files.FirstOrDefault(f => f.FileKey == q.FileKey) is { } gf)
                    queuedFiles.Add(gf);
            if (queuedFiles.Count == 0) return "";

            var devices = BackupLocations
                .Where(d => d.RootId != row.RootId)
                .OrderByDescending(d => d.RootId == m.PrimaryRootId)
                .Select(d => Grog.Core.Volumes.DeviceSpace.ForRoot(d.RootId, d.FreeBytes, d.IsOnline, d.Path))
                .ToList();
            var plan = Grog.Core.Volumes.PlacementPlanner.PlanQueueDetailed(
                queuedFiles, devices, m.Routing, m.PrimaryRootId!, m.Routing.Mode, m.ExtrasLayout);
            if (plan.ShortfallBytes <= 0) return "";

            int n = plan.Placements.Count(p => !p.Fits);
            return $"The queued backup can't finish once {row.Label} is removed: {n} file{(n == 1 ? "" : "s")} / "
                 + $"{Grog.Core.Format.ByteFormat.Size(plan.ShortfallBytes)} will have nowhere to go.";
        }
        catch { return ""; }   // a heads-up must never break the dialog
    }

    [RelayCommand]
    private void RequestDeleteDevice(BackupLocationRow? row)
    {
        if (row is null) return;
        if (MoveTouchesRoot(row.RootId)) return;
        _pendingDeleteRow = row;
        DeleteQueueImpactText = BuildDeleteQueueImpact(row);
        OnPropertyChanged(nameof(ShowDeleteQueueImpact));
        // Default = the choice that changes the least: Detach when it is offered, else Forget (empty folder).
        DeleteChoice = row.IsPrimary ? (row.FileCount > 0 && OtherDeviceForDelete is not null ? 0 : 1) : 3;   // (09-15) Move needs somewhere to move to
        DeleteTypedConfirm = "";
        foreach (var p in new[] { nameof(DeleteDeviceTitle), nameof(DeleteHasFiles), nameof(DeleteOtherLabel), nameof(DeleteCanMove),
                 nameof(DeleteMoveOptionText), nameof(DeleteSummary), nameof(DeleteLeaveNote), nameof(DeleteDestructiveNote),
                 nameof(DeleteRequiresTyped), nameof(DeleteConfirmed), nameof(DeleteActionLabel), nameof(DeleteActionIsDestructive),
                 nameof(DeleteChoiceMove), nameof(DeleteChoiceLeave), nameof(DeleteChoiceDelete),
                 nameof(DeleteChoiceDetach), nameof(DeleteCanDetach), nameof(DeleteDetachNote), nameof(DeleteCanErase) })
            OnPropertyChanged(p);
        ShowDeleteDeviceDialog = true;
    }

    [RelayCommand] private void CancelDeleteDevice() { ShowDeleteDeviceDialog = false; _pendingDeleteRow = null; }

    // ---- OTHER STORAGE (09-13/14): a drive with no slot. Use as <slot> when free, Swap with <slot> when held,
    //      Forget is the plain remove. Files never move; the queue is re-planned after every slot change. ----
    /// <summary>The rail's "N other storage" lands here: the section shows (it is hidden otherwise; owner 09-15),
    /// every drive expanded. Leaving the page, or its own close, hides it again. The empty-slot offer is separate
    /// and always shown.</summary>
    public event Action? ScrollToOtherStorageRequested;
    [ObservableProperty] private bool _showOtherStorageSection;
    internal void RevealOtherStorage()
    {
        foreach (var o in OtherStorage) o.IsExpanded = true;
        ShowOtherStorageSection = true;
        ScrollToOtherStorageRequested?.Invoke();
    }
    internal void HideOtherStorage() => ShowOtherStorageSection = false;
    [RelayCommand] private void ToggleOtherStorageSection() => ShowOtherStorageSection = !ShowOtherStorageSection;
    public string OtherStorageCaret => ShowOtherStorageSection ? "▾" : "▸";
    public string OtherStorageToggleTip => ShowOtherStorageSection ? "Minimise" : "Show the drives";
    /// <summary>The minimised line: every drive's name, count and size, so nothing is lost by folding it.</summary>
    public string OtherStorageSummary => string.Join("   ·   ", OtherStorage.Select(o => $"{o.Label}  {o.Summary}  {o.StateText}"));
    partial void OnShowOtherStorageSectionChanged(bool value)
    { OnPropertyChanged(nameof(OtherStorageCaret)); OnPropertyChanged(nameof(OtherStorageToggleTip)); }
    [RelayCommand] private void ToggleOtherExpanded(OtherStorageRow? row) { if (row is not null) row.IsExpanded = !row.IsExpanded; }

    [RelayCommand]
    private async Task UseAsSecondary(OtherStorageRow? row)
    {
        if (row is null || _session.Manifest is null) return;
        if (MoveTouchesRoot(row.RootId)) return;
        if (!CanAddSecondary) { RequestSwap(row, SecondaryLocation); return; }
        try
        {
            new Grog.Core.Volumes.VolumeService(_session.Manifest).ReattachRoot(row.RootId);
            await _session.Manifest.SaveAsync();
            await AfterSlotChangeAsync($"{row.Label} is now the Secondary storage. Its files were already counted; the queue can download to it now.");
        }
        catch (Exception ex) { ShowLocationError("Couldn't use that storage", ex); }
    }

    [RelayCommand]
    private async Task SwapWithPrimary(OtherStorageRow? row)
    {
        if (row is null || _session.Manifest is null) return;
        if (MoveTouchesRoot(row.RootId) || (PrimaryLocation is { } pl && MoveTouchesRoot(pl.RootId))) return;
        if (PrimaryLocation is { } slot) { RequestSwap(row, slot); return; }
        // No primary at all (09-15 review): "Use as Primary" is a plain reattach + role, no swap.
        try
        {
            var vol = new Grog.Core.Volumes.VolumeService(_session.Manifest);
            vol.ReattachRoot(row.RootId); vol.SetPrimary(row.RootId);
            await AdoptPrimaryPathAsync(row.Path);
            await _session.Manifest.SaveAsync();
            await AfterSlotChangeAsync($"{row.Label} is now the Primary storage.");
        }
        catch (Exception ex) { ShowLocationError("Couldn't use that storage", ex); }
    }

    [ObservableProperty] private bool _showSwapConfirm;
    private OtherStorageRow? _pendingSwapOther; private BackupLocationRow? _pendingSwapSlot;
    public string SwapTitle => _pendingSwapSlot is { } s ? $"Swap with the {(s.IsPrimary ? "Primary" : "Secondary")} storage?" : "Swap storage?";
    public string SwapText => _pendingSwapOther is { } o && _pendingSwapSlot is { } s
        ? $"{o.Label} ({o.Path}) takes the {(s.IsPrimary ? "Primary" : "Secondary")} slot. {s.Label} ({s.Path}) becomes Other storage: its {s.FileCount} file{(s.FileCount == 1 ? " stays" : "s stay")} recorded as backed up there, and nothing downloads to it until it takes a slot again.\n\nNo files move. The download queue is re-planned onto the new slots."
          + (s.IsPrimary ? " New downloads go to the new Primary first." : "")
        : "";
    private void RequestSwap(OtherStorageRow other, BackupLocationRow? slot)
    {
        if (slot is null) { ShowLocationError("That slot is empty; use it directly."); return; }
        _pendingSwapOther = other; _pendingSwapSlot = slot;
        OnPropertyChanged(nameof(SwapTitle)); OnPropertyChanged(nameof(SwapText));
        ShowSwapConfirm = true;
    }
    [RelayCommand] private void CancelSwap() { ShowSwapConfirm = false; _pendingSwapOther = null; _pendingSwapSlot = null; }
    [RelayCommand]
    private async Task ConfirmSwap()
    {
        ShowSwapConfirm = false;
        var other = _pendingSwapOther; var slot = _pendingSwapSlot; _pendingSwapOther = null; _pendingSwapSlot = null;
        if (other is null || slot is null || _session.Manifest is null) return;
        try
        {
            new Grog.Core.Volumes.VolumeService(_session.Manifest).SwapIntoSlot(other.RootId, slot.RootId);
            if (slot.IsPrimary)
            {
                var root = _session.Manifest.Current.Roots.FirstOrDefault(r => r.Id == other.RootId);
                if (root is not null && !string.IsNullOrEmpty(root.PathHint)) await AdoptPrimaryPathAsync(root.PathHint);
            }
            await _session.Manifest.SaveAsync();
            await AfterSlotChangeAsync($"Swapped: {other.Label} is now the {(slot.IsPrimary ? "Primary" : "Secondary")} storage; {slot.DriveName} is Other storage (files still counted).");
        }
        catch (Exception ex) { ShowLocationError("Couldn't swap the storage", ex); }
    }

    /// <summary>Every slot change ends the same way: reconcile (the layout scan binds the drive), rebuild the
    /// cards and grids, re-plan the queue onto the new slots, and say so.</summary>
    private async Task AfterSlotChangeAsync(string logLine)
    {
        await _root.ReconcileAndRefresh();
        _root.RefreshFromManifest();
        RefreshBackupLocations(); RebuildInventory();
        _root.Log(logLine, category: LogCategory.Move);
        await _root.Overview.ReplanQueueAsync("the new storage slots");
        // (09-19) A drive that just left its slot (Detach, Swap) is no longer bound by the layout. AFTER the re-plan (review 09-19):
        // that is what takes a live run's tasks off the shelved drive, so no transfer is writing the .part being carried, so the engine
        // would never find the in-progress downloads on it: carry them to where each file belongs now.
        // Not awaited: a multi-GB partial leaving a USB stick is minutes of copying, and the command that asked for the
        // slot change stays disabled while its task runs (the Detach button greyed out for 2 min 15 s, walk 09-30).
        if (_session.Manifest is { } st)
            _ = CarryOffShelvedAsync(st);
    }

    private async Task CarryOffShelvedAsync(Grog.Core.Manifest.JsonManifestStore st)
    {
        foreach (var shelved in st.Current.Roots.Where(r => r.State == Grog.Core.Models.RootState.Detached && !string.IsNullOrEmpty(r.PathHint)).ToList())
            try { await CarryPartialsOffAsync(shelved.Id, shelved.PathHint, null); }
            catch (Exception ex) { _root.Log($"In-progress downloads were not carried off {shelved.Label}: {ex.Message}", isError: false, category: LogCategory.Move); }
    }

    /// <summary>(QA 09-30 B5) A slot change on a root a move is reading from or writing to (running or paused) is
    /// refused with a toast: Forget reset the files of a drive the runner was still copying onto, and the runner
    /// then stamped them with a root that no longer existed.</summary>
    private bool MoveTouchesRoot(string? rootId)
    {
        if (string.IsNullOrEmpty(rootId) || _activeReorgJob is not { } job || !(IsReorgRunning || ReorgPaused)) return false;
        bool touches;
        lock (job.SyncRoot)
            touches = job.Moves.Any(mv => !mv.IsSettled && ((mv.FromRootId ?? _session.Manifest?.Current.PrimaryRootId) == rootId || (mv.ToRootId ?? mv.FromRootId ?? _session.Manifest?.Current.PrimaryRootId) == rootId))
                      || job.RepointRootId == rootId;
        if (touches) _root.ShowToast("A move is using this storage. Let it finish, or Stop it, before changing storage.", 1);
        return touches;
    }

    // ---- Rename (09-15): the user's name for a drive, from the card or an Other-storage row. Blank = folder name.
    [ObservableProperty] private bool _showRenameDialog;
    [ObservableProperty] private string _renameText = "";
    private string? _renameRootId; private string _renamePath = "";
    public string RenameBody => $"Shown before the path on this drive's card, in the rail, and in the log. Leave blank to use the folder name ({System.IO.Path.GetFileName(_renamePath.TrimEnd('\\', '/'))}).";
    private void RequestRename(string rootId, string path, string current)
    {
        _renameRootId = rootId; _renamePath = path; RenameText = current;
        OnPropertyChanged(nameof(RenameBody)); ShowRenameDialog = true;
    }
    [RelayCommand] private void RequestRenameLocation(BackupLocationRow? row) { if (row is not null) RequestRename(row.RootId, row.Path, row.Name); }
    [RelayCommand] private void RequestRenameOther(OtherStorageRow? row) { if (row is not null) RequestRename(row.RootId, row.Path, row.Label); }
    [RelayCommand] private void CancelRename() { ShowRenameDialog = false; _renameRootId = null; }
    [RelayCommand]
    private async Task ConfirmRename()
    {
        ShowRenameDialog = false;
        var id = _renameRootId; _renameRootId = null;
        if (id is null || _session.Manifest is null) return;
        try
        {
            var vol = new Grog.Core.Volumes.VolumeService(_session.Manifest);
            vol.RenameRoot(id, RenameText);
            await _session.Manifest.SaveAsync();
            _root.RefreshFromManifest();
            RefreshBackupLocations(); RebuildInventory();
            var now = _session.Manifest.Current.Roots.FirstOrDefault(r => r.Id == id)?.Label ?? "";
            _root.Log($"Storage named \"{now}\".", category: LogCategory.Move);
        }
        catch (Exception ex) { ShowLocationError("Couldn't rename the storage", ex); }
    }

    [ObservableProperty] private bool _showForgetDetachedConfirm;
    private OtherStorageRow? _pendingForget;
    public string ForgetDetachedText => _pendingForget is { } r
        ? $"Stop tracking {r.Label} ({r.Path})? Its {r.FileCount} file{(r.FileCount == 1 ? "" : "s")} ({Grog.Core.Format.ByteFormat.Size(r.Bytes)}) will count as not backed up until you back {(r.FileCount == 1 ? "it" : "them")} up again or re-add this storage. Nothing on the drive is touched."
          + PartialsStayNote(r.RootId)
        : "";
    /// <summary>(09-19) The confirm's last word when in-progress downloads are still recorded on the drive: a carry
    /// already ran when it left its slot, so what is left could not go.</summary>
    private string PartialsStayNote(string rootId)
    {
        if (_session.Manifest is not { } st) return "";
        var (n, bytes) = Grog.Core.Volumes.PartialCarry.RecordedOn(st.Current, rootId);
        return n == 0 ? "" : $"\n\n{n} in-progress download{(n == 1 ? " is" : "s are")} still on this drive ({Grog.Core.Format.ByteFormat.Size(bytes)}) and will restart from zero.";
    }
    [RelayCommand]
    private void RequestForgetDetached(OtherStorageRow? row)
    {
        if (row is not null && MoveTouchesRoot(row.RootId)) return;
        if (row is null) return;
        _pendingForget = row; OnPropertyChanged(nameof(ForgetDetachedText)); ShowForgetDetachedConfirm = true;
    }
    [RelayCommand] private void CancelForgetDetached() { ShowForgetDetachedConfirm = false; _pendingForget = null; }
    [RelayCommand]
    private async Task ConfirmForgetDetached()
    {
        ShowForgetDetachedConfirm = false;
        var row = _pendingForget; _pendingForget = null;
        if (row is null || _session.Manifest is null) return;
        try
        {
            new Grog.Core.Volumes.VolumeService(_session.Manifest).RemoveRoot(row.RootId, force: true);
            await _session.Manifest.SaveAsync();
            await AfterSlotChangeAsync($"Forgot {row.Label}: {row.FileCount} file{(row.FileCount == 1 ? "" : "s")} now count as not backed up. Back up to download them again.");
        }
        catch (Exception ex) { ShowLocationError("Couldn't forget the storage", ex); }
    }

    // ---- Mark a drive as LOST: same manifest effect as "remove folder, leave files on disk", framed for a
    //      dead drive. Offline-only, gated on typing LOST because it rewrites many file states at once. ----
    [ObservableProperty] private bool _showLostDeviceDialog;
    [ObservableProperty] private string _lostTypedConfirm = "";
    private BackupLocationRow? _pendingLostRow;

    public string LostDeviceTitle => _pendingLostRow is { } r ? $"Mark the {r.Label} storage as lost?" : "";
    public string LostSummary => _pendingLostRow is { } r
        ? (r.FileCount > 0
            ? $"Use this if the drive died or is gone for good. Grog forgets the {r.FileCount} file{(r.FileCount == 1 ? "" : "s")} it was tracking here ({Grog.Core.Format.ByteFormat.Size(r.GrogBytes)}) - they go back to “Not downloaded” in your library so you can back {(r.FileCount == 1 ? "it" : "them")} up again elsewhere. Nothing in any working storage is touched."
            : "Use this if the drive died or is gone for good. It holds no tracked files, so this just drops it from your storage.")
        : "";
    public bool LostConfirmed => string.Equals(LostTypedConfirm.Trim(), "LOST", StringComparison.Ordinal);
    partial void OnLostTypedConfirmChanged(string value) => OnPropertyChanged(nameof(LostConfirmed));

    [RelayCommand]
    private void RequestLostDevice(BackupLocationRow? row)
    {
        if (row is null) return;
        if (MoveTouchesRoot(row.RootId)) return;
        _pendingLostRow = row;
        LostTypedConfirm = "";
        foreach (var p in new[] { nameof(LostDeviceTitle), nameof(LostSummary), nameof(LostConfirmed) }) OnPropertyChanged(p);
        ShowLostDeviceDialog = true;
    }

    [RelayCommand] private void CancelLostDevice() { ShowLostDeviceDialog = false; _pendingLostRow = null; }

    [RelayCommand]
    private async Task ConfirmLostDevice()
    {
        if (!LostConfirmed) return;   // must type LOST
        var row = _pendingLostRow;
        ShowLostDeviceDialog = false;
        _pendingLostRow = null;
        if (row is null || _session.Manifest is null) return;

        // Losing the PRIMARY: promote another folder first (same as delete), so RemoveRoot can drop the old
        // one. If it's the only folder, clear the primary (app returns to the choose-folder state).
        if (row.IsPrimary)
        {
            var promote = BackupLocations.FirstOrDefault(d => d.RootId != row.RootId)?.RootId;
            if (promote is not null) await PromoteToPrimaryAsync(promote);
            else ClearPrimary();
        }

        await _root.RunBusy("Marking folder lost…", async () =>
        {
            try
            {
                // force:true drops the (offline) device and marks its files "not downloaded" -- the lost-drive
                // outcome: the files aren't coming back, so they re-enter the backup queue.
                new Grog.Core.Volumes.VolumeService(_session.Manifest).RemoveRoot(row.RootId, force: true);
                await _session.Manifest.SaveAsync();
                await _root.ReconcileAndRefresh();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    RefreshBackupLocations();
                    RebuildInventory();
                    _root.Log($"Marked {row.Label} as lost - its {row.FileCount} file{(row.FileCount == 1 ? " is" : "s are")} back in the backup queue.", category: LogCategory.Move);
                });
            }
            catch (Exception ex) { await Dispatcher.UIThread.InvokeAsync(() => ShowLocationError("Couldn't mark the folder lost", ex)); }
        });
    }

    [RelayCommand]
    private async Task ConfirmDeleteDevice()
    {
        if (!DeleteConfirmed) return;   // destructive path: must type DELETE
        var row = _pendingDeleteRow;
        int choice = DeleteHasFiles ? DeleteChoice : (DeleteCanDetach ? 3 : 1);   // empty: detach if it can be, else forget
        var other = OtherDeviceForDelete?.RootId;
        ShowDeleteDeviceDialog = false;
        _pendingDeleteRow = null;
        if (row is null || _session.Manifest is null) return;
        if (choice == 2 && !row.IsOnline)
        {
            ShowLocationError($"{row.DriveName} is not connected, so its files can't be erased. Plug it in, or choose Forget to leave them where they are.");
            return;
        }
        // (09-14) The choice is logged BEFORE it runs: the log is the evidence when the dialog and the outcome disagree.
        _root.Log($"{row.DriveName}: you chose {choice switch { 3 => "Detach", 1 => "Forget", 0 => "Move & Forget", 2 => "Delete", _ => choice.ToString() }}.", category: LogCategory.Move);

        // DETACH (09-13): the shelf. No promotion (never the primary), no file work, no slot.
        if (choice == 3)
        {
            if (row.IsPrimary) return;
            try
            {
                new Grog.Core.Volumes.VolumeService(_session.Manifest).DetachRoot(row.RootId);
                await _session.Manifest.SaveAsync();
                // (09-15 review) Through the one slot-change path: with a run live, the queue must be re-planned
                // or its tasks keep downloading onto the shelved drive.
                await AfterSlotChangeAsync($"Detached {row.DriveName}: its {row.FileCount} file{(row.FileCount == 1 ? " stays" : "s stay")} recorded as backed up there. It is listed under Other storage; use it in a slot again any time.");
            }
            catch (Exception ex) { ShowLocationError("Couldn't detach the storage", ex); }
            return;
        }

        // Deleting the PRIMARY: promote another folder first so RemoveRoot (which refuses the primary) can
        // drop the old one; if it is the ONLY folder, clear the primary (app returns to choose-folder).
        bool wasPrimary = row.IsPrimary;
        // The layout that resolves this drive's files is built BEFORE the primary can be cleared: built after,
        // on an empty primary, its ctor re-creates one at the old path (see ClearPrimary). Off the dispatcher:
        // the ctor probes every root's folder.
        var manifestForLayout = _session.Manifest; var rootForLayout = _session.BackupRoot;
        var layout = await Task.Run(() => new Grog.Core.Volumes.BackupLayout(manifestForLayout.Current, rootForLayout));
        if (wasPrimary)
        {
            var promote = (choice == 0 && !string.IsNullOrEmpty(other)) ? other
                        : BackupLocations.FirstOrDefault(d => d.RootId != row.RootId)?.RootId;
            if (promote is not null) await PromoteToPrimaryAsync(promote);
            else ClearPrimary();
        }

        // Choice 0 uses the SAME visible move engine as grid moves; the folder is removed only after every
        // file settles (reached-end hook), so a stopped move never removes a folder that still holds files.
        if (choice == 0 && !string.IsNullOrEmpty(other))
        {
            var files0 = _session.Manifest.Current.Items
                .SelectMany(i => i.Files.Concat(i.OldVersionFiles)   // carry the Old Versions/ archive too
                    .Where(f => f.RootId == row.RootId).Select(f => (i, f))).ToList();
            // (UI-thread sweep 09-06) BackupLayout probes every root's PathHint and Resolve probes the config
            // dir: both off the dispatcher; the planner itself is pure. The slot is taken inside RunReorgWithPaneAsync.
            var manifest0 = _session.Manifest; var root0 = _session.BackupRoot; var pending0 = _session.PendingWrites;
            var (job0, cfg0) = await Task.Run(() =>
            {
                var layout0 = new Grog.Core.Volumes.BackupLayout(manifest0.Current, root0);
                var room0 = MoveRoomFor(manifest0, pending0, layout0, other!);   // files that cannot fit are left in place
                var planned = Grog.Core.Volumes.ReorgPlanner.ForFileMoves(manifest0.Current, layout0,
                    files0.Select(x => (x.i, x.f)), other!, room0);
                return (planned, Grog.Core.Storage.GrogPaths.Resolve(root0).ConfigDir);
            });
            if (BlockIfDrivesUnavailable(job0))   // don't remove the folder if its files can't move
            {
                // (UI-thread sweep 09-06 r2) the promotion above already moved the app's primary (settings +
                // _backupRoot); persist the manifest side too, or a restart would disagree with settings.
                if (wasPrimary) { await _session.Manifest.SaveAsync(); RefreshBackupLocations(); }
                return;
            }
            LogLeftInPlace(job0, other);
            var otherLabel = DeleteOtherLabel; var driveName = row.DriveName;
            await RunReorgWithPaneAsync(job0, cfg0, onReachedEnd: async () =>
            {
                new Grog.Core.Volumes.VolumeService(_session.Manifest).RemoveRoot(row.RootId, force: true);
                await _session.Manifest.SaveAsync();
                // Said when it is TRUE, at the end of the move, with what actually moved (QA 09-30 B13).
                int moveCount; lock (job0.SyncRoot) moveCount = job0.Moves.Count(x => x.State == Grog.Core.Volumes.ReorgMoveState.Done);
                _root.Log($"Moved {moveCount} file{(moveCount == 1 ? "" : "s")} to {otherLabel} and deleted {driveName}.", category: LogCategory.Move);
            });
            return;
        }

        await _root.RunBusy("Removing folder…", async () =>
        {
            var vol = new Grog.Core.Volumes.VolumeService(_session.Manifest);
            string msg;
            try
            {
                if (choice == 2)
                {
                    // Physically delete this device's files, then remove it. Through LocalDeletion, THE delete
                    // (sweep 2 #7): the hand loop here left every .part, the emptied game folders and the
                    // wrong records behind. Plan + Apply under the gate, the disk work off it.
                    var store = _session.Manifest;
                    Grog.Core.Sync.LocalDeletion.DeletePlan plan;
                    using (store.Gate.Enter()) plan = Grog.Core.Sync.LocalDeletion.PlanForRoot(store.Current, layout, row.RootId);
                    int total = plan.Targets.Count;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        DeleteDeviceProgressTitle = $"DELETING {row.Label.ToUpperInvariant()}";
                        DeleteDeviceProgressText = $"0 of {total}"; DeleteDeviceProgressValue = 0;
                        ShowDeleteDeviceProgress = true;
                    });
                    var lastPaint = System.Diagnostics.Stopwatch.StartNew();
                    var failures = new System.Collections.Generic.List<string>(plan.Failures);
                    // Paint at most ~8x a second, and always on the last file: a slow USB drive is exactly when
                    // the user needs to see it moving.
                    var outcomes = Grog.Core.Sync.LocalDeletion.Execute(plan, null, failures, (done, of, freed) =>
                    {
                        if (lastPaint.ElapsedMilliseconds < 120 && done != of) return;
                        lastPaint.Restart();
                        string text = $"{done} of {of} · {Grog.Core.Format.ByteFormat.Size(freed)} freed" + (failures.Count > 0 ? $" · {failures.Count} failed" : "");
                        double v = of == 0 ? 100 : 100.0 * done / of;
                        Dispatcher.UIThread.Post(() => { DeleteDeviceProgressText = text; DeleteDeviceProgressValue = v; });
                    });
                    Grog.Core.Sync.LocalDeletion.Result res;
                    using (store.Gate.Enter()) res = Grog.Core.Sync.LocalDeletion.Apply(store.Current, plan, outcomes, failures);
                    int deleted = res.Deleted, failed = failures.Count; long bytes = res.Bytes;
                    foreach (var fail in failures) _root.Log($"Delete {row.DriveName}: {fail}", isError: true);
                    await Dispatcher.UIThread.InvokeAsync(() => DeleteDeviceProgressText = "Removing the folder from Grog");
                    vol.RemoveRoot(row.RootId, force: true);
                    msg = $"You deleted {row.DriveName}: {deleted} file{(deleted == 1 ? "" : "s")} ({Grog.Core.Format.ByteFormat.Size(bytes)}) removed from the folder"
                        + (failed > 0 ? $"; {failed} could not be deleted and {(failed == 1 ? "is" : "are")} still on the drive" : "") + ".";
                }
                else
                {
                    // Leave files on disk; just stop tracking the device (RemoveRoot marks them not-downloaded).
                    vol.RemoveRoot(row.RootId, force: true);
                    msg = $"Removed {row.DriveName} (files left on disk).";
                }
                await _session.Manifest.SaveAsync();
                await _root.ReconcileAndRefresh();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    RefreshBackupLocations();
                    RebuildInventory();
                    _root.Log(msg, category: LogCategory.Move);
                });
            }
            catch (Exception ex) { await Dispatcher.UIThread.InvokeAsync(() => ShowLocationError("Couldn't remove the folder", ex)); }
            finally { await Dispatcher.UIThread.InvokeAsync(() => ShowDeleteDeviceProgress = false); }
        });
    }

    // ---- Change folder: re-point a device to a new path, offering to move its files there ----
    private BackupLocationRow? _pendingChangeRow;
    private string? _pendingChangePath;
    [ObservableProperty] private bool _showChangeFolderPrompt;
    [ObservableProperty] private bool _showChangeOrphanWarn;
    public string ChangeFolderText { get; private set; } = "";
    public string ChangeOrphanText { get; private set; } = "";

    /// <summary>Open a backup folder in the OS file manager; best-effort, silently ignores failures.</summary>
    [RelayCommand]
    private void OpenFolder(BackupLocationRow? row) => Grog.App.Services.FileManager.Open(row?.Path);

    [RelayCommand]
    private async Task ChangeFolder(BackupLocationRow? row)
    {
        if (row is null || _session.Manifest is null) return;
        if (MoveTouchesRoot(row.RootId)) return;
        // (UI-thread sweep 09-06 r2) the seed probe (Directory.Exists on a possibly sleeping drive) runs off the
        // dispatcher; the picker only gets a seed that is known to exist.
        var seedPath = row.Path;
        var seed = await Task.Run(() => !string.IsNullOrEmpty(seedPath) && System.IO.Directory.Exists(seedPath) ? seedPath : null);
        var newPath = await _root.PickFolderAsync($"New folder for {row.Label}", seed);
        if (string.IsNullOrEmpty(newPath)) return;
        if (string.Equals(System.IO.Path.GetFullPath(newPath), System.IO.Path.GetFullPath(row.Path), StringComparison.OrdinalIgnoreCase)) return;

        var vol = new Grog.Core.Volumes.VolumeService(_session.Manifest);
        var nested = vol.FindNestingConflict(newPath);
        if (nested is not null)
        {
            ShowLocationError($"That folder is inside an existing backup folder:\n\n{nested.PathHint}\n\nPick a folder outside it.");
            return;
        }

        _pendingChangeRow = row;
        _pendingChangePath = newPath;
        // Source offline: a move cannot run, so the only prior answer was "flag them missing" even when the files
        // sit at the new path already. Sample the root's recorded files there (disk I/O, off the dispatcher).
        if (row.FileCount > 0 && !row.IsOnline)
        {
            var manifest = _session.Manifest; var rootId = row.RootId; var full = System.IO.Path.GetFullPath(newPath);
            var (sampled, found) = await Task.Run(() => SampleFilesUnder(manifest, rootId, full));
            if (!ReferenceEquals(_session.Manifest, manifest) || _pendingChangeRow != row) return;
            if (found > 0)
            {
                ChangeRepointText = $"\"{row.Label}\" is offline, but {(found == sampled ? "all" : $"{found} of")} {sampled} sampled file{(sampled == 1 ? "" : "s")} "
                    + $"{(sampled == 1 ? "is" : "are")} already at\n\n{full}\n\n"
                    + "Re-point keeps every file tracked at the new path. Flag as Missing re-points without checking; anything not there is flagged MISSING.";
                OnPropertyChanged(nameof(ChangeRepointText));
                ShowChangeRepointPrompt = true;
                return;
            }
        }
        if (row.FileCount > 0)
        {
            ChangeFolderText = $"\"{row.Label}\" holds {row.FileCount} file{(row.FileCount == 1 ? "" : "s")}. Move {(row.FileCount == 1 ? "it" : "them")} to the new folder?\n\n{newPath}\n\n"
                + "Same drive is a quick rename; a different drive copies each file and can take a while.";
            OnPropertyChanged(nameof(ChangeFolderText));
            ShowChangeFolderPrompt = true;
        }
        else
        {
            await ApplyChangeNoMoveAsync();   // empty location: just re-point
        }
    }

    [RelayCommand]
    private void CancelChangeFolder() { ShowChangeFolderPrompt = false; ShowChangeOrphanWarn = false; ShowChangeRepointPrompt = false; _pendingChangeRow = null; _pendingChangePath = null; }

    // ---- Offline source whose files already sit at the new path: re-point is the primary answer ----
    [ObservableProperty] private bool _showChangeRepointPrompt;
    public string ChangeRepointText { get; private set; } = "";

    /// <summary>Pool thread. Probes up to five of the root's recorded files under <paramref name="newRoot"/>;
    /// returns (sampled, found). Zero sampled when the root records no files.</summary>
    private static (int Sampled, int Found) SampleFilesUnder(Grog.Core.Manifest.JsonManifestStore? manifest, string rootId, string newRoot)
    {
        if (manifest is null) return (0, 0);
        // The sample is picked under the gate; the disk checks run outside it.
        var sample = manifest.Read(m => m.Items.SelectMany(i => i.Files)
            .Where(f => f.RootId == rootId && !string.IsNullOrEmpty(f.LocalRelativePath))
            .Select(f => f.LocalRelativePath!).Take(5).ToList());
        int found = 0;
        foreach (var rel in sample)
            try { if (System.IO.File.Exists(System.IO.Path.Combine(newRoot, rel))) found++; } catch { }
        return (sample.Count, found);
    }

    /// <summary>The files are already at the new path: rewrite the hint (Core's RepointRoot, via
    /// <see cref="RepointRootAsync"/>) and let reconcile clear any Missing flags.</summary>
    [RelayCommand]
    private async Task ConfirmChangeRepoint()
    {
        ShowChangeRepointPrompt = false;
        var row = _pendingChangeRow; var newPath = _pendingChangePath;
        _pendingChangeRow = null; _pendingChangePath = null;
        if (row is null || newPath is null || _session.Manifest is null) return;
        try
        {
            var full = System.IO.Path.GetFullPath(newPath);
            if (!await RepointRootAsync(row, full)) return;
            await _root.ReconcileAndRefresh();
            RefreshBackupLocations();
            _root.Log($"You re-pointed {row.Label} to {full}; its files were already there.", category: LogCategory.Move);
        }
        catch (Exception ex) { ShowLocationError("Couldn't change the folder", ex); }
    }

    /// <summary>"The move is running, here is where to watch it." Shown once when a move actually has
    /// work to do; the move itself continues regardless of whether this is dismissed or navigated from.</summary>
    [ObservableProperty] private bool _showMoveStartedNotice;

    [RelayCommand]
    private void DismissMoveStartedNotice() => ShowMoveStartedNotice = false;

    [RelayCommand]
    private void WatchMoveOnOverview()
    {
        ShowMoveStartedNotice = false;
        _root.Navigate("Overview");
    }

    /// <summary>Relocate the files via the reorg runner (journaled, resumable, pausable), then re-point the
    /// root: files keep RootId + relative path, and PathHint flips only when the plan reaches its end.</summary>
    [RelayCommand]
    private async Task ConfirmChangeMove()
    {
        ShowChangeFolderPrompt = false;
        var row = _pendingChangeRow; var newPath = _pendingChangePath;
        _pendingChangeRow = null; _pendingChangePath = null;
        if (row is null || newPath is null || _session.Manifest is null) return;

        var full = System.IO.Path.GetFullPath(newPath);

        // Block if either drive is offline/missing: the source (whose files we move) or the destination
        // drive. Don't create the target folder or start a partial repoint.
        // (UI-thread sweep 09-06) the layout's root probes, the destination-volume probe and the mkdir all run
        // off the dispatcher, in the original order (mkdir only when nothing is offline). Slot is taken later.
        var manifestCm = _session.Manifest; var rootCm = _session.BackupRoot;
        var offline = await Task.Run(() =>
        {
            var list = new System.Collections.Generic.List<string>();
            var layoutChk = new Grog.Core.Volumes.BackupLayout(manifestCm.Current, rootCm);
            if (row.FileCount > 0 && !layoutChk.IsOnline(row.RootId))
                list.Add(string.IsNullOrWhiteSpace(row.Label) ? "the current storage" : row.Label);
            var destVol = System.IO.Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(destVol) || !System.IO.Directory.Exists(destVol))
                list.Add("the destination folder");
            if (list.Count == 0) System.IO.Directory.CreateDirectory(full);
            return list;
        });
        if (offline.Count > 0) { BlockMoveForDrives(offline); return; }
        // (UI-thread sweep 09-06 r2) the probe took a while: the storage may have been removed or the primary swapped.
        if (!ReferenceEquals(_session.Manifest, manifestCm) || _session.BackupRoot != rootCm || !_session.Manifest.Current.Roots.Any(r => r.Id == row.RootId))
        { _root.Log("Change folder: storage changed while checking drives; move not started."); return; }

        var job = Grog.Core.Volumes.ReorgPlanner.ForRootPathChange(_session.Manifest.Current, row.RootId, row.Path, full);   // pure
        if (job.Total == 0) { if (await RepointRootAsync(row, full)) { RefreshBackupLocations(); _root.Log($"{row.Label} storage set to {full}"); } return; }

        var configDir = await Task.Run(() => Grog.Core.Storage.GrogPaths.Resolve(rootCm).ConfigDir);   // (UI-thread sweep 09-06)
        await RunReorgWithPaneAsync(job, configDir, onReachedEnd: async () =>
        {
            if (!await RepointRootAsync(row, full)) return;
            Dispatcher.UIThread.Post(() => { RefreshBackupLocations(); _root.Log($"Moved {row.Label} to {full}", category: LogCategory.Move); });
        });
    }

    /// <summary>Continue without moving: second (red) confirm, then re-point + mark files missing.</summary>
    [RelayCommand]
    private void RequestChangeOrphan()
    {
        ShowChangeFolderPrompt = false; ShowChangeRepointPrompt = false;
        var row = _pendingChangeRow;
        if (row is null) return;
        ChangeOrphanText = $"Leave {row.FileCount} file{(row.FileCount == 1 ? " where it is" : "s where they are")} and just re-point \"{row.Label}\"?\n\n"
            + "Grog will still show them as backed up but they won't be in the new folder, so it flags them MISSING. "
            + "You'd re-download or re-import to fix that. Your files are not deleted.";
        OnPropertyChanged(nameof(ChangeOrphanText));
        ShowChangeOrphanWarn = true;
    }

    [RelayCommand]
    private async Task ConfirmChangeOrphan()
    {
        ShowChangeOrphanWarn = false;
        var row = _pendingChangeRow; var newPath = _pendingChangePath;
        _pendingChangeRow = null; _pendingChangePath = null;
        if (row is null || newPath is null || _session.Manifest is null) return;
        try
        {
            // (UI-thread sweep 09-06 r2) ReplaceRoot's CreateDirectory touches the new drive: pre-create off the dispatcher.
            var manifest = _session.Manifest; var full = System.IO.Path.GetFullPath(newPath);
            await Task.Run(() => System.IO.Directory.CreateDirectory(full));
            if (!ReferenceEquals(_session.Manifest, manifest) || !_session.Manifest.Current.Roots.Any(r => r.Id == row.RootId))
            { _root.Log("Change folder: storage changed while preparing; not applied."); return; }
            new Grog.Core.Volumes.VolumeService(_session.Manifest).ReplaceRoot(row.RootId, newPath);
            if (row.IsPrimary) await AdoptPrimaryPathAsync(newPath);
            await _session.Manifest.SaveAsync();
            await _root.ReconcileAndRefresh();
            RefreshBackupLocations();
            _root.Log($"{row.Label} re-pointed to {newPath} (old files flagged missing)");
        }
        catch (Exception ex) { ShowLocationError("Couldn't change the folder", ex); }
    }

    /// <summary>Empty location: nothing to move, just update the path.</summary>
    private async Task ApplyChangeNoMoveAsync()
    {
        var row = _pendingChangeRow; var newPath = _pendingChangePath;
        _pendingChangeRow = null; _pendingChangePath = null;
        if (row is null || newPath is null || _session.Manifest is null) return;
        if (!await RepointRootAsync(row, newPath)) return;
        RefreshBackupLocations();
        _root.Log($"{row.Label} storage set to {newPath}");
    }

    /// <summary>Update the root's path in place through Core's one re-point (foreign-marker and nesting refusals,
    /// PortablePath/LastSeen, the marker write); files keep RootId + relative paths, so they stay tracked. For the
    /// primary, also adopt it as the app's backup root. False when Core refused; the refusal is already shown.</summary>
    internal async Task<bool> RepointRootAsync(BackupLocationRow row, string newPath)
    {
        var full = System.IO.Path.GetFullPath(newPath);
        var manifest = _session.Manifest!;
        try
        {
            await Task.Run(() =>
            {
                System.IO.Directory.CreateDirectory(full);
                new Grog.Core.Volumes.VolumeService(manifest).RepointRoot(row.RootId, full);
            });
        }
        catch (Exception ex) { ShowLocationError("Couldn't change the folder", ex); return false; }

        if (row.IsPrimary) await AdoptPrimaryPathAsync(full);
        await manifest.SaveAsync();
        return true;
    }

    /// <summary>THE way a root becomes primary. The manifest's PrimaryRootId and the app's own primary folder
    /// (settings ChosenRoot / _backupRoot) must move together: BackupLayout binds the primary to the app's
    /// path and rewrites the root's PathHint from it, so a promotion that left _backupRoot on the OLD folder
    /// silently re-pointed the new primary at the old drive (owner-hit 09-04: the promoted USB root took the
    /// removed F: path, F: could not be re-added, and new downloads would have landed on F:).</summary>
    internal async Task PromoteToPrimaryAsync(string rootId)
    {
        if (_session.Manifest is null) return;
        new Grog.Core.Volumes.VolumeService(_session.Manifest).SetPrimary(rootId);
        var root = _session.Manifest.Current.Roots.FirstOrDefault(r => r.Id == rootId);
        if (root is not null && !string.IsNullOrEmpty(root.PathHint)) await AdoptPrimaryPathAsync(root.PathHint);
    }

    /// <summary>The ONLY storage is going: clear the primary everywhere the app keeps it (manifest, shared root,
    /// settings) so the app returns to the choose-folder state. Before 09-18 only the manifest was cleared and
    /// the next BackupLayout built on the still-set root re-created a "Primary" at the same path from a worker
    /// thread (QA 09-18): the card never left, the files stayed counted, and a fresh import was queued.</summary>
    private void ClearPrimary()
    {
        if (_session.Manifest is null) return;
        _session.Manifest.Mutate(m => m.PrimaryRootId = null);   // (manifest gate 09-08)
        _session.BackupRoot = "";
        _session.Settings.ChosenRoot = null;
        _session.Settings.SaveSoon();
        _root.RaiseRootPathChanged();
    }

    /// <summary>Point the app at a new primary backup folder (updates the shared root + settings).</summary>
    internal Task AdoptPrimaryPathAsync(string full)
    {
        _session.BackupRoot = full;
        _session.Settings.ChosenRoot = Grog.Core.Storage.GrogPaths.StorePath(full);
        _session.Settings.SaveSoon();   // (UI-thread sweep 09-06 r2) debounced; flushed on exit/crash
        GameRowViewModel.ArtDir = Grog.Core.Storage.GrogPaths.ResolveNoProbe(_session.BackupRoot).ArtDir;   // (UI-thread sweep 09-06 r2) path only, no disk probe
        _root.RaiseRootPathChanged();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RemoveLocation(BackupLocationRow? row)
    {
        if (row is null || row.IsPrimary || _session.Manifest is null) return;
        try
        {
            new Grog.Core.Volumes.VolumeService(_session.Manifest).RemoveRoot(row.RootId);
            await _session.Manifest.SaveAsync();
            _root.RefreshFromManifest();
            _root.Log("Storage removed");
        }
        catch (Exception ex) { _root.Log($"Couldn't remove: {ex.Message}", isError: true); }
    }
}

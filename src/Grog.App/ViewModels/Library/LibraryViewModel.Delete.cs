// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Format;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;

namespace Grog.App.ViewModels;

/// <summary>
/// Delete downloaded files (owner, 09-11). ONE flow at three sizes -- a file row, a section of the detail
/// pane (game files / extras), the grid selection (whole games) -- so it reads as one action. Confirms once
/// with the real count and bytes; "Don't ask again" is an app setting, per machine. The work itself is
/// <see cref="LocalDeletion"/>; this file only decides WHICH files and asks.
/// </summary>
public sealed partial class LibraryViewModel
{
    [ObservableProperty] private bool _showDeleteConfirm;
    [ObservableProperty] private string _deleteConfirmText = "";
    [ObservableProperty] private bool _dontAskDeleteAgain;
    private List<GameFile>? _pendingDelete;

    [RelayCommand]
    private Task DeleteFile(DetailFileRow? row)
        => row is null ? Task.CompletedTask : BeginDelete(new[] { row.File }, row.Name);

    /// <summary>"games" or "extras": every file in that section of the open game.</summary>
    [RelayCommand]
    private Task DeleteSection(string? which)
    {
        var rows = which == "extras" ? DetailExtraFiles : DetailGameFiles;
        var title = DetailSource?.Title ?? "";
        return BeginDelete(rows.Select(r => r.File).ToList(), which == "extras" ? $"the extras of {title}" : $"the game files of {title}");
    }

    /// <summary>Storage page, file row: the one copy on that device.</summary>
    [RelayCommand]
    private Task DeleteInventoryFile(InventoryRow? row)
        => row is null ? Task.CompletedTask : BeginDelete(new[] { row.File }, row.FileName);

    /// <summary>Storage page, game rollup: this game's files ON THIS DEVICE only. A copy of the same game on the
    /// other drive is a separate row there and is not touched -- the user deletes what they right-clicked.</summary>
    [RelayCommand]
    private Task DeleteInventoryGame(InventoryGroupRow? group)
        => group is null ? Task.CompletedTask
         : BeginDelete(group.Files.Select(r => r.File).ToList(), $"{group.Title} on {(string.IsNullOrEmpty(group.Grid?.Label) ? "this device" : group.Grid!.Label)}");

    /// <summary>(09-20) Nothing downloaded, nothing to delete: the entry is dead on a game that holds no bytes
    /// (records only, no disk). Re-asked whenever the menu counts are (selection and download-state changes).</summary>
    private bool CanDeleteSelected()
        => SelectedRows.Select(r => ItemById(r.GogId)).Any(i => i is not null && i.Files.Concat(i.OldVersionFiles).Any(Grog.Core.Sync.BackupScope.HoldsBytes));

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private Task DeleteSelected()
    {
        var items = SelectedRows.Select(r => ItemById(r.GogId)).Where(i => i is not null).Select(i => i!).ToList();
        if (items.Count == 0) return Task.CompletedTask;
        var what = items.Count == 1 ? items[0].Title : $"{items.Count} games";
        // Withdrawn slots live in the archive list; their bytes are the user's to delete too.
        return BeginDelete(items.SelectMany(i => i.Files.Concat(i.OldVersionFiles.Where(Grog.Core.Sync.BackupScope.IsWithdrawnOnDisk))).ToList(), what);
    }

    private Task BeginDelete(IReadOnlyList<GameFile> files, string what)
    {
        if (_manifest is null) return Task.CompletedTask;
        var (n, bytes, queued) = LocalDeletion.Preview(_manifest.Current, files);
        if (n == 0)
        {
            Log(queued > 0 ? $"Nothing to delete for {what}: the files are queued or downloading." : $"Nothing to delete for {what}: no downloaded copy.");
            return Task.CompletedTask;
        }
        if (_settings.SuppressDeleteConfirm) return DeleteNow(files.ToList());

        var text = $"Delete {n} downloaded file{(n == 1 ? "" : "s")} ({ByteFormat.Size(bytes)}) for {what} from your backup drive? "
                 + "Grog will mark them not backed up; they can be downloaded again any time.";
        if (queued > 0) text += $" {queued} file{(queued == 1 ? " is" : "s are")} queued or downloading and will be left alone.";
        DeleteConfirmText = text;
        _pendingDelete = files.ToList();
        DontAskDeleteAgain = false;
        ShowDeleteConfirm = true;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void CancelDelete() { ShowDeleteConfirm = false; _pendingDelete = null; DontAskDeleteAgain = false; }

    [RelayCommand]
    private async Task ConfirmDelete()
    {
        ShowDeleteConfirm = false;
        if (DontAskDeleteAgain) { _settings.SuppressDeleteConfirm = true; _settings.SaveSoon(); }
        DontAskDeleteAgain = false;
        var files = _pendingDelete; _pendingDelete = null;
        if (files is not null) await DeleteNow(files);
    }

    private async Task DeleteNow(List<GameFile> files)
    {
        var manifest = _manifest; var root = _backupRoot;
        if (manifest is null || string.IsNullOrEmpty(root)) return;
        // Three steps (09-13): records read and paths resolved under the gate, the DISK WORK OFF it (a whole-game
        // delete on a slow drive held the gate for seconds and froze every UI read), records written under it.
        var result = await Task.Run(() =>
        {
            Grog.Core.Volumes.BackupLayout layout;
            try { layout = new Grog.Core.Volumes.BackupLayout(manifest.Current, root); }
            catch (Exception ex) { return new LocalDeletion.Result(0, 0, 0, 0, new List<string> { ex.Message }); }
            var plan = manifest.Read(m => LocalDeletion.Plan(m, layout, files));
            var failures = new List<string>(plan.Failures);
            var outcomes = LocalDeletion.Execute(plan, failures: failures);          // no gate held
            LocalDeletion.Result r = LocalDeletion.Result.Empty;
            manifest.Mutate(m => r = LocalDeletion.Apply(m, plan, outcomes, failures));
            return r;
        });
        try { await manifest.SaveAsync(); } catch { /* the next save persists it */ }
        await _root.ReconcileAndRefresh();
        _root.Storage.RefreshBackupLocations();
        _root.Storage.RebuildInventory();   // the Storage grids list bytes on disk: the deleted rows leave now (owner-hit 09-17)

        if (result.Deleted > 0)
            Log($"You deleted {result.Deleted} downloaded file{(result.Deleted == 1 ? "" : "s")} ({ByteFormat.Size(result.Bytes)}) from your backup drive"
                + (result.Missing > 0 ? $"; {result.Missing} had already gone" : "")
                + (result.Deleted == 1 ? ". It now counts" : ". They now count") + " as not backed up, as if never downloaded.");
        if (result.Skipped > 0)
            Log($"{result.Skipped} file{(result.Skipped == 1 ? " is" : "s are")} queued or downloading and {(result.Skipped == 1 ? "was" : "were")} left alone.");
        foreach (var f in result.Failures) Log($"Could not delete {f} (your delete request)", isError: true);
    }
}

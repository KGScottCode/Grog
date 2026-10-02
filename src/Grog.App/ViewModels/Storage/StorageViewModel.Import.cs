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

// Import: the folder-first import planner's UI (results screen, Find-match picker, adopt) and the deferred imports.
public sealed partial class StorageViewModel
{

    // --- import existing backups (folder-first planner; shared with the CLI 'import' verb) ---
    private Grog.Core.Verify.ImportPlan? _lastImportPlan;
    [ObservableProperty] private string _importSummary = "";
    [ObservableProperty] private bool _importCanAdopt;
    /// <summary>(09-25, owner) The Add dialog's "Verify file integrity with GOG's checksums". On by default (it was
    /// the behavior before the box existed); this session only.</summary>
    [ObservableProperty] private bool _importVerifyChecksums = true;
    // adopt behavior is chosen BEFORE scanning (mirrors the CLI --mode flag)
    [ObservableProperty] private Grog.Core.Verify.ImportMode _importMode = Grog.Core.Verify.ImportMode.Preview;
    public bool ImportModeIsPreview => ImportMode == Grog.Core.Verify.ImportMode.Preview;
    public bool ImportModeIsAdopt => ImportMode == Grog.Core.Verify.ImportMode.Adopt;
    public bool ImportModeIsAdoptOrphans => ImportMode == Grog.Core.Verify.ImportMode.AdoptAndReportOrphans;
    partial void OnImportModeChanged(Grog.Core.Verify.ImportMode value)
    {
        OnPropertyChanged(nameof(ImportModeIsPreview));
        OnPropertyChanged(nameof(ImportModeIsAdopt));
        OnPropertyChanged(nameof(ImportModeIsAdoptOrphans));
    }

    private static string FolderTail(string p, int n = 3)
    {
        var names = System.IO.Path.GetFileName(p);
        return string.IsNullOrEmpty(names) ? p : names;
    }

    private string BuildImportSummary(Grog.Core.Verify.ImportPlan plan, int? adopted)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"Found {plan.FoldersScanned} game folder(s): {plan.Matched.Count} matched ")
          .Append($"({plan.MatchedFileCount} file(s)), {plan.Ambiguous.Count} ambiguous, ")
          .Append($"{plan.UnmatchedGameFolders.Count} unmatched, {plan.OrphanFolders.Count} orphan.");
        if (adopted is { } a) sb.Append($"  Adopted {a} file(s).");
        if (plan.Ambiguous.Count > 0)
            sb.Append("\nAmbiguous (need a manual pick): ")
              .Append(string.Join(", ", plan.Ambiguous.Take(5).Select(x => FolderTail(x.Path))))
              .Append(plan.Ambiguous.Count > 5 ? ", …" : "");
        if (plan.UnmatchedGameFolders.Count > 0)
            sb.Append("\nUnmatched game folders: ")
              .Append(string.Join(", ", plan.UnmatchedGameFolders.Take(5).Select(FolderTail)))
              .Append(plan.UnmatchedGameFolders.Count > 5 ? ", …" : "");
        return sb.ToString();
    }


    [RelayCommand]
    private async Task ImportAdopt()
    {
        if (_session.Manifest is null || _lastImportPlan is null) return;
        try
        {
            var planner = new Grog.Core.Verify.ImportPlanner(_session.Manifest, _session.BackupRoot);
            await planner.ApplyAsync(_lastImportPlan, Grog.Core.Verify.ImportMode.Adopt);
            await _root.ReconcileAndRefresh();
            ImportSummary = $"Adopted {_lastImportPlan.AdoptedFiles} file(s). Your library is updated.";
            ImportCanAdopt = false;
            ShowImportResults = false;
        }
        catch (Exception ex)
        {
            ImportSummary = $"Adopt failed: {ex.Message}";
            _root.Log($"Adopt failed: {ex}", isError: true, category: LogCategory.Verify);   // the log gets the site and path
        }
    }

    // ---- results screen: problems (ambiguous/unmatched) with per-item Find-match; matched collapsed ----
    [ObservableProperty] private bool _showImportResults;
    [ObservableProperty] private bool _importMatchedExpanded;
    public ObservableCollection<ImportResultRow> ImportProblemRows { get; } = new();
    public ObservableCollection<string> ImportMatchedTitles { get; } = new();
    public int ImportMatchedCount => ImportMatchedTitles.Count;
    public bool ImportHasResults => ImportProblemRows.Count > 0 || ImportMatchedTitles.Count > 0;

    private void BuildImportResultRows(Grog.Core.Verify.ImportPlan plan)
    {
        ImportProblemRows.Clear();
        ImportMatchedTitles.Clear();
        foreach (var a in plan.Ambiguous)
            ImportProblemRows.Add(new ImportResultRow { Path = a.Path, Kind = "Ambiguous",
                Detail = string.Join("  /  ", a.Candidates.Select(c => c.Title)) });
        foreach (var u in plan.UnmatchedGameFolders)
            ImportProblemRows.Add(new ImportResultRow { Path = u, Kind = "Unmatched", Detail = "no library match" });
        foreach (var m in plan.Matched) ImportMatchedTitles.Add(m.Title);
        OnPropertyChanged(nameof(ImportMatchedCount));
        OnPropertyChanged(nameof(ImportHasResults));
    }

    [RelayCommand] private void OpenImportResults() { if (ImportHasResults) ShowImportResults = true; }
    [RelayCommand] private void CloseImportResults() => ShowImportResults = false;
    [RelayCommand] private void ToggleMatchedExpanded() => ImportMatchedExpanded = !ImportMatchedExpanded;

    // ---- Find-match picker (searchable game list) ----
    [ObservableProperty] private bool _showImportPicker;
    [ObservableProperty] private string _importPickerSearch = "";
    public ObservableCollection<GamePick> ImportPickerGames { get; } = new();
    private ImportResultRow? _importPickerTarget;
    partial void OnImportPickerSearchChanged(string value) => RefreshImportPickerGames();

    [RelayCommand]
    private void FindMatch(ImportResultRow? row)
    {
        if (row is null) return;
        _importPickerTarget = row;
        ImportPickerSearch = "";
        RefreshImportPickerGames();
        ShowImportPicker = true;
    }

    private void RefreshImportPickerGames()
    {
        ImportPickerGames.Clear();
        if (_session.Manifest is null) return;
        var q = ImportPickerSearch?.Trim() ?? "";
        foreach (var it in _session.Manifest.Current.Items
                     .Where(i => q.Length == 0 || i.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(i => i.Title).Take(200))
            ImportPickerGames.Add(new GamePick(it.GogId, it.Title));
    }

    [RelayCommand]
    private async Task ImportPickerSelect(long gogId)
    {
        if (_importPickerTarget is null || _lastImportPlan is null || _session.Manifest is null) { ShowImportPicker = false; return; }
        // (UI-thread sweep 09-06 r2) MatchFolderToItem walks the folder on the backup drive (EnumerateFiles,
        // AllDirectories): off the dispatcher. Target/plan/manifest are captured first and re-checked after.
        var target = _importPickerTarget; var plan = _lastImportPlan; var manifest = _session.Manifest; var root = _session.BackupRoot;
        var targetPath = target.Path;
        Grog.Core.Verify.MatchedFolder? matched;
        try
        {
            matched = await Task.Run(() => new Grog.Core.Verify.ImportPlanner(manifest, root).MatchFolderToItem(targetPath, gogId));
        }
        catch (Exception ex) { _root.Log($"Find match failed: {ex.Message}", isError: true); ShowImportPicker = false; return; }
        if (!ReferenceEquals(_importPickerTarget, target) || !ReferenceEquals(_lastImportPlan, plan) || !ReferenceEquals(_session.Manifest, manifest))
        {
            _root.Log("Find match: the picker target changed while matching; result dropped.");
            ShowImportPicker = false;
            return;
        }
        if (matched is not null)
        {
            plan.Matched.Add(matched);
            plan.Ambiguous.RemoveAll(a => a.Path == target.Path);
            plan.UnmatchedGameFolders.RemoveAll(p => p == target.Path);
            target.ResolvedTitle = matched.Title;
            ImportMatchedTitles.Add(matched.Title);
            OnPropertyChanged(nameof(ImportMatchedCount));
            ImportCanAdopt = true;
        }
        else
        {
            target.ResolvedTitle = "";
            ImportSummary = $"No files in that folder matched {matched?.Title ?? "the chosen game"} by size.";
        }
        ShowImportPicker = false;
    }

    [RelayCommand] private void CloseImportPicker() => ShowImportPicker = false;

    /// <summary>The App's side of an import that ran: the results rows and summary from the plan, the grid
    /// refresh after adoption, and the results screen when something needs a human (unattended, the 03:00
    /// run, it waits on the Storage page instead of sitting as a modal until morning). (S2.2)</summary>
    private async Task ShowImportOutcome(Grog.Core.Runs.ImportRunResult result, bool interactive)
    {
        if (result.Status == Grog.Core.Runs.ImportRunStatus.Failed) throw result.Error!;
        if (result.Plan is not { } plan) return;
        _lastImportPlan = plan;
        BuildImportResultRows(plan);
        await _root.ReconcileAndRefresh();   // after adoption: the new root's card shows its files, the grid gets its root id
        ImportCanAdopt = false;
        ImportSummary = BuildImportSummary(plan, plan.AdoptedFiles);
        if (ImportProblemRows.Count > 0)
        {
            if (interactive) ShowImportResults = true;
            else _root.Log($"{ImportProblemRows.Count} file(s) in {result.Root.Label} need a Find match - open Import results on the Storage page.");
        }
    }

    /// <summary>Run the imports that were deferred at add time, now that a scan has stored library items.
    /// Core clears and saves each root as it runs (a failure is logged, not retried forever). (S2.2)</summary>
    internal async Task RunPendingImportsAsync(bool interactive)
    {
        if (_session.Manifest is null) return;
        var manifest = _session.Manifest;
        if (!manifest.Read(m => Grog.Core.Verify.PendingImports.Runnable(m).Count > 0)) return;
        // A sleeping USB or network root can take seconds to answer: the whole run is a worker call.
        var results = await Task.Run(() => Grog.Core.Runs.ImportRun.RunPendingAsync(manifest,
            new MainWindowViewModel.AppRunHost(_root, LogCategory.General), _session.Cts?.Token ?? default,
            new Grog.Core.Runs.ImportRun.Options { VerifyAdopted = false }));   // (09-19) verified below, after the refresh
        if (!ReferenceEquals(_session.Manifest, manifest)) return;
        foreach (var r in results)
        {
            if (r.Status is Grog.Core.Runs.ImportRunStatus.Unreachable or Grog.Core.Runs.ImportRunStatus.Failed) continue;   // Core logged it
            await ShowImportOutcome(r, interactive);
        }
        foreach (var r in results) await _root.VerifyAdoptedAsync(r, ImportVerifyChecksums);   // (09-19) after the outcome, one visible pass per drive
    }
}

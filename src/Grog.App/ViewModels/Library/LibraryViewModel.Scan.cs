// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Download;
using Grog.Core.Format;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.App.Runtime;

namespace Grog.App.ViewModels;

// Library scan (S3.2): the catalog operations bar and its preview rows, the per-account record of the last scan,
// the scan summary modal (composition, new games, scope cost block) and Back Up New.
public sealed partial class LibraryViewModel
{
    /// <summary>The guide's step or active flag moved (S3.2): the library-owned readouts that depend on it.</summary>
    internal void RaiseGuideDerived()
    {
        OnPropertyChanged(nameof(SyncBackupLabel));
        OnPropertyChanged(nameof(ShowScanPanel)); OnPropertyChanged(nameof(ShowGuideScanPanel));
    }
}

public sealed partial class LibraryViewModel
{
    // ---- Catalog operations (library fetch, update check, cloud probe): mini bar under the CTA buttons ----
    /// <summary>True while a lightweight catalog/metadata operation runs (not a content download).</summary>
    [ObservableProperty] private bool _catalogBusy;
    [ObservableProperty] private double _catalogProgress;
    [ObservableProperty] private bool _catalogIndeterminate = true;
    // On-object scan progress text; lives on the scan screen, never the status bar.
    [ObservableProperty] private string _catalogDetail = "";
    // Scan panel eyebrow; a multi-account run names the account per pass to explain the changing totals.
    [ObservableProperty] private string _scanPanelTitle = "SCANNING YOUR GOG LIBRARY";
    // Content-download aggregate shown next to the scope label under the library bar (amber, S5).

    // CatalogBusy is normally toggled via Begin/EndCatalog (which raise the bar), but keep the signal robust
    // so the Scanning state refreshes no matter who flips it.
    partial void OnCatalogBusyChanged(bool value) => _root.RaiseBarState();

    internal void BeginCatalog(string statusText)
    {
        CatalogIndeterminate = true;
        CatalogProgress = 0;
        CatalogBusy = true;
        ScanPanelTitle = "SCANNING YOUR GOG LIBRARY";
        CatalogDetail = statusText;
        OnPropertyChanged(nameof(ShowScanPanel));
        RaiseScanPanel();
        _root.RaiseBarState();
    }
    /// <summary>The scan panel's readouts as ONE set (scan start + per-item progress both raise it).</summary>
    private void RaiseScanPanel()
    {
        OnPropertyChanged(nameof(CatalogCountText)); OnPropertyChanged(nameof(CatalogPercentText));
        OnPropertyChanged(nameof(ShowScanPanel)); OnPropertyChanged(nameof(ShowGuideScanPanel));
    }

    // Raw scan counts for the footer's "X / Y Products" readout; CatalogProgress is the derived percent.
    private int _catalogDone, _catalogTotal;
    internal void UpdateCatalog(int done, int total, string statusText)
    {
        _catalogDone = done; _catalogTotal = total;
        CatalogIndeterminate = total <= 0;
        CatalogProgress = total <= 0 ? 0 : 100.0 * done / total;
        CatalogDetail = statusText;
        RaiseScanPanel();
        // CatalogScanned is NOT set here: a reported total is not evidence anything was stored yet.
        _root.RaiseBarState();
        // Status bar carries only pre-count connection narration; once counts exist progress lives on the object.
        Log(total <= 0 ? statusText : "");
    }
    /// <summary>Preview rows, batched on a timer -- per-item inserts into a bound collection stutter the grid.
    /// Nothing persists; the manifest writes once at the end, so a canceled scan drops these rows.</summary>
    private readonly List<Grog.Core.Models.LibraryItem> _previewPending = new();
    private bool _previewActive;

    /// <summary>Shown only when the FIRST scan is canceled; a canceled re-scan keeps the catalog.</summary>
    [ObservableProperty] private bool _showFirstScanCanceled;

    [RelayCommand]
    private void DismissFirstScanCanceled() => ShowFirstScanCanceled = false;

    [RelayCommand]
    private void RescanAfterCancel()
    {
        ShowFirstScanCanceled = false;
        if (_root.PrimaryActionCommand.CanExecute(null)) _root.PrimaryActionCommand.Execute(null);
    }

    internal void BeginPreview()
    {
        _previewActive = true;
        lock (_previewPending) _previewPending.Clear();
    }

    internal void OnDiscovered(Grog.Core.Models.LibraryItem item)
    {
        if (!_previewActive) return;
        lock (_previewPending) _previewPending.Add(item);
    }

    /// <summary>Flush the batch into the grid, each row landing in TITLE order so nothing jumps around.</summary>
    internal void FlushPreview()
    {
        if (!_previewActive) return;
        List<Grog.Core.Models.LibraryItem> batch;
        lock (_previewPending)
        {
            if (_previewPending.Count == 0) return;
            batch = new List<Grog.Core.Models.LibraryItem>(_previewPending);
            _previewPending.Clear();
        }
        var registeredIds = _manifest?.Current.Accounts.Select(a => a.Id).ToList();
        var connectedIds = _root.Accounts.ConnectedAccountIds();   // hoisted: token-readability probes, once per flush
        foreach (var item in batch)
        {
            if (item.Type is ProductType.Pack or ProductType.Dlc) continue;   // not backable rows
            if (_gameById.ContainsKey(item.GogId)) continue;
            var row = GameRowViewModel.FromItem(item, _includeExtras, _includeGames,
                                                _manifest?.Current.PrimaryRootId ?? "", EffectiveScope,
                                                registeredIds, connectedIds);
            int i = _allGames.Count;
            while (i > 0 && string.Compare(_allGames[i - 1].Title, row.Title, StringComparison.OrdinalIgnoreCase) > 0) i--;
            _allGames.Insert(i, row);
            _gameById[row.GogId] = row;
        }
        ApplyFilter();
    }

    internal void EndPreview()
    {
        _previewActive = false;
        lock (_previewPending) _previewPending.Clear();
    }

    internal void EndCatalog(bool completed = true)
    {
        CatalogBusy = false;
        if (completed) CatalogScanned = true;
        _root.RaiseBarState();
        // Raise BOTH panel properties so a scan run during the guide clears its overlay card too.
        OnPropertyChanged(nameof(ShowScanPanel));
        OnPropertyChanged(nameof(ShowGuideScanPanel));
    }

    /// <summary>Raw counts ("342 / 1,206"); empty until the total is known, so the panel spins.</summary>
    public string CatalogCountText => _catalogTotal > 0 ? $"{_catalogDone:N0} / {_catalogTotal:N0}" : "";
    public string CatalogPercentText => _catalogTotal > 0 ? $"{CatalogProgress:F0}%" : "";
    /// <summary>The scan panel renders in exactly one of two hosts: the page normally, the guide overlay
    /// while the guide runs (page content sits behind the scrim).</summary>
    public bool ShowScanPanel => CatalogBusy && !_root.Guide.GuideActive;
    public bool ShowGuideScanPanel => CatalogBusy && _root.Guide.GuideActive && _root.Guide.GuideHasTarget;

    /// <summary>Assistant-only: drive the scan panel without a GOG connection for headless rendering.</summary>
    internal void SeedScanProgress(int done, int total, string detail)
    {
        BeginCatalog(detail);
        UpdateCatalog(done, total, detail);
    }

    // ---- Multi-account scan: full-library scans go through the runner ----

    /// <summary>Per-account outcome of the last full scan (null = single-account run or none yet);
    /// feeds the scan summary's BY ACCOUNT line.</summary>
    internal volatile System.Collections.Generic.IReadOnlyList<Grog.Core.Sync.AccountSyncPass>? _lastScanPasses;   // written on the scan thread, read on the UI thread

    /// <summary>False when the full scan just run stored NOTHING (every account pass failed or was
    /// skipped). Gates EndCatalog's completed flag so CatalogScanned stays honest: a failed scan on an
    /// empty library must leave the bar at "Scan GOG Library", never "Library Backed Up" (owner-hit
    /// 09-01, GOG 500 after a fresh-library reset). Single-account runs (null passes) throw on failure
    /// and never reach EndCatalog, so null reads true.</summary>
    internal bool LastScanStoredAnything => _lastScanPasses is null || _lastScanPasses.Any(p => p.Result is not null);

    public bool ShowScanAccountsLine => _lastScanPasses is { Count: > 1 };

    // ---- What changed since the last scan (owner 09-06) ----
    // The first scan makes everything "new", so the line and the button appear only on a rescan of a
    // library that already had items; the ids come from the scan itself, not from a diff of the grid.
    private int _itemsBeforeScan;
    private bool _lastScanRan;
    /// <summary>Called on the UI thread before a scan's RunBusy: the worker may already be appending to Items
    /// by the time a posted BeginCatalog runs, so the "before" count is taken here, not there.</summary>
    internal void NoteScanStarting() { _itemsBeforeScan = _manifest?.Current.Items.Count ?? 0; }
    private System.Collections.Generic.List<long> _lastNewGameIds = new();
    private int _lastNewFiles;
    public bool ShowScanNewGames => _itemsBeforeScan > 0 && _lastNewGameIds.Count > 0;
    public string ScanNewGamesLine
    {
        get
        {
            int g = _lastNewGameIds.Count;
            return $"{g} new {(g == 1 ? "game" : "games")} ({_lastNewFiles:N0} {(_lastNewFiles == 1 ? "file" : "files")}) since your last scan.";
        }
    }
    /// <summary>The summary said "no games": a successful scan that returned an empty account. Said
    /// plainly so an empty page after a scan never looks like a scan that did not run.</summary>
    public bool ShowScanEmptyLibrary => _lastScanRan && LastScanStoredAnything && (_manifest?.Current.Items.Count ?? 0) == 0;

    /// <summary>Back up only what this scan added; everything already in the library stays as it is.</summary>
    [RelayCommand]
    private async Task BackUpNew()
    {
        if (_lastNewGameIds.Count == 0) { ShowCompositionModal = false; return; }
        // A scheduled run may have started while the summary sat open: say so, keep the summary up.
        if (_root.Busy) { ShowToast("Try again once the current task finishes.", 0); return; }
        ShowCompositionModal = false;
        // The whole of what is new, not what the Library's chips happen to show: "3 new games" must not end
        // in "nothing to download" because extras are filtered out of view.
        await _root.DownloadFiltered(updatesOnly: false, onlyGameIds: _lastNewGameIds.ToHashSet(), ignoreContentChips: true);
    }
    public string ScanAccountsLine
    {
        get
        {
            if (_lastScanPasses is null) return "";
            // Names padded to one width and rendered in MonoFont, so the colons sit in a column.
            int w = _lastScanPasses.Max(p => p.Name.Length);
            return string.Join("\n", _lastScanPasses.Select(p =>
                p.Skipped ? $"{p.Name.PadRight(w)} : skipped ({p.SkipReason})"
                : p.Result is { } r ? $"{p.Name.PadRight(w)} : {r.GamesSeen} products"
                : $"{p.Name.PadRight(w)} : failed"));
        }
    }

    /// <summary>Write the scan report and open it. A FILE, not a panel: a diagnostic meant to be pasted
    /// into a bug report, readable at 1,500 products as well as 158.</summary>
    [RelayCommand]
    private async Task ViewScanReport()
    {
        var m = _manifest;
        if (m is null) return;
        // The report write (and the shell open) run off the dispatcher (UI-thread sweep 09-06 r2).
        var seen = _lastProductsSeen; var failed = _lastScanFailedIds;
        string? path;
        try
        {
            path = await Task.Run(() =>
            {
                var dir = Grog.Core.Storage.GrogPaths.ResolveConfigDir();
                return Grog.Core.Sync.ScanReport.Write(m.Current, seen, dir, failed);
            });
        }
        catch { path = null; }
        if (path is null) { Log("Couldn't write the scan report.", isError: true); return; }
        var opened = path;
        await Task.Run(() => Grog.App.Services.FileManager.Open(opened));
    }
    /// <summary>The guide holds its step while this is up, so open/close must re-evaluate the tour.</summary>
    partial void OnShowCompositionModalChanged(bool value) => _root.Guide.Refresh();

    [RelayCommand] private void DismissComposition()
    {
        // No one-time gate: a summary of a scan you asked for shows every time; automatic runs never raise it.
        ShowCompositionModal = false;
    }

    // --- scan summary ---
    [ObservableProperty] private bool _showCompositionModal;

    private int _lastScanErrors;
    private System.Collections.Generic.List<long> _lastScanFailedIds = new();

    /// <summary>The three rows MUST add up to what GOG reported; unreconciled numbers read as loss.</summary>
    public int ScanSeenCount => _lastProductsSeen;
    public int ScanNoFilesCount => _manifest?.Current.Excluded.Count ?? 0;
    public int ScanErrorCount => _lastScanErrors;
    public bool ScanHadErrors => _lastScanErrors > 0;

    // ---- Scope cost block: what the current selection costs against EVERYTHING, shown while fresh ----
    // The informed opt-out: languages default to the system pick, and this is where the user learns it.
    /// <summary>"1,009 files, 750.4 GB" for the whole catalog, no narrowing at all.</summary>
    public string ScanEverythingLine =>
        _manifest is null ? "" : ScopeCostLine(Grog.Core.Sync.Scope.Both);
    /// <summary>Same figure under the CURRENT scope.</summary>
    public string ScanSelectionLine =>
        _manifest is null ? "" : ScopeCostLine(EffectiveScope);
    /// <summary>Names the current selection: "Selected: All Platforms, English Only".</summary>
    public string ScanSelectionLabel
    {
        get
        {
            // Proper-noun platforms: the scope stores lowercase tokens ("windows"), the label wears names.
            static string Cap(string p) => PlatformNames.Label(p) is var l && l != p ? l
                : p.Length > 0 ? char.ToUpperInvariant(p[0]) + p[1..] : p;
            string plat = _platforms.Count == 0 ? "All Platforms" : string.Join("/", _platforms.Select(Cap));
            string lang = _languages.Count == 0 ? "All Languages" : string.Join("/", _languages) + " Only";
            return $"Selected: {plat}, {lang}";
        }
    }
    /// <summary>The subtraction's result, shown as its own row under a rule: Everything minus Selected.</summary>
    public string ScanSavingsLine
    {
        get
        {
            if (_manifest is null) return "";
            long delta = ScopeCostBytes(Grog.Core.Sync.Scope.Both) - ScopeCostBytes(EffectiveScope);
            return delta <= 0 ? "" : Grog.Core.Format.ByteFormat.Size(delta);
        }
    }
    /// <summary>The savings sentence. Shown only when the delta is worth a decision (> 1 GB).</summary>
    public string ScanSavingsText
    {
        get
        {
            if (_manifest is null) return "";
            long all = ScopeCostBytes(Grog.Core.Sync.Scope.Both);
            long sel = ScopeCostBytes(EffectiveScope);
            long delta = all - sel;
            return delta <= 0 ? ""
                : $"Your current selection saves {Grog.Core.Format.ByteFormat.Size(delta)} against backing up everything. "
                + "You can change what gets backed up in Settings.";
        }
    }
    public bool ShowScanScopeBlock => _manifest is not null
        && ScopeCostBytes(Grog.Core.Sync.Scope.Both) - ScopeCostBytes(EffectiveScope) > 1L << 30;

    private string ScopeCostLine(Grog.Core.Sync.Scope scope)
    {
        var s = Grog.Core.Sync.LibraryStats.From(_manifest!.Current, scope, ShowLegacyMovies);
        return $"{s.InScopeFiles:N0} files  ·  {Grog.Core.Format.ByteFormat.Size(s.PresentBytes + s.GapBytes)}";
    }
    private long ScopeCostBytes(Grog.Core.Sync.Scope scope)
    {
        var s = Grog.Core.Sync.LibraryStats.From(_manifest!.Current, scope, ShowLegacyMovies);
        return s.PresentBytes + s.GapBytes;
    }

    /// <summary>Record scan results; raise the summary only for user-requested scans.</summary>
    internal void NoteScanResult(Grog.Core.Sync.SyncResult r, bool userAsked = true)
    {
        _lastScanErrors = r.ParseFailures;   // fetch-failed products live in the excluded bucket instead
        _lastScanFailedIds = r.FailedProductIds ?? new();
        _lastNewGameIds = r.NewGameIds ?? new();
        _lastNewFiles = r.NewFiles;
        _lastScanRan = true;
        OnPropertyChanged(nameof(ShowScanNewGames)); OnPropertyChanged(nameof(ScanNewGamesLine));
        OnPropertyChanged(nameof(ShowScanEmptyLibrary));
        OnPropertyChanged(nameof(ScanSeenCount));
        OnPropertyChanged(nameof(ScanNoFilesCount)); OnPropertyChanged(nameof(ScanErrorCount));
        OnPropertyChanged(nameof(ScanHadErrors));
        OnPropertyChanged(nameof(ScanEverythingLine)); OnPropertyChanged(nameof(ScanSelectionLine));
        OnPropertyChanged(nameof(ScanSelectionLabel)); OnPropertyChanged(nameof(ScanSavingsText)); OnPropertyChanged(nameof(ScanSavingsLine));
        OnPropertyChanged(nameof(ShowScanScopeBlock));
        OnPropertyChanged(nameof(ShowScanAccountsLine)); OnPropertyChanged(nameof(ScanAccountsLine));
        // Every account skipped = nothing was scanned: say so where the user looks, with Fix routing,
        // never an empty summary as if a scan ran.
        if (userAsked && _lastScanPasses is { Count: > 0 } lp && lp.All(x => x.Skipped))
        {
            ShowToast(lp.Count == 1
                ? $"Nothing was scanned - {lp[0].Name} is signed out. Sign in from the Accounts page."
                : "Nothing was scanned - every account is signed out. Sign in from the Accounts page.",
                2, "Accounts");
            return;
        }
        // Every pass FAILED = nothing was scanned either (owner-hit 09-01: a GOG 500 after a library
        // reset looked like "nothing came back" -- the only trace was one log line). Name the error,
        // never open the summary as if a scan ran.
        if (userAsked && _lastScanPasses is { Count: > 0 } fp && fp.All(x => x.Result is null) && fp.Any(x => !x.Skipped))
        {
            var first = fp.FirstOrDefault(x => x.Error is not null)?.Error;
            var why = first is null ? "unknown error" : Grog.Core.Api.GogError.Describe(first);
            ShowToast($"The scan failed - {why} Your library is unchanged.", 2);
            return;
        }
        if (userAsked) ShowCompositionModal = true;
    }

    /// <summary>Has the library ever been scanned? Splits *never-scanned* (-> "Scan my GOG library") from
    /// *scanned-and-empty* (-> "Up to date"). Set true when a catalog scan completes or a scanned manifest
    /// loads.</summary>
    [ObservableProperty] private bool _catalogScanned;
}

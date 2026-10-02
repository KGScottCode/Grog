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

// Library page (S3.2): filter chips and pills, hide / remove / rating scope, multi-select and its back-up actions,
// per-item reconcile, the "still have it" guard, and the selection-aware primary label + right-click submenu counts.
public sealed partial class LibraryViewModel
{
    // ---- Filter-bar CONTENT CHIPS (Game + extras): the primary cross-cutting filter ----
    // One unified bucket set; all-lit = no filter, deselect to narrow. Persisted. Two visual groups
    // (SOFTWARE: Game, Mod | EXTRAS), ONE filter; Game/Mod installers key by product type.
    internal static readonly (string Key, string Label)[] ExtraDefs =
    {
        ("Soundtracks", "Soundtracks"), ("Manuals", "Manuals"), ("Videos", "Videos"), ("Art", "Art"),
        // Localizations shows only with in-scope localization content; Alt Versions whenever language-less
        // alternate builds exist. Both toggle via FilterChip.IsVisible (RefreshConditionalChips).
        ("Localizations", "Localizations"), ("Alt Versions", "Alt Versions"), ("Other", "Other"),
    };
    public ObservableCollection<FilterChip> SoftwareChips { get; } = new();
    public ObservableCollection<FilterChip> ExtraFilterChips { get; } = new();
    internal IEnumerable<FilterChip> AllChips => SoftwareChips.Concat(ExtraFilterChips);
    internal HashSet<string> _selectedContent = new(StringComparer.OrdinalIgnoreCase);
    internal bool _allContentSelected = true;

    private void SeedChips()
    {
        var off = _settings.DeselectedContentChips ?? new();
        void Add(ObservableCollection<FilterChip> col, string key, string label, IBrush swatch)
        {
            col.Add(new FilterChip
            {
                Label = label, Value = key, Swatch = swatch,
                Icon = ChipIcons.For(key),
                IsActive = !off.Contains(key), Changed = OnChipsChanged,
            });
        }
        SoftwareChips.Clear(); ExtraFilterChips.Clear();
        Add(SoftwareChips, "Game", "Game", ExtraTypeSegment.ColorFor("Game"));
        Add(SoftwareChips, "Mod", "Mod", ExtraTypeSegment.ColorFor("Mod"));
        foreach (var (key, label) in ExtraDefs) Add(ExtraFilterChips, key, label, ExtraTypeSegment.ColorFor(key));
        RecomputeSelectedContent();
        RefreshConditionalChips();
    }

    private void RecomputeSelectedContent()
    {
        _selectedContent = AllChips.Where(c => c.IsActive).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _allContentSelected = _selectedContent.Count == SoftwareChips.Count + ExtraFilterChips.Count;
        GameRowViewModel.ActiveExtraFilter = _allContentSelected ? null : _selectedContent;   // drives square dim/sort
    }

    private void OnChipsChanged()
    {
        RecomputeSelectedContent();
        _settings.DeselectedContentChips = AllChips.Where(c => !c.IsActive).Select(c => c.Value).ToList();
        _settings.SaveSoon();
        ApplyFilter();
        foreach (var g in _allGames) g.RaiseExtraChips();   // refresh per-row square dim + reorder
        RaiseChipDerived();
    }

    private void RaiseChipDerived()
    {
        OnPropertyChanged(nameof(FilterSummaryText));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(SyncBackupLabel));
        _root.RaiseBarState();   // label AND command follow QueueHeldResume
    }

    public string FilterSummaryText
    {
        // Count only ("104 of 120"); the static "Showing" label lives in the toolbar so the readout stays narrow.
        // The denominator is "everything the grid COULD show": the same HiddenPolicy ApplyFilter uses, never a
        // hand-rolled copy. The copy that stood here knew the per-game picks and not the mature toggle, so with
        // mature content hidden the readout was "104 of 120" on a grid that can never show 120 (sweep 2 #17).
        get { var hidden = Hidden; int total = _allGames.Count(g => (ShowLegacyMovies || g.Type != ProductType.Movie) && (g.Item is null || hidden.IsVisible(g.Item))); return $"{Games.Count} of {total}"; }
    }
    public bool HasActiveFilters => !_allContentSelected
        || !string.IsNullOrWhiteSpace(FilterText) || StatusFilterActive || OwnerFilterActive || FolderFilterActive || ExtrasFilterActive || !string.IsNullOrEmpty(QuickFilter);

    // ---- Responsive filter pills ----
    // All pills step down together through three render modes as the toolbar narrows; the view
    // (MainWindow.axaml.cs) measures width and picks the mode. Per-pill fitting is deliberately not attempted.
    /// <summary>Filter-pill render modes, widest to narrowest; the view steps down as space runs out.</summary>
    public enum PillDisplayMode { Full = 0, NameOnly = 1, IconOnly = 2 }

    private PillDisplayMode _pillMode = PillDisplayMode.Full;
    public PillDisplayMode PillMode
    {
        get => _pillMode;
        set
        {
            if (_pillMode == value) return;
            _pillMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PillShowIcon));
            OnPropertyChanged(nameof(PillShowLabel));
            OnPropertyChanged(nameof(PillShowCaption));
        }
    }
    /// <summary>The "Filter" caption before the chips: hides with the labels, in the narrowest mode only (owner 09-16).</summary>
    public bool PillShowCaption => _pillMode != PillDisplayMode.IconOnly;
    /// <summary>Icon tile shows in Full + IconOnly, hides in NameOnly.</summary>
    public bool PillShowIcon => _pillMode != PillDisplayMode.NameOnly;
    /// <summary>Label shows in Full + NameOnly, hides in IconOnly.</summary>
    public bool PillShowLabel => _pillMode != PillDisplayMode.IconOnly;

    /// <summary>Toggle a content chip (bound to each chip button).</summary>
    [RelayCommand]
    private void ToggleChip(FilterChip? chip) { if (chip is not null) chip.IsActive = !chip.IsActive; }

    /// <summary>Settings toggle: show GOG's legacy Movie products. Default off.</summary>
    public bool ShowLegacyMovies
    {
        get => _settings.ShowLegacyMovies;
        set
        {
            if (_settings.ShowLegacyMovies == value) return;
            _settings.ShowLegacyMovies = value; _settings.SaveSoon();
            OnPropertyChanged();
            ApplyFilter(); RaiseChipDerived();
        }
    }

    // ---- Hide from grid (discretion; visual-only -- hidden items are STILL backed up) ----
    private readonly HashSet<long> _hiddenIds = new();
    private bool _showHiddenItems;   // deliberately NOT persisted: resets off each launch so hidden stays hidden
    /// <summary>Settings toggle: reveal hidden rows so they can be unhidden. Not persisted.</summary>
    public bool ShowHiddenItems
    {
        get => _showHiddenItems;
        set
        {
            if (_showHiddenItems == value) return;
            _showHiddenItems = value;
            InvalidateHidden();
            OnPropertyChanged();
            ApplyFilter();
            OnPropertyChanged(nameof(FilterSummaryText));
            OnPropertyChanged(nameof(FilterCountText)); OnPropertyChanged(nameof(HasFilterCount));
        }
    }

    /// <summary>Right-click "Hide from grid": drop the selected rows from view. Visual only -- files stay
    /// backed up. "Unhide" reverses it (shown only while Show-hidden reveals the rows).</summary>
    [RelayCommand] private void HideSelected()   => SetSelectionHidden(true);
    [RelayCommand] private void UnhideSelected() => SetSelectionHidden(false);

    private void SetSelectionHidden(bool hidden)
    {
        if (SelectedRows.Count == 0) return;
        var ids = SelectedRows.Select(r => r.GogId).ToList();   // capture before we clear the selection
        foreach (var id in ids) { if (hidden) _hiddenIds.Add(id); else _hiddenIds.Remove(id); }
        InvalidateHidden();
        _settings.HiddenIds = _hiddenIds.ToList(); _settings.SaveSoon();
        ClearSelection();     // the affected rows are about to appear/disappear
        ApplyFilter();
        _root.RefreshHiddenSurfaces();   // (09-19)
        OnPropertyChanged(nameof(FilterSummaryText));
        OnPropertyChanged(nameof(FilterCountText)); OnPropertyChanged(nameof(HasFilterCount));
    }

    // ---- Remove from library (untrack: the manifest forgets the items; bytes stay on disk) ----
    // Distinct from Hide: a removed game is no longer counted, verified or queued, and a rescan by an
    // owning account brings it back. The dialog says so and points at Hide for permanent concealment.
    [ObservableProperty] private bool _showRemoveItemsConfirm;
    [ObservableProperty] private string _removeItemsConfirmText = "";
    private List<long> _pendingRemoveIds = new();

    /// <summary>Right-click "Remove from library": two-step (scrim confirm) untrack of the selection.</summary>
    [RelayCommand]
    private void RemoveSelected()
    {
        if (_manifest is null || SelectedRows.Count == 0) return;
        _pendingRemoveIds = SelectedRows.Select(r => r.GogId).ToList();
        int n = _pendingRemoveIds.Count;
        var what = n == 1 ? $"{SelectedRows[0].Title} leaves" : $"{n} games leave";
        RemoveItemsConfirmText = $"{what} the library: no longer counted, checked or queued. Downloaded files "
                               + "stay on disk, and the next scan of an owning account adds removed games back. "
                               + "To keep a game off the grid permanently, use Hide from grid instead.";
        ShowRemoveItemsConfirm = true;
    }

    [RelayCommand]
    private void CancelRemoveItems() { ShowRemoveItemsConfirm = false; _pendingRemoveIds = new(); }

    [RelayCommand]
    private async Task ConfirmRemoveItems()
    {
        ShowRemoveItemsConfirm = false;
        var ids = _pendingRemoveIds; _pendingRemoveIds = new();
        if (_manifest is null || ids.Count == 0) return;
        int n = _manifest.Read(m => Grog.Core.Sync.LibraryUntrack.RemoveItems(m, ids));   // (manifest gate 09-08)

        _liveEngine?.CancelGames(ids.ToHashSet());   // queue entries are purged; stop any in-flight transfer too
        ClearSelection();
        await _manifest.SaveAsync();
        _root.RefreshFromManifest();
        _root.Storage.RefreshBackupLocations();   // queue changed: re-plan fits-nowhere
        Log($"Removed {n} game(s) from the library. Files on disk are untouched; a rescan re-adds owned games.");
    }

    // ---- (New items 09-09) The New flag: clearing it, and the header's "any left?" gate ----
    /// <summary>How many items still carry the New flag (the rail line, the header button and the manual-
    /// rescan prompt all read this). Zero when there is no manifest yet.</summary>
    // (owner 09-25, Linux walk) Only what the Library can show: an out-of-scope or hidden item is not "new" to
    // someone whose Settings leave it out (17 New, several of them Linux-less games under a Linux-only scope).
    public int NewItemCount
    {
        get
        {
            if (_manifest is null) return 0;
            if (_allGames.Count == 0) return _manifest.Read(Grog.Core.Sync.NewItems.Count);   // rows not built yet
            var hidden = Hidden;
            return _allGames.Count(g => g.IsNew && !g.NotInScope && (g.Item is null || hidden.IsVisible(g.Item)));
        }
    }
    public bool HasNewItems => NewItemCount > 0;

    /// <summary>Right-click "Clear New Status > Selected Item": drop the flag on the current selection. Nothing else changes --
    /// the items keep their backup state and stay in the library.</summary>
    [RelayCommand]
    private async Task ClearNew()
    {
        if (_manifest is null || SelectedRows.Count == 0) return;
        var ids = SelectedRows.Select(r => r.GogId).ToList();
        var manifest = _manifest;
        int n = await Task.Run(() => { int c = 0; manifest.Mutate(m => c = Grog.Core.Sync.NewItems.Clear(m, ids)); return c; });   // gate wait off the dispatcher
        if (n == 0) return;
        await manifest.SaveAsync();
        RebuildRows();
        RaiseNewItems();
        Log($"Cleared the New flag on {n} item(s).");
    }

    /// <summary>Right-click "Clear New Status > All Items": drop every flag at once. Was a link in the
    /// filter bar until 09-09; among the chips it read as a label rather than an action, and the wider scope
    /// belongs beside the narrower one rather than in a different part of the screen.</summary>
    [RelayCommand]
    private async Task ClearAllNew()
    {
        if (_manifest is null) return;
        var manifest = _manifest;
        int n = await Task.Run(() => { int c = 0; manifest.Mutate(m => c = Grog.Core.Sync.NewItems.ClearAll(m)); return c; });   // gate wait off the dispatcher
        if (n == 0) return;
        await manifest.SaveAsync();
        RebuildRows();
        RaiseNewItems();
        Log($"Cleared the New flag on {n} item(s).");
    }

    /// <summary>The New-derived readouts as ONE set: the header button's visibility and the rail's line.</summary>
    internal void RaiseNewItems()
    {
        OnPropertyChanged(nameof(NewItemCount));
        OnPropertyChanged(nameof(HasNewItems));
        _root.Overview.RaiseRailBadge();
    }

    // ---- Content rating filter (parental discretion; a persistent SCOPE like Hide, not a transient chip) ----
    private static readonly HashSet<string> _allBoardSet =
        new(RatingBoards.Keys, StringComparer.OrdinalIgnoreCase);

    /// <summary>Settings master toggle: hide 18+ games. One adult cutoff across all boards.</summary>
    public bool HideMatureContent
    {
        get => _settings.HideMatureContent;
        set
        {
            if (_settings.HideMatureContent == value) return;
            // (09-26, owner) Turning it ON first shows the games it would hide; nothing changes until "Hide All".
            if (value && OpenMatureReview()) { Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(HideMatureContent))); return; }
            SetHideMature(value);
        }
    }

    private void SetHideMature(bool value)
    {
        _settings.HideMatureContent = value; _settings.SaveSoon();
        InvalidateHidden();
        OnPropertyChanged(nameof(HideMatureContent));
        ApplyFilter(); RaiseChipDerived();
        OnPropertyChanged(nameof(FilterSummaryText));
        _root.RefreshHiddenSurfaces();   // (09-19) the rail and every other list that names games read the same policy
    }

    // ---- (09-26, owner) Mature review: the games the rating filter would hide, each with a Keep box ----
    public ObservableCollection<MatureReviewRow> MatureReviewGogOnly { get; } = new();
    public ObservableCollection<MatureReviewRow> MatureReviewBoards { get; } = new();
    [ObservableProperty] private bool _showMatureReview;
    [ObservableProperty] private string _matureReviewTitle = "";
    public bool MatureReviewHasGogOnly => MatureReviewGogOnly.Count > 0;
    public bool MatureReviewHasBoards => MatureReviewBoards.Count > 0;

    // (owner 10-01) One ALL box per section: ticking it keeps every game in that section; it reads ticked only
    // while every row is kept, so un-ticking a single game clears it.
    private bool _settingAll;
    public bool KeepAllGogOnly
    {
        get => MatureReviewGogOnly.Count > 0 && MatureReviewGogOnly.All(r => r.Keep);
        set => SetAllKeep(MatureReviewGogOnly, value);
    }
    public bool KeepAllBoards
    {
        get => MatureReviewBoards.Count > 0 && MatureReviewBoards.All(r => r.Keep);
        set => SetAllKeep(MatureReviewBoards, value);
    }
    private void SetAllKeep(ObservableCollection<MatureReviewRow> rows, bool keep)
    {
        _settingAll = true;
        try { foreach (var r in rows) r.Keep = keep; }
        finally { _settingAll = false; }
        RaiseKeepAll();
    }
    private void RaiseKeepAll() { OnPropertyChanged(nameof(KeepAllGogOnly)); OnPropertyChanged(nameof(KeepAllBoards)); }
    private void OnMatureRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (!_settingAll && e.PropertyName == nameof(MatureReviewRow.Keep)) RaiseKeepAll(); }

    /// <summary>Fill the review from the rating test (Keep ticked for games already kept). False when nothing is rated
    /// 18+, so the toggle simply applies.</summary>
    private bool OpenMatureReview()
    {
        if (_manifest is null) return false;
        var policy = Hidden with { HideMature = true };
        var kept = new HashSet<long>(_settings.MatureKeptIds);
        var rows = _manifest.Current.Items.Where(policy.IsMatureRated).OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(i => new MatureReviewRow(i.GogId, i.Title, RatingsText(i.AgeRatings), i.AgeRatings.Keys.All(k => k.Equals("gog", StringComparison.OrdinalIgnoreCase))) { Keep = kept.Contains(i.GogId) })
            .ToList();
        if (rows.Count == 0) return false;
        MatureReviewGogOnly.Clear(); MatureReviewBoards.Clear();
        foreach (var r in rows) { r.PropertyChanged += OnMatureRowChanged; (r.GogOnly ? MatureReviewGogOnly : MatureReviewBoards).Add(r); }
        RaiseKeepAll();
        MatureReviewTitle = rows.Count == 1 ? "1 game will be hidden" : $"{rows.Count} games will be hidden";
        OnPropertyChanged(nameof(MatureReviewHasGogOnly)); OnPropertyChanged(nameof(MatureReviewHasBoards));
        ShowMatureReview = true;
        return true;
    }

    private static string RatingsText(IReadOnlyDictionary<string, int> r)
        => string.Join("  ·  ", RatingBoards.All(r.Keys).Where(k => r.TryGetValue(k, out var a) && a > 0).Select(k => $"{RatingBoards.Acronym(k)} {r[k]}"));

    /// <summary>Settings "Review…": the same list while the filter is on.</summary>
    [RelayCommand] private void ReviewMature() => OpenMatureReview();

    [RelayCommand]
    private void ConfirmMatureReview()
    {
        // Every row's Keep is written: a game un-ticked here is hidden again (the list is the whole truth for rated games).
        var listed = MatureReviewGogOnly.Concat(MatureReviewBoards).ToList();
        var kept = new HashSet<long>(_settings.MatureKeptIds);
        foreach (var r in listed) { if (r.Keep) kept.Add(r.GogId); else kept.Remove(r.GogId); }
        _settings.MatureKeptIds = kept.ToList();
        InvalidateHidden();
        ShowMatureReview = false;
        int hidden = listed.Count(r => !r.Keep), keptN = listed.Count - hidden;
        var tail = $"{hidden} game(s) hidden{(keptN > 0 ? $", {keptN} kept visible" : "")}. Backups are unchanged.";
        _root.Log(_settings.HideMatureContent ? $"You updated the mature review: {tail}" : $"You turned on Hide mature content: {tail}");
        if (_settings.HideMatureContent) { _settings.SaveSoon(); ApplyFilter(); RaiseChipDerived(); OnPropertyChanged(nameof(FilterSummaryText)); _root.RefreshHiddenSurfaces(); }
        else SetHideMature(true);
    }

    [RelayCommand] private void CancelMatureReview() => ShowMatureReview = false;

    /// <summary>(09-26, owner) The mature filter reads GOG's own rating only. A game GOG never rated stays visible.</summary>
    private static readonly string[] GogBoardOnly = { "gog" };

    /// <summary>The adult cutoff age; unrated games stay visible.</summary>
    private const int AdultAge = Grog.Core.Sync.HiddenPolicy.AdultAge;

    /// <summary>THE definition of "hidden", cached and rebuilt after any input changes (<see cref="InvalidateHidden"/>).
    /// Scope decides what is BACKED UP; this decides what is SHOWN (<see cref="Grog.Core.Sync.HiddenPolicy"/>); never hand-roll it.</summary>
    public Grog.Core.Sync.HiddenPolicy Hidden => _hidden ??= new(
        HideMatureContent, _settings.MatureAgeThreshold,
        GogBoardOnly,   // (09-26, owner) GOG's own store rating decides; the other boards caught violent classics
        _hiddenIds.Count > 0 ? _hiddenIds : null,   // the live set itself; the policy reads membership only
        _showHiddenItems,
        _settings.MatureKeptIds is { Count: > 0 } ? _settings.MatureKeptIds : null);
    private Grog.Core.Sync.HiddenPolicy? _hidden;

    /// <summary>Called at every site that changes a policy input: the hidden ids, the mature toggle, the kept ids, Show hidden.</summary>
    private void InvalidateHidden() => _hidden = null;

    // ---- Multi-select + "Sync selected" ----
    public ObservableCollection<GameRowViewModel> SelectedRows { get; } = new();
    [ObservableProperty] private int _selectedActionableCount;
    public bool HasSelection => SelectedActionableCount > 0;

    /// <summary>Called by the view when the grid's selection changes.</summary>
    public void UpdateSelection(System.Collections.IList selected)
    {
        SelectedRows.Clear();
        int actionable = 0;
        foreach (var o in selected)
            if (o is GameRowViewModel r)
            {
                SelectedRows.Add(r);
                if (r.Status.NeedsWork()) actionable++;
            }
        SelectedActionableCount = actionable;
        RefreshBackUpMenuCounts();   // per-scope pending counts for the right-click submenu
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SyncBackupLabel));
        OnPropertyChanged(nameof(BackUpSelectedLabel));
        OnPropertyChanged(nameof(ClearNewSelectedLabel));
        _root.RaisePrimaryEnabled();
        OnPropertyChanged(nameof(FilterCountText)); OnPropertyChanged(nameof(HasFilterCount));
        _root.RaiseBarState();   // primary is selection-aware ("Back up · 3 selected")
    }

    /// <summary>How many rows a filter is hiding: STATE beside the grid, not a transient message.
    /// Empty when nothing is filtered.</summary>
    public string FilterCountText
    {
        get
        {
            // Base excludes hidden items, so hiding never shows a phantom "Showing N of M".
            int baseN = _showHiddenItems ? _allGames.Count : _allGames.Count(g => !_hiddenIds.Contains(g.GogId));
            return _root.IsLibraryNav && baseN > 0 && Games.Count < baseN ? $"Showing {Games.Count} of {baseN}" : "";
        }
    }
    public bool HasFilterCount => FilterCountText.Length > 0;

    /// <summary>One click back to the whole library: search, quick-filters, header dropdowns and content
    /// chips all reset. Lives on the filtered strip, which only exists when something is narrowed.</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        FilterText = "";
        QuickFilter = "";
        foreach (var o in TypeOptions) o.SetCheckedQuiet(true);
        foreach (var o in StatusOptions) o.SetCheckedQuiet(o.Value != NotInScopeBucket);   // reset = the DEFAULT view, which excludes Not in Scope
        foreach (var o in OwnerOptions) o.SetCheckedQuiet(true);
        foreach (var o in FolderOptions) o.SetCheckedQuiet(true);
        foreach (var o in ExtrasOptions) o.SetCheckedQuiet(true);
        foreach (var c in SoftwareChips) c.SetActiveQuiet(true);
        foreach (var c in ExtraFilterChips) c.SetActiveQuiet(true);
        RecomputeSelectedContent();
        OnPropertyChanged(nameof(OwnerFilterActive));
        OnPropertyChanged(nameof(FolderFilterActive));
        OnPropertyChanged(nameof(ExtrasFilterActive));
        ApplyFilter();
    }

    /// <summary>A Status/Type header filter limits the grid beyond the DEFAULT view (which has "Not in Scope"
    /// unchecked, so that alone never reads as active); used to bold the header.</summary>
    public bool StatusFilterActive => StatusOptions.Any(o => o.IsChecked != (o.Value != NotInScopeBucket));
    public bool TypeFilterActive => TypeOptions.Count(o => o.IsChecked) < TypeOptions.Count;

    [RelayCommand]
    internal async Task SyncSelected()
    {
        if (SelectedRows.Count == 0) return;
        var ids = SelectedRows.Select(r => r.GogId).ToHashSet();
        await _root.DownloadFiltered(updatesOnly: false, onlyGameIds: ids);
    }

    // Right-click "Back up" submenu: force a scope for THIS action only, never touching the persisted scope.
    [RelayCommand(AllowConcurrentExecutions = true)] private Task SyncSelectedAll()    => SyncSelectedScoped(games: true,  extras: true);
    [RelayCommand(AllowConcurrentExecutions = true)] private Task SyncSelectedGames()  => SyncSelectedScoped(games: true,  extras: false);
    [RelayCommand(AllowConcurrentExecutions = true)] private Task SyncSelectedExtras() => SyncSelectedScoped(games: false, extras: true);

    /// <summary>(09-20, owner) The other intention: these games FIRST. Queues what is not queued, moves all of it to
    /// the front of the queue, and starts or resumes the run. Same words as the queue row's "Download this next".</summary>
    // AllowConcurrentExecutions on the four entries (09-22): an async command is disabled while its last click is
    // awaited, and "All files" awaits the whole download run it starts, so the entry was dead for the run.
    [RelayCommand(AllowConcurrentExecutions = true)] private Task DownloadSelectedNext() => SyncSelectedScoped(games: true, extras: true, next: true);
    public string DownloadNextLabel => $"Download next \u00b7 {SelectedRows.Count} {(SelectedRows.Count == 1 ? "item" : "items")}";

    private async Task SyncSelectedScoped(bool games, bool extras, bool next = false)
    {
        if (SelectedRows.Count == 0) return;
        if (!_root.RequireAccount("back up the selected games")) return;
        var ids = SelectedRows.Select(r => r.GogId).ToHashSet();

        // RE-SCOPE the queue for the selection: drop out-of-scope files (partials on disk kept) so
        // "Extras only" means extras only even when games were already queued; DownloadFiltered re-enqueues.
        if (_manifest is not null)
        {
            bool removed = false;
            // Collect, then apply in two batches -- see the note in LibraryViewModel.Scope.cs: per-file this
            // was a gate enter/exit plus a CancelFile whose Snapshot scan copies the whole queue (measured 09-10).
            var scope = ScopeFor(games, extras);
            var drop = new List<(long GogId, string FileKey)>();
            foreach (var q in _manifest.Current.Downloads.Snapshot())
            {
                if (!ids.Contains(q.GogId)) continue;
                var item = ItemById(q.GogId);
                var f = item?.Files.FirstOrDefault(x => x.FileKey == q.FileKey);
                if (f is null || scope.Includes(f)) continue;   // keep in-scope (full language+platform)
                drop.Add((q.GogId, q.FileKey));
            }
            if (drop.Count > 0)
            {
                removed = true;
                var keys = drop.ToHashSet();
                var manifest = _manifest;
                await Task.Run(() => manifest.Mutate(m => { foreach (var (g, k) in drop) m.Downloads.Remove(g, k); }));   // gate wait off the dispatcher
                _liveEngine?.CancelRange(_liveEngine.Snapshot.Where(t => keys.Contains((t.File.GameGogId, t.File.FileKey))).ToList());
            }
            if (removed) await _manifest.SaveAsync();
        }

        // (09-20, owner) Neither entry lifts a Pause: a paused queue is the user's, and editing it is not a Resume.
        // "Download next" is ALWAYS a queue edit through the one path that tells a live engine too; it starts a
        // run only when nothing is running AND nothing is paused.
        if (next && _manifest is not null)
        {
            // Last first, so the first selected game ends up at the very front.
            foreach (var id in SelectedRows.Select(r => r.GogId).Reverse().ToList())
                if (ItemById(id) is { } it) _root.QueueRemainingForItem(it, toTop: true, filtered: false, refresh: false);
            _root.AfterQueueEdits(toTop: true);
            RefreshBackUpMenuCounts();
            return;
        }
        // (09-22, owner) A MOVE holds the activity slot: the files are queued now and start when it ends. Starting a
        // run was refused ("a download or move is already running") and the queue edit was lost with it.
        // (09-22, owner) A run is LIVE: DownloadFiltered would exit on Busy and the click did nothing (log 15:20).
        // Queue through the path that also hands the new tasks to the running engine, at the END (no promotion).
        if (_root.DownloadRunning && !_root.DownloadPaused && _manifest is not null)
        {
            foreach (var id in SelectedRows.Select(r => r.GogId).ToList())
                if (ItemById(id) is { } it) _root.QueueRemainingForItem(it, toTop: false, filtered: false, scope: ScopeFor(games, extras), refresh: false);
            _root.AfterQueueEdits(toTop: false);
            RefreshBackUpMenuCounts();
            return;
        }
        if (_root.DownloadPaused && _manifest is not null)
        {
            var picks = Grog.Core.Runs.BackupQueueBuilder.Build(_manifest.Current, null,
                _root.SelectionOptions(updatesOnly: false, onlyGameIds: ids, fromQueue: false, ScopeFor(games, extras), _allContentSelected));
            var manifest = _manifest;
            int added = await Task.Run(() =>   // gate wait and the Contains walk off the dispatcher
            {
                int a = 0;
                manifest.Mutate(m =>
                {
                    foreach (var (it, f) in picks)
                        if (!m.Downloads.Contains(it.GogId, f.FileKey)) { m.Downloads.Enqueue(it.GogId, f.FileKey); a++; }
                });
                return a;
            });
            await manifest.SaveAsync();
            await _root.Overview.ReplanQueueAsync("Add to queue");
            _root.Log($"Added {added} file{(added == 1 ? "" : "s")} to the end of the queue. Downloads stay paused; Resume starts them.", category: LogCategory.Download);
            RefreshBackUpMenuCounts();
            return;
        }
        await _root.DownloadFiltered(updatesOnly: false, onlyGameIds: ids,
                               scopeGamesOverride: games, scopeExtrasOverride: extras, promotePicked: next);
    }

    // ---- Confirm downloaded / Clear backed-up flag (per-item reconcile, non-destructive) ----
    // Confirm = ask the disk (presence + size) and set the truth; Clear = assert not-backed-up without
    // touching disk. Neither deletes files; MD5 was checked at download time, so Confirm is size-only.

    [RelayCommand]
    private async Task ConfirmDownloaded()
    {
        if (_root.Busy || _manifest is null || SelectedRows.Count == 0) return;
        var ids = SelectedRows.Select(r => r.GogId).ToHashSet();
        await _root.RunBusy("Confirming…", async () =>
        {
            // (S2.2) Core's VerifyRun (size-only over the selected products); the App's own line stays.
            var v = await Grog.Core.Runs.VerifyRun.RunAsync(_manifest, _backupRoot, Grog.Core.Runs.VerifyRequest.ConfirmItems(ids),
                new Grog.Core.Runs.NullBackupHost(), _cts?.Token ?? default);
            var r = v.Verify;
            await _root.ReconcileAndRefresh();
            Log($"Confirmed {v.Items} item(s): {r.SizeOnlyOk} present, {r.Missing} missing, {r.Corrupt} wrong size");
        });
    }

    [RelayCommand]
    private Task MarkNotBackedUp() => MarkSelected(FileState.NotBackedUp, "Not Backed Up");

    [RelayCommand]
    private Task MarkMissing() => MarkSelected(FileState.Missing, "Missing");

    // ---- "You still have this file" guard ----
    [ObservableProperty] private bool _showMarkNotBackedUpConfirm;
    [ObservableProperty] private string _markWarningText = "";
    /// <summary>The dialog's "Don't ask again" box; persisted only if the mark goes through.</summary>
    [ObservableProperty] private bool _dontAskMarkAgain;

    /// <summary>The mark to perform on confirm: held, not re-derived, so it is exactly the action warned
    /// about even if the selection changes behind the dialog.</summary>
    private Func<Task>? _pendingMark;

    /// <summary>(UI-thread sweep 09-06) How many of the given files are physically on disk. ONE BackupLayout
    /// (its ctor probes every root) and every File.Exists run off the dispatcher.</summary>
    private Task<int> CountFilesOnDiskAsync(System.Collections.Generic.IReadOnlyList<GameFile> files)
    {
        var manifest = _manifest; var root = _backupRoot;
        if (manifest is null || string.IsNullOrEmpty(root) || files.Count == 0) return Task.FromResult(0);
        return Task.Run(() =>
        {
            Grog.Core.Volumes.BackupLayout layout;
            try { layout = new Grog.Core.Volumes.BackupLayout(manifest.Current, root); }
            catch { return 0; }   // offline drive / unresolvable: not something to warn about
            return files.Count(f => FileIsOnDisk(layout, f));
        });
    }

    private Task<int> LocalCopiesOnDiskAsync(System.Collections.Generic.IEnumerable<long> gogIds)
    {
        var files = gogIds.Select(ItemById).Where(i => i is not null).SelectMany(i => i!.Files).ToList();
        return CountFilesOnDiskAsync(files);
    }

    private static bool FileIsOnDisk(Grog.Core.Volumes.BackupLayout layout, GameFile f)
    {
        try
        {
            var p = layout.ResolvePath(f);
            return p is not null && System.IO.File.Exists(p);
        }
        catch { return false; }   // offline drive / unresolvable: not something to warn about
    }

    [RelayCommand]
    private void CancelMarkNotBackedUp()
    {
        ShowMarkNotBackedUpConfirm = false;
        _pendingMark = null;
        DontAskMarkAgain = false;
    }

    [RelayCommand]
    private async Task ConfirmMarkNotBackedUp()
    {
        ShowMarkNotBackedUpConfirm = false;
        if (DontAskMarkAgain) { _settings.SuppressMarkNotBackedUpWarning = true; _settings.SaveSoon(); }
        DontAskMarkAgain = false;
        var go = _pendingMark; _pendingMark = null;
        if (go is not null) await go();
    }

    private async Task MarkSelected(FileState target, string label)
    {
        if (_manifest is null || SelectedRows.Count == 0) return;
        var ids = SelectedRows.Select(r => r.GogId).ToList();

        // Warn only when a copy is actually on disk (the mistake costs a re-download); a warning that
        // fires with nothing to lose is noise.
        if (target == FileState.NotBackedUp && !_settings.SuppressMarkNotBackedUpWarning)
        {
            int present = await LocalCopiesOnDiskAsync(ids);   // (UI-thread sweep 09-06) one layout, off the dispatcher
            if (present > 0)
            {
                _pendingMark = () => MarkSelectedNow(ids, target, label);
                MarkWarningText = $"Grog can see {present} file(s) on disk for this selection. Marking them not "
                                + "backed up won't delete anything, but Grog will download them again.";
                ShowMarkNotBackedUpConfirm = true;
                return;
            }
        }

        await MarkSelectedNow(ids, target, label);
    }

    private async Task MarkSelectedNow(System.Collections.Generic.List<long> ids, FileState target, string label)
    {
        if (_manifest is null) return;
        int changed = 0;
        foreach (var id in ids)
            changed += Grog.Core.Sync.ManualBackupState.MarkProduct(_manifest.Current, id, target);
        await _manifest.SaveAsync();
        await _root.ReconcileAndRefresh();
        // (09-19) Say what HAPPENED: "Marked 1 item(s) as Missing" was logged for a never-downloaded game where
        // the rule (only bytes Grog held can be Missing) changed nothing.
        Log(changed == 0
            ? $"Nothing to mark as {label}: {(target == FileState.Missing ? "only files Grog has downloaded can be Missing." : "the selection is already in that state.")}"
            : $"Marked {changed} file(s) in {ids.Count} item(s) as {label}. Files on disk are untouched.");
    }

    [RelayCommand]
    private async Task ConfirmFile(DetailFileRow? row)
    {
        if (_root.Busy || _manifest is null || row is null) return;
        var parent = ItemById(row.GogId);
        if (parent is null) return;
        await _root.RunBusy("Confirming…", async () =>
        {
            // (S2.2) Core's VerifyRun over the one file.
            var r = (await Grog.Core.Runs.VerifyRun.RunAsync(_manifest, _backupRoot,
                Grog.Core.Runs.VerifyRequest.ForFiles(new[] { (parent.GogId, row.File.FileKey) }),
                new Grog.Core.Runs.NullBackupHost(), _cts?.Token ?? default)).Verify;
            await _root.ReconcileAndRefresh();
            Log($"Confirmed {row.Name}: {(r.Missing > 0 ? "missing" : r.Corrupt > 0 ? "wrong size" : "present")}");
        });
    }

    [RelayCommand]
    private Task MarkFileNotBackedUp(DetailFileRow? row) => MarkFile(row, FileState.NotBackedUp, "Not Backed Up");

    [RelayCommand]
    private Task MarkFileMissing(DetailFileRow? row) => MarkFile(row, FileState.Missing, "Missing");

    private async Task MarkFile(DetailFileRow? row, FileState target, string label)
    {
        if (_manifest is null || row is null) return;

        // (UI-thread sweep 09-06) the on-disk probe runs off the dispatcher.
        if (target == FileState.NotBackedUp && !_settings.SuppressMarkNotBackedUpWarning
            && await CountFilesOnDiskAsync(new[] { row.File }) > 0)
        {
            _pendingMark = () => MarkFileNow(row, target, label);
            MarkWarningText = $"Grog can see {row.Name} on disk. Marking it not backed up won't delete it, "
                            + "but Grog will download it again.";
            ShowMarkNotBackedUpConfirm = true;
            return;
        }

        await MarkFileNow(row, target, label);
    }

    private async Task MarkFileNow(DetailFileRow row, FileState target, string label)
    {
        if (_manifest is null) return;
        Grog.Core.Sync.ManualBackupState.MarkFile(_manifest.Current, row.GogId, row.File.FileKey, target);
        await _manifest.SaveAsync();
        await _root.ReconcileAndRefresh();
        Log($"Marked {row.Name} as {label}. File on disk is untouched.");
    }

    /// <summary>Raised when the VM wants the grid to drop its selection (the view clears SelectedItems).</summary>
    public event Action? SelectionClearRequested;

    [RelayCommand]
    internal void ClearSelection() => SelectionClearRequested?.Invoke();

    // Software content-chip keys; everything else is an extras bucket. Used to decide games-vs-files noun.
    private static readonly HashSet<string> _softwareChipKeys = new(StringComparer.OrdinalIgnoreCase) { "Game", "Mod" };

    /// <summary>The tour's "back up everything" step makes the primary the whole-library action, so the
    /// button ignores the selection there; the selection itself deliberately stays.</summary>
    internal bool GuideWantsWholeLibrary => _root.Guide.GuideActive && _root.Guide.CurrentStepKey == "back-up-all";

    public string SyncBackupLabel
    {
        get
        {
            // One name for the operation: "Scan GOG Library" before a catalog exists, "Rescan GOG Library"
            // after. Measured at 1920x1080 the button renders the long form in full; re-measure before shortening.
            if (!_root.IsConnected) return "Connect account";
            if (TotalCount == 0) return "Scan GOG Library";
            // A selection targets those rows -> generic "items".
            if (SelectedActionableCount > 0 && !GuideWantsWholeLibrary) return $"Back up · {SelectedActionableCount} item{(SelectedActionableCount == 1 ? "" : "s")}";
            // "Games" is the default noun (users think in games); switch to the FILE count only when the
            // narrowing brings extras into play. Software-only narrowing stays on the game count.
            bool extrasInScope = _selectedContent.Any(k => !_softwareChipKeys.Contains(k));
            if (!_allContentSelected && extrasInScope)
            {
                int n = FilteredPendingFileCount();
                return n > 0 ? $"Back up · {n} file{(n == 1 ? "" : "s")}" : "Backed up ✓";
            }
            return PendingTaskCount > 0 ? $"Back up · {PendingTaskCount} game{(PendingTaskCount == 1 ? "" : "s")}" : "Backed up ✓";
        }
    }

    /// <summary>Count of pending files the current filter chips would queue (mirrors DownloadFiltered's
    /// selection). Drives the scoped primary label. On-demand only.</summary>
    // Memoized per dashboard pass: SyncBackupLabel is read by up to four bindings per RaiseBarState/RaiseDashboard
    // and each read walked the whole library through BackupQueueBuilder when the chips were narrowed
    // (UI-thread sweep 09-06 r2). The epoch bumps in RaiseDashboardCore and RebuildRows; a chip change swaps the
    // _selectedContent set instance (Library.cs), which the key catches by reference.
    private long _pendingCountEpoch;
    private (long Epoch, object? Chips, bool AllContent, bool Movies, bool Games, bool Extras, int Value) _pendingCountMemo;
    internal void InvalidatePendingFileCount() => _pendingCountEpoch++;
    private int FilteredPendingFileCount()
    {
        if (_manifest is null) return 0;
        var key = (_pendingCountEpoch, (object?)_selectedContent, _allContentSelected, ShowLegacyMovies, _includeGames, _includeExtras);
        var memo = _pendingCountMemo;
        if (memo.Epoch == key.Item1 && ReferenceEquals(memo.Chips, key.Item2) && memo.AllContent == key.Item3
            && memo.Movies == key.Item4 && memo.Games == key.Item5 && memo.Extras == key.Item6)
            return memo.Value;
        // Same rule as the enqueue (SelectionOptions); no layout = no volume scan on a label refresh.
        int n = Grog.Core.Runs.BackupQueueBuilder.Build(_manifest.Current, null,
            _root.SelectionOptions(updatesOnly: false, onlyGameIds: null, fromQueue: false, EffectiveScope, _allContentSelected)).Count;
        _pendingCountMemo = (key.Item1, key.Item2, key.Item3, key.Item4, key.Item5, key.Item6, n);
        return n;
    }

    // --- right-click "Back up" submenu: per-scope pending counts (drive the labels + disabled state) ---
    private int _pendAll, _pendGames, _pendExtras;
    public string BackUpAllLabel    => $"All files ({_pendAll})";
    public string BackUpGamesLabel  => $"Game files only ({_pendGames})";
    public string BackUpExtrasLabel => $"Extras only ({_pendExtras})";
    public bool CanBackUpAll    => _pendAll    > 0;
    public bool CanBackUpGames  => _pendGames  > 0;
    public bool CanBackUpExtras => _pendExtras > 0;
    /// <summary>(09-20) Every file the selection still needs is ALREADY in the queue: there is nothing to add, so the
    /// "Add to queue" entry greys out and says so, and "Download next" is the one live choice.</summary>
    private bool _selectionAllQueued;
    public bool CanAddToQueue => CanBackUpAll && !_selectionAllQueued;

    /// <summary>In-scope, filter-passing files in the selection that still need work. Counted whether or not
    /// already queued: a scoped back-up RE-SCOPES the queue, so a paused queue must not zero the count.</summary>
    private int PendingForSelection(bool games, bool extras)
    {
        if (_manifest is null || SelectedRows.Count == 0) return 0;
        var ids = SelectedRows.Select(r => r.GogId).ToHashSet();
        // Full scope (language+platform) and the same rule as the enqueue, so the count matches what queues.
        return Grog.Core.Runs.BackupQueueBuilder.Build(_manifest.Current, null,
            _root.SelectionOptions(updatesOnly: false, onlyGameIds: ids, fromQueue: false, ScopeFor(games, extras), _allContentSelected)).Count;
    }

    /// <summary>Recompute the three submenu counts and refresh their labels + enabled state. Cheap
    /// (scans only the selection); called on selection change and after download/queue state changes.</summary>
    internal void RefreshBackUpMenuCounts()
    {
        _pendAll    = PendingForSelection(games: true,  extras: true);
        _pendGames  = PendingForSelection(games: true,  extras: false);
        _pendExtras = PendingForSelection(games: false, extras: true);
        _selectionAllQueued = false;
        if (_pendAll > 0 && _manifest is not null)
        {
            var ids = SelectedRows.Select(r => r.GogId).ToHashSet();
            var queued = _manifest.Current.Downloads.Snapshot().Select(q => (q.GogId, q.FileKey)).ToHashSet();
            _selectionAllQueued = Grog.Core.Runs.BackupQueueBuilder.Build(_manifest.Current, null,
                    _root.SelectionOptions(updatesOnly: false, onlyGameIds: ids, fromQueue: false, ScopeFor(true, true), _allContentSelected))
                .All(x => queued.Contains((x.Item.GogId, x.File.FileKey)));
        }
        OnPropertyChanged(nameof(CanAddToQueue)); OnPropertyChanged(nameof(BackUpSelectedLabel)); OnPropertyChanged(nameof(DownloadNextLabel));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(BackUpAllLabel));    OnPropertyChanged(nameof(CanBackUpAll));
        OnPropertyChanged(nameof(BackUpGamesLabel));  OnPropertyChanged(nameof(CanBackUpGames));
        OnPropertyChanged(nameof(BackUpExtrasLabel)); OnPropertyChanged(nameof(CanBackUpExtras));
        RaiseLibraryAction();
    }

    /// <summary>Back-up-selection button text; scopes to the current selection.</summary>
    /// (09-20, owner) "Back up" read as both "add it" and "do it now": the two intentions carry their own words.
    public string BackUpSelectedLabel => _selectionAllQueued ? "Already queued"
        : $"Add to queue \u00b7 {SelectedRows.Count} {(SelectedRows.Count == 1 ? "item" : "items")}";

    /// <summary>"Clear New Status > Selected..." text. Singular reads without a count (a "(1)" beside one
    /// highlighted row is noise); plural carries it, so the menu states how much the click covers before
    /// you commit to it -- the same promise the Back up entry above makes.</summary>
    public string ClearNewSelectedLabel
        => SelectedRows.Count == 1 ? "Selected Item" : $"Selected Items ({SelectedRows.Count})";

    // ---- Library action foot: PRIMARY binds the BarState machine; only the SECONDARY has its own label ----
    /// <summary>Secondary label: ALWAYS the rescan, never the run transport -- the foot acts on the selection,
    /// so it must not read "Stop" whenever a download happens to be running.</summary>
    /// <summary>Selection-aware rescan text: selection or whole library, and the "Re" prefix only once a
    /// catalog exists -- "RE-scan" is a lie before the first scan.</summary>
    internal string RescanLabel => SelectedRows.Count > 0
        ? (TotalCount > 0 ? "Rescan Selected" : "Scan Selected")
        : (TotalCount > 0 ? "Rescan GOG Library" : "Scan GOG Library");
    public string LibraryRescanLabel => RescanLabel;

    /// <summary>Anything backed up anywhere yet; gates the Folders page's inventory half.</summary>

    public bool ShowLibrarySecondary => TotalCount > 0;
    public void RaiseLibraryAction() => OnPropertyChanged(nameof(LibraryRescanLabel));
}

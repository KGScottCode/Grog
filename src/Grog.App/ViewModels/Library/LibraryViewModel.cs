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

/// <summary>
/// The Library page's view model (S3.2, 09-08): the game rows and their filters (search, header dropdowns,
/// content chips, quick filters), the hide / remove / rating scope, the multi-select and its actions, the
/// detail pane and its art, the language / platform scope with the select-everything shortcut, and the scan
/// panel with its summary modal. Split into partials by those topics. It owns the library-derived stats
/// (<see cref="Stats"/>, <see cref="Rollup"/>) that the Overview page reads through the root; the session,
/// the run machinery (RunBusy, DownloadFiltered, the primary action), navigation and the log stay on the
/// shell and are reached through <c>_root</c>'s small internal surface.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly MainWindowViewModel _root;
    private readonly GrogSession _session;

    public LibraryViewModel(MainWindowViewModel root, GrogSession session)
    {
        _root = root;
        _session = session;
        // Scope lives in the MANIFEST; these AppSettings values are only the pre-manifest default and the
        // seed for a pre-scope manifest's first load (AdoptManifestScope). Scope changes never write them.
        _includeExtras = _settings.IncludeExtras;
        _includeGames = _settings.IncludeGames;
        _languages = _settings.Languages.ToList();
        // Extras stay NULL unless given their own pick, which is what makes them inherit the games list.
        _extraLanguages = _settings.ExtraLanguagesChosen && _settings.ExtraLanguages is { } ex ? ex.ToList() : null;
        // Platforms follow the Languages contract: empty means every platform. NO first-run seeding --
        // a backup tool must not drop content the user never chose to drop; Settings is the informed opt-out.
        _platforms = _settings.Platforms.ToList();
        RebuildPlatformOptions();
        RebuildContentOptions();   // priced against the same axes
        // If the stored platform narrowing matches no file in the library, fall back to no narrowing
        // rather than silently excluding everything.
        if (_platforms.Count > 0 && PlatformOptions.Count > 0
            && !PlatformOptions.Any(o => _platforms.Contains(o.Key, StringComparer.OrdinalIgnoreCase)))
        {
            _platforms = new List<string>();
            _settings.Platforms = _platforms.ToList();
            _settings.SaveSoon();
            RebuildPlatformOptions();
            RebuildContentOptions();   // priced against the same axes
        }
        _hiddenIds = _settings.HiddenIds is { } hid ? new HashSet<long>(hid) : new HashSet<long>();
        _lastProductsSeen = _settings.LastProductsSeen;
        SeedFilters();
        SeedChips();
    }

    // ---- Session shorthands (same names as the root's, so the moved code reads unchanged) ----
    private JsonManifestStore _manifest => _session.Manifest;
    private AppSettings _settings => _session.Settings;
    private DownloadEngine? _liveEngine => _session.LiveEngine;
    private string _backupRoot => _session.BackupRoot;
    private bool _servicesReady => _session.ServicesReady;
    private CancellationTokenSource? _cts => _session.Cts;
    private void Log(string message, bool isError = false, LogCategory category = LogCategory.General) => _root.Log(message, isError, category);
    private void ShowToast(string message, int severity, ToastFix? fix = null) => _root.ShowToast(message, severity, fix);

    public ObservableCollection<GameRowViewModel> Games { get; } = new();
    internal readonly List<GameRowViewModel> _allGames = new();
    // gogId -> row, kept in step with _allGames, so per-tick "which row?" lookups are O(1).
    internal readonly Dictionary<long, GameRowViewModel> _gameById = new();

    /// <summary>THE item lookup (9 hand-rolled copies before 09-02). O(1) through the row index when the
    /// game has a row, else the manifest scan (packs/DLC have no row).</summary>
    internal LibraryItem? ItemById(long gogId)
        => _gameById.TryGetValue(gogId, out var g) && g.Item is not null ? g.Item
         : _manifest?.Current.ItemById(gogId);   // packs/DLC have no row; before RebuildRows nothing does
    /// <summary>Item + file by the queue's identity (GogId, FileKey); either half null when unknown.</summary>
    internal (LibraryItem? Item, GameFile? File) Resolve(long gogId, string fileKey)
    {
        var item = ItemById(gogId);
        return (item, item?.Files.FirstOrDefault(f => f.FileKey == fileKey));
    }

    /// <summary>The library rows across every filter (the grid shows <see cref="Games"/>).</summary>
    internal IReadOnlyList<GameRowViewModel> AllGames => _allGames;
    public bool HasPendingWork => PendingTaskCount > 0;
    /// <summary>The Owner column shows only with more than one account (the grid's code-behind sizes it).</summary>
    public bool ShowOwnerColumn => OwnerBadges.Show;

    [ObservableProperty] private string _filterText = "";
    partial void OnFilterTextChanged(string value) => ApplyFilter();

    // --- header-dropdown filters (multi-select, All/None) ---
    public ObservableCollection<FilterOption> TypeOptions { get; } = new();
    public ObservableCollection<FilterOption> StatusOptions { get; } = new();
    /// <summary>Owner filter (accounts &gt; 1 only): all checked = no filter; a game is kept when its owners
    /// contain any checked account, so a co-owned game shows in each owner's filtered view.</summary>
    public ObservableCollection<FilterOption> OwnerOptions { get; } = new();
    public bool OwnerFilterActive => OwnerOptions.Count > 0 && OwnerOptions.Any(o => !o.IsChecked);

    /// <summary>Rebuilds badge registry + filter options when accounts change; checks survive by Value.</summary>
    internal void RebuildOwnerBadges(IReadOnlyList<(string Id, string Name)> accounts)
    {
        OwnerBadges.Rebuild(accounts);
        var wasUnchecked = OwnerOptions.Where(o => !o.IsChecked).Select(o => o.Value).ToHashSet();
        OwnerOptions.Clear();
        if (OwnerBadges.Show)
            foreach (var b in OwnerBadges.Map.Values)
            {
                var opt = new FilterOption { Label = b.Name, Value = b.OwnerId, Changed = OnOwnerFilterChanged };
                if (wasUnchecked.Contains(b.OwnerId)) opt.SetCheckedQuiet(false);
                OwnerOptions.Add(opt);
            }
        OnPropertyChanged(nameof(ShowOwnerColumn));
        OnPropertyChanged(nameof(OwnerFilterActive));
    }

    private void OnOwnerFilterChanged() { OnPropertyChanged(nameof(OwnerFilterActive)); ApplyFilter(); }

    /// <summary>Folder filter: where a game's files live (Primary / Secondary / Split / no folder yet).</summary>
    public ObservableCollection<FilterOption> FolderOptions { get; } = new();
    public bool FolderFilterActive => FolderOptions.Any(o => !o.IsChecked);
    private void OnFolderFilterChanged() { OnPropertyChanged(nameof(FolderFilterActive)); ApplyFilter(); }

    /// <summary>Extras COLUMN filter, distinct from the toolbar chips: keeps games having a checked extras
    /// type, plus "No extras" for an empty column -- a different question from the chips.</summary>
    public ObservableCollection<FilterOption> ExtrasOptions { get; } = new();
    public bool ExtrasFilterActive => ExtrasOptions.Any(o => !o.IsChecked);
    private void OnExtrasFilterChanged() { OnPropertyChanged(nameof(ExtrasFilterActive)); ApplyFilter(); }

    private void SeedFilters()
    {
        // Type filter: only separately-downloadable kinds. Packs are store shells and DLC rolls into its
        // parent, so neither is an option; Movies are legacy but may still sit in an existing library.
        foreach (var (label, value) in new[] { ("Game", "Game"), ("Mod", "Mod"), ("Movie (Legacy)", "Movie") })
            TypeOptions.Add(new FilterOption { Label = label, Value = value, Changed = ApplyFilter });
        // "Not in Scope" starts UNCHECKED: scope in Settings wins until the user opts in here.
        // (New items 09-09) "New" is a CROSS-CUTTING bucket, not a status: a new item also has a backup
        // status. It is checked by default, and the predicate below matches it alongside the status bucket.
        foreach (var s in new[] { "Backed Up", "Downloading", "Not Downloaded", "Partial", "Missing", "Updates", NewBucket, NotInScopeBucket })
        {
            var opt = new FilterOption { Label = s, Value = s, Changed = ApplyFilter };
            opt.SetCheckedQuiet(s != NotInScopeBucket);
            StatusOptions.Add(opt);
        }
        foreach (var (label, value) in new[] { ("Primary", "Primary"), ("Secondary", "Secondary"), ("Split", "Split"), ("No storage yet", "\u2014") })
            FolderOptions.Add(new FilterOption { Label = label, Value = value, Changed = OnFolderFilterChanged });
        foreach (var (key, label) in ExtraDefs)
            ExtrasOptions.Add(new FilterOption { Label = label, Value = key, Swatch = ExtraTypeSegment.ColorFor(key), Changed = OnExtrasFilterChanged });
        ExtrasOptions.Add(new FilterOption { Label = "No extras", Value = "None", Changed = OnExtrasFilterChanged });
    }

    [RelayCommand]
    private void FilterAll(string which) => SetAllFilters(which, true);
    [RelayCommand]
    private void FilterNone(string which) => SetAllFilters(which, false);

    private void SetAllFilters(string which, bool value)
    {
        foreach (var o in which switch { "type" => TypeOptions, "owner" => OwnerOptions, "folder" => FolderOptions, "extras" => ExtrasOptions, _ => StatusOptions })
            o.SetCheckedQuiet(value);
        OnPropertyChanged(nameof(OwnerFilterActive));
        OnPropertyChanged(nameof(FolderFilterActive));
        OnPropertyChanged(nameof(ExtrasFilterActive));
        ApplyFilter();
    }

    [ObservableProperty] private string _quickFilter = "";
    partial void OnQuickFilterChanged(string value)
    {
        OnPropertyChanged(nameof(ShowGoneBar)); OnPropertyChanged(nameof(GoneBarText));
    }

    // ---- Contextual resolution bar: the missing-files notice has no dismiss; it resolves at the grid ----
    public bool ShowGoneBar => QuickFilter == "gone" && GoneCount > 0;
    private int GoneCount => _allGames.Count(g => g.Status == BackupStatus.Missing);
    public string GoneBarText => GoneCount == 1
        ? "1 game has files that are no longer on disk."
        : $"{GoneCount} games have files that are no longer on disk.";

    /// <summary>Back up everything currently filtered as missing -- the "get them back" resolution.</summary>
    [RelayCommand]
    private async Task BackUpGone()
    {
        var ids = _allGames.Where(g => g.Status == BackupStatus.Missing).Select(g => g.GogId).ToHashSet();
        if (ids.Count == 0) return;
        await _root.DownloadFiltered(updatesOnly: false, onlyGameIds: ids);
    }

    /// <summary>Accept the loss: these were deleted on purpose, so stop calling them missing --
    /// changes the truth rather than hiding it.</summary>
    [RelayCommand]
    private async Task MarkGoneNotBackedUp()
    {
        if (_manifest is null) return;
        var ids = _allGames.Where(g => g.Status == BackupStatus.Missing).Select(g => g.GogId).ToList();
        if (ids.Count == 0) return;
        foreach (var id in ids)
            Grog.Core.Sync.ManualBackupState.MarkProduct(_manifest.Current, id, FileState.NotBackedUp);
        await _manifest.SaveAsync();
        MissingNotice = "";
        await _root.ReconcileAndRefresh();
        Log($"Marked {ids.Count} game(s) as not backed up. Files on disk are untouched.");
    }

    // --- hero dashboard counts (bound in the header) ---
    public int TotalCount => _allGames.Count;
    internal int _lastProductsSeen;      // raw owned product count from the last sync

    public int CompleteCount => _allGames.Count(g => g.Status == BackupStatus.Complete);
    /// <summary>Games with outstanding FIRST-backup work (not updates). GAME-level, deliberately distinct
    /// from the Status page's file-level <c>HealthGoneCount</c> -- they answer different questions.</summary>
    public int NeedsBackupCount => _allGames.Count(g => g.Status.NeedsFirstBackup());
    public int UpdatesCount => _allGames.Count(g => g.HasUpdate);
    public IBrush BackedUpColor => Palette.SuccessGreen; // always green: the goal
    /// <summary>Updates -- yellow, distinct from Missing's action-amber ("available" vs "act now").</summary>
    public IBrush UpdatesColor => Palette.StrokeStronger;
    /// <summary>Missing is the primary actionable metric -- full amber, matching the Sync button.</summary>
    public IBrush MissingColor => (PendingTaskCount > 0 ? Palette.AccentAmber : Palette.InkDim);
    /// <summary>Backed-up completion 0-100 for the dashboard progress bar.</summary>
    public double BackedUpPercent => TotalCount == 0 ? 0 : 100.0 * CompleteCount / TotalCount;
    public string BackedUpPercentText => TotalCount == 0 ? "" : $"{BackedUpPercent:0}% backed up";
    /// <summary>GAMES with work waiting, each counted ONCE. The sum of the two figures counted a game that is
    /// both missing a file and holding an outdated one twice, so "Back up · N games" overshot (sweep 2 #16).</summary>
    public int PendingTaskCount => _allGames.Count(g => g.Status.NeedsFirstBackup() || g.HasUpdate);

    /// <summary>Owned GOG products that aren't backable games (bundles, promos, giveaway claims). Uses the
    /// domain total (_stats.TotalProducts), never the live grid -- the grid empties mid-rebuild.</summary>
    public int NonGameCount => _lastProductsSeen > _stats.TotalProducts ? _lastProductsSeen - _stats.TotalProducts : 0;
    public bool HasNonGameItems => NonGameCount > 0;

    /// <summary>The "N games, M non-downloadable" composition readouts as ONE set (dashboard refresh,
    /// filter change and post-scan reconciliation all raise it).</summary>
    internal void RaiseComposition()
    {
        OnPropertyChanged(nameof(NonGameCount));
        OnPropertyChanged(nameof(HasNonGameItems));
        _root.RaiseShowCompositionLine();
    }

    internal void RebuildRows() => Services.UiWatch.Time("RebuildRows", RebuildRowsCore);
    internal void RebuildRowsCore()
    {
        if (_manifest is null) return;
        InvalidatePendingFileCount();   // manifest / scope changed under the scoped label (UI-thread sweep 09-06 r2)
        // Keep the per-row squares' language scope in step with the current selection: localizations for
        // unselected languages drop out of the squares, matching the card + details pane.
        GameRowViewModel.ActiveLanguages = _languages.Count > 0 ? _languages : null;
        _stats = Grog.Core.Sync.LibraryStats.From(_manifest.Current, EffectiveScope, ShowLegacyMovies);
        _rollup = Grog.Core.Sync.Rollups.Library(_manifest.Current, EffectiveScope, CurrentInFlight());
        _rollupBaseValid = false;   // manifest/scope may have changed; next tick reseeds the cached base
        // Detail dots resolve Missing-vs-Detached against live device availability.
        DetailFileRow.DeviceLookup = Grog.Core.Sync.ConditionRules.LookupFrom(_manifest.Current);
        var selectedId = SelectedRow?.GogId;   // survive the rebuild without closing the detail pane
        // Owner discs: rebuild the account-to-badge registry alongside the rows that read it, so a just-
        // added second account colors the grid on the same rebuild that shows its games.
        RebuildOwnerBadges(_manifest.Current.Accounts
            .Select(a => (a.Id, string.IsNullOrWhiteSpace(a.Username) ? a.Id : a.Username)).ToList());
        _allGames.Clear();
        _gameById.Clear();
        var registeredIds = _manifest.Current.Accounts.Select(a => a.Id).ToList();
        var connectedIds = _root.Accounts.ConnectedAccountIds();
        foreach (var item in _manifest.Current.Items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase))   // ordinal: the culture comparer cost a table lookup per compare
        {
            // Packs are a store construct and DLC rolls into its parent -- neither is separately
            // downloadable, so they aren't tracked as library rows.
            if (item.Type is ProductType.Pack or ProductType.Dlc) continue;
            var g = GameRowViewModel.FromItem(item, _includeExtras, _includeGames, _manifest.Current.PrimaryRootId, EffectiveScope,
                registeredIds, connectedIds);
            _allGames.Add(g);
            _gameById[g.GogId] = g;
        }
        _root.DownloadPaused = _manifest.Current.Downloads.Paused;   // keep the bindable flag in step with the fact
        _root.RecomputeActivity();   // derive every row's activity from the persisted queues (the single source)
        ApplyFilter();
        if (selectedId is { } id && SelectedRow?.GogId != id)
            SelectedRow = _allGames.FirstOrDefault(g => g.GogId == id);   // re-point to the fresh row instance
        // The Settings scope pickers read the same manifest + scope as the rows, so they rebuild here, once per
        // refresh; RaiseDashboard no longer rebuilds them at 1 Hz during a run (UI-thread sweep 09-06).
        RebuildLanguageOptions();
        RebuildPlatformOptions();
        RebuildContentOptions();   // priced against the same axes
        RaiseNewItems();   // (New items 09-09) a scan may have flagged or an auto-clear retired some
    }

    /// <summary>Maps a game to its Status-filter bucket (matches the header dropdown's four options).</summary>
    private const string NotInScopeBucket = "Not in Scope";
    /// <summary>(New items 09-09) The cross-cutting "New" bucket in the Status dropdown.</summary>
    internal const string NewBucket = "New";

    private static string StatusBucket(GameRowViewModel g)
    {
        if (g.HasUpdate) return "Updates";
        if (g.NotInScope) return NotInScopeBucket;   // before Complete: zero-scoped games derive Complete
        return g.Status switch
        {
            BackupStatus.Complete => "Backed Up",
            BackupStatus.Missing => "Missing",
            BackupStatus.Partial or BackupStatus.Corrupt => "Partial",
            _ => "Not Downloaded",
        };
    }

    internal void ApplyFilter()
    {
        IEnumerable<GameRowViewModel> q = _allGames;
        // ONE hidden test (per-game picks + the mature cutoff), applied FIRST so a hidden title can never
        // leak through search or any other filter. Turn the setting off and everything returns.
        var hidden = Hidden;
        q = q.Where(g => g.Item is null || hidden.IsVisible(g.Item));
        if (!string.IsNullOrWhiteSpace(FilterText))
            q = q.Where(g => g.Title.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

        // Legacy Movie products hidden unless enabled (a product type, not a content bucket).
        if (!ShowLegacyMovies)
            q = q.Where(g => g.Type != ProductType.Movie);

        // CONTENT CHIPS: all lit = no filter. A subset shows games that have a file in a selected bucket.
        // "Game" = installers, so a subset including Game keeps every installer-having game.
        if (!_allContentSelected)
        {
            var sel = _selectedContent;
            q = q.Where(g => g.Item is not null && g.Item.Files.Any(f => sel.Contains(GameRowViewModel.ContentKey(f, g.Type))));
        }

        // Header Status dropdown: show only checked status buckets.
        var statuses = StatusOptions.Where(o => o.IsChecked).Select(o => o.Value).ToHashSet();
        if (statuses.Count < StatusOptions.Count)
            // (New items 09-09) A row is kept when ANY checked bucket describes it: its status bucket, or
            // "New" when the item carries the flag. New sits ACROSS the statuses, so it cannot be one of them.
            // (owner 09-25) "New" never brings back a row Settings scope excludes: Not in Scope stays opt-in.
            q = q.Where(g => statuses.Contains(StatusBucket(g)) || (g.IsNew && !g.NotInScope && statuses.Contains(NewBucket)));

        // Header Owner dropdown: a game stays visible while ANY checked account owns it, so a co-owned
        // game appears in each owner's filtered view. All checked = no filter.
        var owners = OwnerOptions.Where(o => o.IsChecked).Select(o => o.Value).ToHashSet();
        bool ownerNarrowed = OwnerOptions.Count > 0 && owners.Count < OwnerOptions.Count;
        if (ownerNarrowed)
            q = q.Where(g => g.Item is not null && g.Item.OwnerIds.Any(owners.Contains));
        // Only the checked accounts' DISCS render; rows re-read the static state below.
        OwnerBadges.VisibleOwners = ownerNarrowed ? owners : null;

        // Folder dropdown: keep rows whose Folder-column word is checked ("--" = no files placed yet).
        var folders = FolderOptions.Where(o => o.IsChecked).Select(o => o.Value).ToHashSet();
        if (folders.Count < FolderOptions.Count)
            q = q.Where(g => folders.Contains(g.LocationShort));

        // Extras dropdown: keep games having a checked extras type, or with NO extras when "None" is
        // checked. Keys via ContentKey, the same bucketing the column's squares use.
        var extras = ExtrasOptions.Where(o => o.IsChecked).Select(o => o.Value).ToHashSet();
        if (extras.Count < ExtrasOptions.Count)
            q = q.Where(g =>
            {
                if (g.Item is null) return false;
                var hasAny = false;
                foreach (var f in g.Item.Files)
                {
                    if (f.Kind != FileKind.Extra) continue;
                    hasAny = true;
                    if (extras.Contains(GameRowViewModel.ContentKey(f, g.Type))) return true;
                }
                return !hasAny && extras.Contains("None");
            });
        // Rail quick-filters (Updates / Missing) narrow the same ledger.
        q = QuickFilter switch
        {
            "updates" => q.Where(g => g.HasUpdate),
            "missing" => q.Where(g => g.Status.NeedsFirstBackup()),
            "gone" => q.Where(g => g.Status == BackupStatus.Missing),
            _ => q,
        };

        var keepSelectedId = SelectedRow?.GogId;
        // Incremental reconcile (not Clear()+refill): a Reset re-realizes every visible row, which is janky
        // on big libraries during search-as-you-type. Only changed rows are touched.
        ReconcileRowsByGame(Games, q.ToList());
        foreach (var g in Games) g.RaiseOwnersChanged();   // discs re-read the owner-filter state above
        // Keep the detail pane's selection across a filter change: reselect the same game if it's still
        // visible; if it got filtered out, retain the reference so the pane stays open (don't null it).
        if (keepSelectedId is { } keepId)
            SelectedRow = Games.FirstOrDefault(g => g.GogId == keepId) ?? SelectedRow;
        OnPropertyChanged(nameof(FilterCountText)); OnPropertyChanged(nameof(HasFilterCount));
        OnPropertyChanged(nameof(ShowGoneBar)); OnPropertyChanged(nameof(GoneBarText));
        OnPropertyChanged(nameof(StatusFilterActive));
        OnPropertyChanged(nameof(FilterSummaryText)); OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(TypeFilterActive));
        _root.RaiseShowCompositionLine();
        RaiseComposition();
        // With nothing actively selected, the detail pane previews the first visible row -- which the
        // filter just changed -- so refresh it. (When a row IS selected, the pane already tracks it.)
        if (SelectedRow is null) RaiseDetail();
        _root.RaiseDashboard();
    }

    /// <summary>Make <paramref name="col"/> match <paramref name="desired"/> in place by REFERENCE identity,
    /// minimal remove/move/insert -- never Clear()+refill, which re-realizes every rendered container.</summary>
    /// <summary>(09-22, owner) RebuildRows makes NEW row instances, and reconciling by reference removed every row and
    /// inserted every row again: the grid dropped to the top after each landed file. Keyed by game, a row that is
    /// still there is REPLACED in its slot (one Replace notification, scroll and sort untouched).</summary>
    private static void ReconcileRowsByGame(ObservableCollection<GameRowViewModel> col, IReadOnlyList<GameRowViewModel> desired)
    {
        var want = desired.Select(r => r.GogId).ToHashSet();
        for (int i = col.Count - 1; i >= 0; i--)
            if (!want.Contains(col[i].GogId)) col.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < col.Count && col[i].GogId == desired[i].GogId) { if (!ReferenceEquals(col[i], desired[i])) col[i] = desired[i]; continue; }
            int j = -1;
            for (int k = i + 1; k < col.Count; k++)
                if (col[k].GogId == desired[i].GogId) { j = k; break; }
            if (j >= 0) { col.Move(j, i); if (!ReferenceEquals(col[i], desired[i])) col[i] = desired[i]; }
            else col.Insert(i, desired[i]);
        }
        while (col.Count > desired.Count) col.RemoveAt(col.Count - 1);
    }

    internal static void ReconcileCollection<T>(ObservableCollection<T> col, IReadOnlyList<T> desired) where T : class
    {
        // reference identity -- these view-models don't override Equals, and we want "same instance", not "equal"
        var want = new HashSet<T>(desired, System.Collections.Generic.ReferenceEqualityComparer.Instance as IEqualityComparer<T>);
        for (int i = col.Count - 1; i >= 0; i--)
            if (!want.Contains(col[i])) col.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < col.Count && ReferenceEquals(col[i], desired[i])) continue;
            int j = -1;
            for (int k = i; k < col.Count; k++)
                if (ReferenceEquals(col[k], desired[i])) { j = k; break; }
            if (j >= 0) col.Move(j, i);
            else col.Insert(i, desired[i]);
        }
        while (col.Count > desired.Count) col.RemoveAt(col.Count - 1);
    }

    /// <summary>Domain-derived library counts -- the ONLY source of dashboard numbers. Recomputed with the
    /// rows in RebuildRows so a count can never read a half-built grid.</summary>
    private Grog.Core.Sync.LibraryStats _stats = Grog.Core.Sync.LibraryStats.Empty;

    /// <summary>Byte-weighted library progress from the Core rollup module. Unlike _stats it counts in-flight
    /// bytes, so the top cards + whole-library bar climb smoothly during a sync. Recomputed in RebuildRows
    /// and on download progress.</summary>
    private Grog.Core.Sync.LibraryRollup _rollup;

    /// <summary>LocalSave of what the live engine has received per in-flight file, as an InFlightBytes
    /// lookup for the rollups. Empty (all zero) when nothing is downloading.</summary>
    private Grog.Core.Sync.Rollups.InFlightBytes CurrentInFlight()
    {
        var eng = _liveEngine;
        if (eng is null) return _ => 0;
        var byKey = new Dictionary<(long, string), long>();
        foreach (var t in eng.Snapshot)
            if (t.State is DownloadTaskState.Active or DownloadTaskState.Pending && t.BytesReceived > 0)
                byKey[(t.File.GameGogId, t.File.FileKey)] = t.BytesReceived;
        return f => byKey.TryGetValue((f.GameGogId, f.FileKey), out var b) ? b : 0;
    }

    // A byte tick must NOT re-walk the whole library. The rollup = a cached "settled" base (present-full
    // + static partials, EXCLUDING currently in-flight files) plus a per-tick sum over ONLY the in-flight
    // files (~3). The base is invalidated on any structural change (download state transition, rebuild,
    // reconcile, scope) and rebuilt on the next progress call. So a pure byte tick is O(in-flight), not O(F).
    private long _rbGDone, _rbGTotal, _rbXDone, _rbXTotal;
    private bool _rollupBaseValid;

    private System.Collections.Generic.HashSet<(long, string)> InFlightKeys()
    {
        var set = new System.Collections.Generic.HashSet<(long, string)>();
        var eng = _liveEngine;
        if (eng is not null)
            foreach (var t in eng.Snapshot)
                if (t.State is DownloadTaskState.Active or DownloadTaskState.Pending && t.BytesReceived > 0)
                    set.Add((t.File.GameGogId, t.File.FileKey));
        return set;
    }

    // Full walk (only on invalidation, not per tick): present-full + static partials, EXCLUDING in-flight
    // files (those are added live each tick). Mirrors Rollups.Library's filter exactly (IncludesLanguage;
    // games/extras split by kind) so the cached path and the full path agree to the byte.
    private void ReseedRollupBase(System.Collections.Generic.HashSet<(long, string)> inflight)
    {
        long gD = 0, gT = 0, xD = 0, xT = 0;
        var scope = EffectiveScope;
        foreach (var item in _manifest!.Current.Items)
        {
            if (!Grog.Core.Sync.LibraryStats.IsBackupEligible(item)) continue;
            foreach (var f in item.Files)
            {
                if (!scope.IncludesLanguage(f)) continue;
                long total = Grog.Core.Sync.Rollups.TotalBytesOf(f);
                long done = Grog.Core.Sync.BackupScope.IsPresent(f) ? total
                          : inflight.Contains((f.GameGogId, f.FileKey)) ? 0
                          : Grog.Core.Sync.Rollups.DoneBytesOf(f, null);
                if (f.Kind == FileKind.Extra) { xT += total; xD += done; } else { gT += total; gD += done; }
            }
        }
        _rbGDone = gD; _rbGTotal = gT; _rbXDone = xD; _rbXTotal = xT; _rollupBaseValid = true;
    }

    /// <summary>Recompute the library rollup and notify the cards + whole-library bar. On a byte tick (the
    /// hot path) this is O(in-flight) via the cached base; a full re-walk happens only when the base is
    /// invalid (after a state transition / rebuild / scope change).</summary>
    internal void RaiseLibraryProgress(System.Collections.Generic.IReadOnlyList<(GameFile File, long Live)>? inflight = null)
    {
        if (_manifest is null) return;
        if (inflight is not null && _rollupBaseValid)
        {
            long gD = _rbGDone, xD = _rbXDone;
            var scope = EffectiveScope;
            foreach (var (f, live) in inflight)
            {
                if (!scope.IncludesLanguage(f)) continue;
                long done = Grog.Core.Sync.Rollups.DoneBytesOf(f, _ => live);
                if (f.Kind == FileKind.Extra) xD += done; else gD += done;
            }
            _rollup = new Grog.Core.Sync.LibraryRollup(new(gD, _rbGTotal), new(xD, _rbXTotal));
        }
        else
        {
            _rollup = Grog.Core.Sync.Rollups.Library(_manifest.Current, EffectiveScope, CurrentInFlight());
            ReseedRollupBase(InFlightKeys());
        }
        _root.Overview.RaiseByteRollup();
    }

    // ---- The root's small surface onto the library (S3.2) ----

    /// <summary>The library-derived readouts the 1 Hz dashboard pass raises (counts, colors, scope labels).</summary>
    internal void RaiseDashboardReadouts()
    {
        OnPropertyChanged(nameof(TotalCount));
        RaiseComposition();
        OnPropertyChanged(nameof(CompleteCount));
        OnPropertyChanged(nameof(NeedsBackupCount));
        OnPropertyChanged(nameof(UpdatesCount));
        OnPropertyChanged(nameof(BackedUpColor));
        OnPropertyChanged(nameof(UpdatesColor));
        OnPropertyChanged(nameof(MissingColor));
        OnPropertyChanged(nameof(BackedUpPercent));
        OnPropertyChanged(nameof(BackedUpPercentText));
        OnPropertyChanged(nameof(BackupPolicySummary));
        OnPropertyChanged(nameof(GamesRowOpacity));
        OnPropertyChanged(nameof(ExtrasRowOpacity));
        OnPropertyChanged(nameof(ScopeLabel));
        OnPropertyChanged(nameof(PendingTaskCount));
        OnPropertyChanged(nameof(SyncBackupLabel));
        OnPropertyChanged(nameof(HasPendingWork));
        RaiseDetailWidth();
    }

    /// <summary>The detail pane's width pair, raised with the dashboard and on navigation.</summary>
    internal void RaiseDetailWidth() { OnPropertyChanged(nameof(DetailColumnWidth)); OnPropertyChanged(nameof(DetailMinWidth)); }

    /// <summary>Fresh Library: hidden ids name games that no longer exist, so they go too.</summary>
    internal void ClearHiddenIds() { _hiddenIds.Clear(); InvalidateHidden(); _settings.HiddenIds = new(); }

    /// <summary>Fresh Library: every scan-derived readout starts over.</summary>
    internal void ResetScanRecord()
    {
        _lastProductsSeen = 0; _settings.LastProductsSeen = 0;
        _lastScanPasses = null; _lastScanErrors = 0; _lastScanFailedIds = new();
        CatalogScanned = false;
    }

    /// <summary>RunBusy's finally: a canceled/errored scan threw before EndCatalog, so end it here as NOT
    /// completed and drop the preview rows (never in the manifest); the first scan's cancel gets its card.</summary>
    internal void EndAbandonedScan()
    {
        bool abandonedFirstScan = CatalogBusy && _previewActive;
        if (CatalogBusy) { EndCatalog(completed: false); _root.Overview.RaiseRunBar(); }
        if (_previewActive) { EndPreview(); RebuildRows(); }
        if (abandonedFirstScan && TotalCount == 0) ShowFirstScanCanceled = true;
    }

    /// <summary>Tray hide: free decoded cover art and the detail banner (covers re-decode lazily on show).</summary>
    internal void ReleaseArt()
    {
        foreach (var g in _allGames) g.DropCover();
        OnPropertyChanged(nameof(DetailLogoBitmap)); OnPropertyChanged(nameof(HasDetailArt));
    }

    /// <summary>Tray restore: re-arm lazy cover decoding.</summary>
    internal void ReloadArt()
    {
        foreach (var g in _allGames) g.RaiseCover();
        OnPropertyChanged(nameof(DetailLogoBitmap)); OnPropertyChanged(nameof(HasDetailArt));
    }
}

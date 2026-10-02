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

// Library scope (S3.2): language + platform narrowing, the games/extras axis, the select-everything shortcut and
// its undo, the queue prune they drive, and the library stats + byte rollup every dashboard number reads.
public sealed partial class LibraryViewModel
{
    // ---- Split language scope: games vs extras. ONE flyout, retargeted -- not two copies of the picker,
    //      so the two sides can never drift apart in behavior.
    /// <summary>Point the shared flyout at the GAMES list and reseed its checkboxes from it.</summary>
    [RelayCommand] private void EditGamesLanguages() => RetargetLanguageFlyout(false);
    /// <summary>Point the shared flyout at the EXTRAS list; extras with no pick of their own show the games
    /// selection, which is what they back up.</summary>
    [RelayCommand] private void EditExtrasLanguages() => RetargetLanguageFlyout(true);

    private void RetargetLanguageFlyout(bool extras)
    {
        if (_editingExtraLanguages == extras) return;   // the options already reflect this side
        _editingExtraLanguages = extras;
        RebuildLanguageOptions();                        // reseeds IsChecked from ActiveLanguageList
        OnPropertyChanged(nameof(LanguageFlyoutTitle));
    }

    /// <summary>Which side the open flyout is editing, so the panel cannot be read as the wrong one.</summary>
    public string LanguageFlyoutTitle => _editingExtraLanguages ? "Languages for extras" : "Languages for games";

    public string GamesLanguageLabel => $"Games · {LanguageListLabel(_languages, _gameLangCount, _gameHasNeutral)}";
    /// <summary>Extras with no pick of their own report the games value, because that is what they follow.</summary>
    public string ExtrasLanguageLabel => $"Extras · {LanguageListLabel(_extraLanguages ?? _languages, _extraLangCount, _extraHasNeutral)}";

    /// <summary>How many languages each side's files actually tag -- the denominators for the buttons.</summary>
    private int _gameLangCount, _extraLangCount;
    private bool _gameHasNeutral, _extraHasNeutral;

    /// <summary>Button-sized label: a COUNT, never the list (owner 09-11). A list grows without bound and
    /// tells the user nothing a count does not; a count has a known maximum width, so the row beside it cannot
    /// reflow as the selection changes. The +1 is the always-kept Neutral entry, which is real but not
    /// user-selectable.</summary>
    private static string LanguageListLabel(List<string> langs, int available, bool hasNeutral)
        => available <= 0 ? (langs.Count == 0 ? "All" : $"{langs.Count} languages")
         : langs.Count == 0 ? $"{available} of {available}"
         : $"{Math.Min(langs.Count + (hasNeutral ? 1 : 0), available)} of {available}";   // +1 only when that side HAS neutral files (09-13)

    [RelayCommand] private void SelectAllLanguages() { foreach (var o in LanguageOptions) o.IsChecked = true; }

    /// <summary>The one shortcut worth having: the common case is "I only want my own language".</summary>
    [RelayCommand] private void SelectEnglishOnly()
    {
        foreach (var o in LanguageOptions) if (!o.IsLocked) o.IsChecked = o.Name.Equals("English", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Compact language list for the policy summary strip ("English", "English, German", "all languages").</summary>
    public string LanguageSummaryShort
    {
        get
        {
            var picked = LanguageOptions.Where(o => o.IsChecked && !o.IsLocked).Select(o => o.Name).ToList();
            if (picked.Count == 0) return "no languages";
            if (picked.Count == LanguageOptions.Count) return "all languages";
            return string.Join(", ", picked);
        }
    }

    private void UpdateLanguageConsequence()
    {
        // State what is chosen, not what is lost: a narrow default is the normal case, not a warning.
        var picked = LanguageOptions.Where(o => o.IsChecked && !o.IsLocked).Select(o => o.Name).ToList();
        var others = LanguageOptions.Where(o => !o.IsChecked).ToList();
        var chosen = picked.Count == 0 ? "Nothing selected"
            : picked.Count == LanguageOptions.Count ? "Backing up every language in your library"
            : $"Backing up {string.Join(", ", picked)}";
        var rest = others.Count == 0 ? ""
            : $" · {others.Count} other{(others.Count == 1 ? "" : "s")} available, {ByteFormat.Size(others.Sum(o => o.Bytes))}";
        LanguageConsequence = chosen + rest + ". Files GOG doesn't tag with a language (most installers) are always kept.";
    }

    private void ApplyLanguages()
    {
        var picked = LanguageOptions.Where(o => o.IsChecked && !o.IsLocked).Select(o => o.Name).ToList();
        // All REAL choices ticked == no narrowing; store empty so "everything" stays the honest default.
        // The locked Neutral entry is not a choice and must not inflate the denominator.
        var chosen = picked.Count == LanguageOptions.Count(o => !o.IsLocked) ? new List<string>() : picked;
        using (_manifest.Gate.Enter())   // (manifest gate 09-08)
        {
            var msScope = ManifestScope();
            if (_editingExtraLanguages)
            {
                // Writing extras for the first time is what BREAKS the inheritance from games -- from here on
                // the two lists move independently.
                _extraLanguages = chosen;
                msScope.ExtraLanguages = chosen.ToList();
                msScope.ExtraLanguagesChosen = true;
            }
            else
            {
                _languages = chosen;
                msScope.Languages = _languages.ToList();
                msScope.LanguagesChosen = true;
            }
        }
        Runtime.ManifestSaves.Default.SaveInBackground(_manifest, "scope");
        OnPropertyChanged(nameof(GamesLanguageLabel)); OnPropertyChanged(nameof(ExtrasLanguageLabel));
        RebuildContentOptions();      // content is priced under the language pick, so it moved
        PruneQueuedOutOfScope();      // deselecting a language drops its already-queued files
        UpdateLanguageConsequence();
        _root.RefreshFromManifest();
        OnPropertyChanged(nameof(ScopeLabel));
    }

    /// <summary>States the backup policy on the Backups page; the controls themselves live in Settings.</summary>
    public string BackupPolicySummary
    {
        get
        {
            var layout = ScopeHasExtras
                ? _root.Settings.ExtrasLayoutValue switch
                {
                    Grog.Core.Models.ExtrasPlacement.WithGame => " · extras with each game",
                    Grog.Core.Models.ExtrasPlacement.SeparateByGame => " · extras by game",
                    _ => " · extras by type",
                }
                : "";
            return $"Backing up {EffectiveScope.Caption.ToLowerInvariant()}{layout}.";
        }
    }

    /// <summary>Scope lives in the MANIFEST -- a property of the backup, so the CLI on any OS honors the GUI's
    /// picks. A pre-scope manifest is seeded once from legacy AppSettings; after that the manifest wins.</summary>
    internal void AdoptManifestScope()
    {
        if (_manifest.Current.Scope is not { } ms)
        {
            _manifest.Mutate(m => m.Scope = new Grog.Core.Sync.ScopeSettings   // (manifest gate 09-08)
            {
                IncludeGames = _includeGames, IncludeExtras = _includeExtras,
                Languages = _languages.ToList(), LanguagesChosen = _settings.LanguagesChosen,
                ExtraLanguages = _extraLanguages?.ToList(), ExtraLanguagesChosen = _settings.ExtraLanguagesChosen,
                Platforms = _platforms.ToList(), PlatformsChosen = _settings.PlatformsChosen,
            });
            Runtime.ManifestSaves.Default.SaveInBackground(_manifest, "scope");

            return;
        }
        _includeGames = ms.IncludeGames; _includeExtras = ms.IncludeExtras;
        _languages = ms.Languages.ToList();
        _extraLanguages = ms.ExtraLanguagesChosen && ms.ExtraLanguages is { } ex ? ex.ToList() : null;
        _platforms = ms.Platforms.ToList();
        // Raise here: the header button binds before the manifest scope loads and would keep the pre-scope answer.
        // The two language BUTTON labels are not in RaiseState's set; raise them or they stay stale.
        OnPropertyChanged(nameof(GamesLanguageLabel)); OnPropertyChanged(nameof(ExtrasLanguageLabel));
    }

    /// <summary>The manifest's scope record, created on demand. Every scope WRITE goes through here followed
    /// by a manifest save -- the manifest is the owner, the VM fields are the cache.</summary>
    private Grog.Core.Sync.ScopeSettings ManifestScope()
        => _manifest.Read(m => m.Scope ??= new Grog.Core.Sync.ScopeSettings());   // (manifest gate 09-08) the create-on-demand is a graph write

    internal Grog.Core.Sync.Scope EffectiveScope => new(_includeGames, _includeExtras,
        _languages.Count > 0 ? _languages : null,
        _platforms.Count > 0 ? _platforms : null,
        ExtraLanguagesForScope);

    /// <summary>The extras language list as Core wants it: NULL means "follow games", an EMPTY list means
    /// "every language". `_extraLanguages` is null until extras are given their own pick.</summary>
    private IReadOnlyCollection<string>? ExtraLanguagesForScope
        => _extraLanguages is null ? null : (_extraLanguages.Count > 0 ? _extraLanguages : System.Array.Empty<string>());

    /// <summary>EffectiveScope with the games/extras axis overridden but language + platform kept. One source
    /// so the submenu count, the queue re-prune, and the enqueue agree on the same file set.</summary>
    internal Grog.Core.Sync.Scope ScopeFor(bool games, bool extras) => new(games, extras,
        _languages.Count > 0 ? _languages : null,
        _platforms.Count > 0 ? _platforms : null,
        ExtraLanguagesForScope);
    private List<string> _languages = new();
    /// <summary>NULL until extras get their own pick, which is what makes them inherit the games list.</summary>
    private List<string>? _extraLanguages;
    /// <summary>Which list the ONE shared, retargeted checkbox flyout is currently editing.</summary>
    private bool _editingExtraLanguages;
    /// <summary>The list the flyout reads/writes; extras with no pick of their own show the games selection.</summary>
    private List<string> ActiveLanguageList
        => _editingExtraLanguages ? (_extraLanguages ?? _languages) : _languages;
    private List<string> _platforms = new();

    // ---- PLATFORMS: scope, exactly like languages. Not selected means not fetched, not counted, and gone
    //      from both numerator and denominator so 100% stays reachable.
    public sealed partial class PlatformOption : ObservableObject
    {
        public PlatformOption(string key, string label, int fileCount, long bytes, bool isChecked)
        { Key = key; Label = label; FileCount = fileCount; Bytes = bytes; _isChecked = isChecked; }
        public string Key { get; }
        public string Label { get; }
        public int FileCount { get; }
        public long Bytes { get; }
        // No "+" prefix: the standing sub-line names the platform's weight, not a delta.
        public string CostText => $"{Grog.Core.Format.ByteFormat.Size(Bytes)} \u00b7 {FileCount:N0} files";
        [ObservableProperty] private bool _isChecked;
        public Action? Changed;
        partial void OnIsCheckedChanged(bool value) => Changed?.Invoke();
    }
    public ObservableCollection<PlatformOption> PlatformOptions { get; } = new();
    public bool HasPlatformOptions => PlatformOptions.Count > 0;

    private int _neutralFiles; private long _neutralBytes;
    /// <summary>The platform row's "always kept" figure: extras + platform-neutral files under the current
    /// games/extras/language picks. Platform checkboxes plus this line sum to the scope totals.</summary>
    public string NeutralPlatformCostText =>
        $"{Grog.Core.Format.ByteFormat.Size(_neutralBytes)} \u00b7 {_neutralFiles:N0} files";

    /// <summary>The Content row's extras figure ("Extras: size, files"), shown while extras are in scope.
    /// Same number the platform rows need to sum to the scope totals -- extras are platform-neutral.</summary>
    public string ExtrasScopeCostText => $"Extras: {NeutralPlatformCostText}";

    private void RebuildPlatformOptions()
    {
        _seedingPlatforms = true;
        try
        {
        PlatformOptions.Clear();
        if (!_servicesReady) { OnPropertyChanged(nameof(HasPlatformOptions)); return; }
        var tally = new Dictionary<string, (int files, long bytes)>(StringComparer.OrdinalIgnoreCase);
        // Each platform's cost is measured against the user's OTHER choices (language above all) so it matches
        // Overview. Platform narrowing itself is lifted (WithPlatforms(null)) so an unticked platform still
        // shows what ticking it would cost.
        var langOnly = EffectiveScope.WithPlatforms(null);
        foreach (var item in _manifest.Current.Items)
            foreach (var f in item.Files)
            {
                if (Grog.Core.Sync.Scope.PlatformOf(f) is not { } p) continue;
                if (!langOnly.IncludesLanguage(f)) continue;
                if (!Grog.Core.Sync.BackupScope.CountsTowardCompleteness(f)) continue;   // same rule as every stat
                tally.TryGetValue(p, out var t);
                tally[p] = (t.files + 1, t.bytes + (Grog.Core.Sync.Rollups.TotalBytesOf(f)));
            }
        foreach (var key in new[] { "windows", "mac", "linux" })
        {
            if (!tally.TryGetValue(key, out var t)) continue;
            var label = PlatformNames.Label(key);
            PlatformOptions.Add(new PlatformOption(key, label, t.files, t.bytes,
                _platforms.Count == 0 || _platforms.Contains(key, StringComparer.OrdinalIgnoreCase))
            { Changed = OnPlatformsChanged });
        }
        // Count the always-kept remainder: extras and shared/untagged files ignore the platform pick, and
        // this number makes the platform rows sum to the totals the rest of the app shows.
        int nFiles = 0; long nBytes = 0;
        foreach (var item in _manifest.Current.Items)
            foreach (var f in item.Files)
            {
                if (Grog.Core.Sync.Scope.PlatformOf(f) is not null) continue;
                if (!langOnly.Includes(f)) continue;
                if (!Grog.Core.Sync.BackupScope.CountsTowardCompleteness(f)) continue;   // Unavailable files stay out, same rule as every stat
                nFiles++; nBytes += Grog.Core.Sync.Rollups.TotalBytesOf(f);
            }
        _neutralFiles = nFiles; _neutralBytes = nBytes;
        OnPropertyChanged(nameof(NeutralPlatformCostText)); OnPropertyChanged(nameof(ExtrasScopeCostText));
        // HEAL an explicit every-platform list back to "no narrowing" (same rule as languages): a full list
        // would silently exclude a platform the library gains later and hide the everything-state.
        if (_platforms.Count > 0 && PlatformOptions.Count > 0
            && PlatformOptions.All(o => _platforms.Contains(o.Key, StringComparer.OrdinalIgnoreCase)))
        {
            _platforms = new List<string>();
            using (_manifest.Gate.Enter()) { var msHeal = ManifestScope(); msHeal.Platforms = new List<string>(); }   // (manifest gate 09-08)
            Runtime.ManifestSaves.Default.SaveInBackground(_manifest, "scope");
        }
        OnPropertyChanged(nameof(HasPlatformOptions)); OnPropertyChanged(nameof(PlatformsLabel));
        }
        finally { _seedingPlatforms = false; }
    }

    // Set while the list is being built: lazily realized CheckBoxes write their default back through the
    // two-way binding before the bound value lands, which would fire Changed and clear the selection.
    private bool _seedingPlatforms;

    // ---- CONTENT: the same three choices, but PRICED and single-pick (09-11). As pills they implied three
    //      independent toggles and could not say what any of them costs; the decision is "which of these three",
    //      and a first-run user cannot make it without the numbers.
    public sealed class ContentOption
    {
        public ContentOption(string key, string label, int fileCount, long bytes, bool isCurrent)
        { Key = key; Label = label; FileCount = fileCount; Bytes = bytes; IsCurrent = isCurrent; }
        public string Key { get; }
        public string Label { get; }
        public int FileCount { get; }
        public long Bytes { get; }
        public string CostText => $"{Grog.Core.Format.ByteFormat.Size(Bytes)} \u00b7 {FileCount:N0} files";
        /// <summary>The pick in force. Not two-way: the flyout row is a button that runs SetScope, so there is
        /// one write path for the choice instead of a binding that can fire while the list is being seeded.</summary>
        public bool IsCurrent { get; }
    }

    public ObservableCollection<ContentOption> ContentOptions { get; } = new();

    /// <summary>The Content button's label: the choice in force, by name. Three short fixed options, so the
    /// name fits where a count would be less informative.</summary>
    public string ContentLabel => ScopeGamesOn ? "Games only" : ScopeExtrasOn ? "Extras only" : "Games & Extras";

    /// <summary>The Platforms button's label: a count, for the reason the language buttons carry one.</summary>
    public string PlatformsLabel
    {
        get
        {
            var total = PlatformOptions.Count;
            if (total == 0) return "All";
            var picked = _platforms.Count == 0 ? total : PlatformOptions.Count(o => o.IsChecked);
            return $"{picked} of {total}";
        }
    }

    /// <summary>The scope card's headline figure: what the CURRENT picks add up to. Sits on the card's
    /// header rule the way the device cards carry their contents telemetry, so the card states its total
    /// before the user reads a single control.</summary>
    public string ScopeTotalText { get; private set; } = "";

    private void RebuildContentOptions()
    {
        ContentOptions.Clear();
        if (!_servicesReady) { OnPropertyChanged(nameof(ContentLabel)); return; }
        // Priced against the user's OTHER choices, with the content axis LIFTED -- the same rule the platform
        // rows use, so an unpicked option still shows what picking it would cost rather than zero. The walk
        // itself is Core's (ScopeTally), so the pricing rule has one definition and is pinned by tests.
        var t = Grog.Core.Sync.ScopeTally.ByContent(_manifest.Current, EffectiveScope);

        ContentOptions.Add(new ContentOption("both", "Games & Extras", t.Both.Files, t.Both.Bytes, ScopeBothOn));
        ContentOptions.Add(new ContentOption("games", "Games only", t.Games.Files, t.Games.Bytes, ScopeGamesOn));
        ContentOptions.Add(new ContentOption("extras", "Extras only", t.Extras.Files, t.Extras.Bytes, ScopeExtrasOn));

        // The total is the bucket the CURRENT pick names, taken from the same walk, so the header can never
        // disagree with the flyout row the user just read.
        var now = ScopeGamesOn ? t.Games : ScopeExtrasOn ? t.Extras : t.Both;
        ScopeTotalText = $"{Grog.Core.Format.ByteFormat.Size(now.Bytes)} \u00b7 {now.Files:N0} files";
        OnPropertyChanged(nameof(ContentLabel)); OnPropertyChanged(nameof(ScopeTotalText));
    }


    private void OnPlatformsChanged()
    {
        if (_seedingPlatforms) return;
        var picked = PlatformOptions.Where(p => p.IsChecked).Select(p => p.Key).ToList();
        // All ticked is stored as "no narrowing", matching Languages, so a later library that adds a platform
        // is not silently excluded by a stale explicit list.
        _platforms = picked.Count == PlatformOptions.Count ? new List<string>() : picked;
        using (_manifest.Gate.Enter())   // (manifest gate 09-08)
        {
            var msScope = ManifestScope();
            msScope.Platforms = _platforms.ToList();
            msScope.PlatformsChosen = true;
        }
        Runtime.ManifestSaves.Default.SaveInBackground(_manifest, "scope");
        RebuildContentOptions();      // content is priced under the platform pick, so it moved
        OnPropertyChanged(nameof(PlatformsLabel));
        PruneQueuedOutOfScope();      // deselecting a platform drops its already-queued files
        _root.RefreshFromManifest();
        OnPropertyChanged(nameof(ScopeLabel));
    }

    /// <summary>Drop queued files the current language OR platform scope excludes: the queue, not the scope,
    /// is the source of truth for future runs, so a resume would otherwise fetch a turned-off axis.</summary>
    private void PruneQueuedOutOfScope()
    {
        if (_manifest is null || _manifest.Current.Downloads.IsEmpty) return;
        // FULL scope (games/extras + language + platform), so turning OFF any axis drops its already-queued files,
        // matching what will (not) back up. Core collects and removes in one gated batch (the CLI runs the same).
        var drop = SavedScope.PruneQueuedOutOfScope(_manifest, EffectiveScope);
        if (drop.Count == 0) return;
        var keys = drop.ToHashSet();
        // A running engine must drop them too, or it keeps fetching out of scope.
        _liveEngine?.CancelRange(_liveEngine.Snapshot.Where(t => keys.Contains((t.File.GameGogId, t.File.FileKey))).ToList());
        Runtime.ManifestSaves.Default.SaveInBackground(_manifest, "scope");
        _root.RefreshStorageIfIdle();
    }

    /// <summary>Change scope from the running app (Settings): persist, re-evaluate rows + dashboard.</summary>
    public void ApplyScope(bool includeGames, bool includeExtras)
    {
        if (_includeGames == includeGames && _includeExtras == includeExtras) return;
        _includeGames = includeGames; _includeExtras = includeExtras;
        var msScope = ManifestScope();
        msScope.IncludeGames = includeGames; msScope.IncludeExtras = includeExtras;
        Runtime.ManifestSaves.Default.SaveInBackground(_manifest, "scope");
        PruneQueuedOutOfScope();  // deselecting games/extras drops that kind's already-queued files (like language/platform)
        _root.RefreshFromManifest();  // re-scopes rows + stats, then notifies
        RaiseDetail();          // re-filter the open product's file list to the new scope
        OnPropertyChanged(nameof(ScopeLabel));
        RaiseScopeButtons();
        _root.Settings.RaisePolicySummaryText();
        _root.Settings.RaisePolicyState();
        // The extras placement (shape + device) is NOT touched here (09-13). Until then, dropping extras from
        // scope wrote Routing[Extras] = primary straight into the manifest: half of the one two-part decision,
        // with no move plan, so extras already on the secondary stayed there while the manifest said primary --
        // and it reset the card, discarding anything the user had staged. A scope change decides what is
        // FETCHED; where extras live is the user's separate choice and keeps until they change it. With extras
        // out of scope the storage row is hidden (ShowExtrasLocation), so nothing stale is shown either.
    }

    /// <summary>The Content buttons' states + extras gating as ONE set (ApplyScope and the everything
    /// shortcut both raise it).</summary>
    internal void RaiseScopeButtons()
    {
        OnPropertyChanged(nameof(ScopeBothOn)); OnPropertyChanged(nameof(ScopeGamesOn)); OnPropertyChanged(nameof(ScopeExtrasOn));
        OnPropertyChanged(nameof(ScopeHasExtras));
        // The content flyout prices every option against the OTHER axes, so a content change moves the
        // numbers inside it as well as which row is current: rebuild, never just re-raise the label.
        RebuildContentOptions();
        OnPropertyChanged(nameof(ContentLabel)); OnPropertyChanged(nameof(PlatformsLabel));
    }

    /// <summary>The scope hint shown after "% backed up".</summary>
    public string ScopeLabel =>
        _includeGames || _includeExtras ? $"Backing up {EffectiveScope.Caption.ToLowerInvariant()}"
        : "Nothing selected to back up";

    // Settings scope cards (single-select, mirrors onboarding)
    public bool ScopeBothOn => _includeGames && _includeExtras;
    public bool ScopeGamesOn => _includeGames && !_includeExtras;
    public bool ScopeExtrasOn => !_includeGames && _includeExtras;
    public bool ScopeHasExtras => _includeExtras;   // gates the folder-layout section (only relevant with extras)
    [RelayCommand] private void SetScope(string? s) => ApplyScope(s != "extras", s != "games");

    /// <summary>When false, the download skips Extra files (set by the first-run "Games only" choice).</summary>
    private bool _includeExtras = true;
    private bool _includeGames = true;

    // ---- Language scope: built from the library's OWN tags; empty selection = everything ----
    public ObservableCollection<LanguageOption> LanguageOptions { get; } = new();

    /// <summary>What the grid actually shows: a filtered list, because a UniformGrid reserves a cell per
    /// bound item whether or not it is visible.</summary>
    public ObservableCollection<LanguageOption> VisibleLanguages { get; } = new();

    private void RebuildVisibleLanguages()
    {
        VisibleLanguages.Clear();
        foreach (var o in LanguageOptions)
            if (ShowOtherLanguages || o.IsPrimary) VisibleLanguages.Add(o);
    }
    public bool HasLanguageChoices => LanguageOptions.Count > 1;

    /// <summary>Surface the language choice in the read-only policy summaries only when it differs from the
    /// English-only default; the Settings language chooser stays visible regardless.</summary>
    public bool ShowLanguagePolicy
    {
        get
        {
            var picked = LanguageOptions.Where(o => o.IsChecked && !o.IsLocked).Select(o => o.Name).ToList();
            return !(picked.Count == 1 && string.Equals(picked[0], "English", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Live consequence of the current language choice -- what you're giving up, in real numbers.</summary>
    [ObservableProperty] private string _languageConsequence = "";

    /// <summary>Extra languages are hidden until asked for: most people want one, and 14 checkboxes for a
    /// decision you'll never revisit is noise.</summary>
    [ObservableProperty] private bool _showOtherLanguages;
    public string OtherLanguagesLabel => ShowOtherLanguages ? "Hide other languages" : $"Show {OtherLanguageCount} other languages";
    public bool HasOtherLanguages => OtherLanguageCount > 0;
    private int OtherLanguageCount => Math.Max(0, LanguageOptions.Count - 1);
    [RelayCommand] private void ToggleOtherLanguages()
    {
        ShowOtherLanguages = !ShowOtherLanguages;
        OnPropertyChanged(nameof(OtherLanguagesLabel));
        RaiseLanguageVisibility();
    }
    private void RaiseLanguageVisibility()
    {
        foreach (var o in LanguageOptions) o.RaiseVisible(ShowOtherLanguages);
        RebuildVisibleLanguages();
    }

    private void RebuildLanguageOptions()
    {
        LanguageOptions.Clear();
        if (_manifest is null) return;

        // Count every language the library actually tags, games and localization extras alike.
        var seen = new Dictionary<string, (int files, long bytes)>(StringComparer.OrdinalIgnoreCase);
        int neutralFiles = 0; long neutralBytes = 0;
        var gameLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extraLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool gameNeutral = false, extraNeutral = false;
        foreach (var item in _manifest.Current.Items)
            foreach (var f in item.Files)
            {
                // Mirror of the platform tally's rule: a language's cost is measured against the platform
                // narrowing, or the two Settings rows quote different worlds.
                if (!EffectiveScope.IncludesPlatform(f)) continue;
                if (!Grog.Core.Sync.BackupScope.CountsTowardCompleteness(f)) continue;   // same rule as every stat
                var lang = Grog.Core.Sync.Scope.LanguageOf(f);
                // BOTH sides' denominators are collected here (for the "3/15" button labels), before the
                // per-side filter narrows the tally to the flyout being edited.
                if (lang is not null) (f.Kind == FileKind.Extra ? extraLangs : gameLangs).Add(lang);
                else if (f.Kind == FileKind.Extra) extraNeutral = true; else gameNeutral = true;
                // The flyout quotes the SIDE it is editing, so its rows sum to that side's total.
                if (_editingExtraLanguages != (f.Kind == FileKind.Extra)) continue;
                if (lang is null) { neutralFiles++; neutralBytes += f.ExpectedSizeBytes ?? 0; continue; }
                seen.TryGetValue(lang, out var t);
                seen[lang] = (t.files + 1, t.bytes + (f.ExpectedSizeBytes ?? 0));
            }
        // Neutral counts as one slot on each side that has any (matching its locked flyout entry), so an
        // all-selected side reads "8/8" -- the total stated, not an uninformative "All".
        _gameLangCount = gameLangs.Count + (gameNeutral ? 1 : 0);
        _extraLangCount = extraLangs.Count + (extraNeutral ? 1 : 0);
        _gameHasNeutral = gameNeutral; _extraHasNeutral = extraNeutral;
        // HEAL a full-explicit list back to "no narrowing": an explicit full list would silently exclude a
        // language the library gains later. Empty is the honest form.
        if (_languages.Count > 0 && gameLangs.Count > 0 && gameLangs.All(l => _languages.Contains(l, StringComparer.OrdinalIgnoreCase)))
        {
            _languages = new List<string>();
            using (_manifest.Gate.Enter()) { var msHeal = ManifestScope(); msHeal.Languages = new List<string>(); }   // (manifest gate 09-08)
            Runtime.ManifestSaves.Default.SaveInBackground(_manifest, "scope");
            OnPropertyChanged(nameof(GamesLanguageLabel));
        }
        if (_extraLanguages is { Count: > 0 } exl && extraLangs.Count > 0 && extraLangs.All(l => exl.Contains(l, StringComparer.OrdinalIgnoreCase)))
        {
            _extraLanguages = new List<string>();
            using (_manifest.Gate.Enter()) { var msHeal = ManifestScope(); msHeal.ExtraLanguages = new List<string>(); }   // (manifest gate 09-08)
            Runtime.ManifestSaves.Default.SaveInBackground(_manifest, "scope");
            OnPropertyChanged(nameof(ExtrasLanguageLabel));
        }
        OnPropertyChanged(nameof(GamesLanguageLabel)); OnPropertyChanged(nameof(ExtrasLanguageLabel));

        // First run: default to ENGLISH explicitly -- a deliberate pick, not the system language; the scan
        // summary's selection block states it and what changing it costs.
        if (!ManifestScope().LanguagesChosen && seen.Count > 0)
        {
            var pick = seen.Keys.FirstOrDefault(k => k.Equals("English", StringComparison.OrdinalIgnoreCase));
            if (pick is not null)
            {
                _languages = new List<string> { pick };
                using (_manifest.Gate.Enter())   // (manifest gate 09-08)
                {
                    var msScope = ManifestScope();
                    msScope.Languages = _languages.ToList();
                    msScope.LanguagesChosen = true;
                }
                Runtime.ManifestSaves.Default.SaveInBackground(_manifest, "scope");

                // Raise the button labels or they keep saying "All" while every tally is already narrowed.
                OnPropertyChanged(nameof(GamesLanguageLabel)); OnPropertyChanged(nameof(ExtrasLanguageLabel));
            }
        }

        // The primary language sorts first; the rest are alphabetical behind the "show others" toggle.
        var primary = _languages.FirstOrDefault() ?? "English";
        var ordered = seen.OrderByDescending(k => k.Key.Equals(primary, StringComparison.OrdinalIgnoreCase))
                          .ThenBy(k => k.Key, StringComparer.CurrentCultureIgnoreCase);
        bool first = true;
        foreach (var kv in ordered)
        {
            LanguageOptions.Add(new LanguageOption(kv.Key, kv.Value.files, kv.Value.bytes,
                ActiveLanguageList.Count == 0 || ActiveLanguageList.Contains(kv.Key, StringComparer.OrdinalIgnoreCase),
                isPrimary: first)
            { Changed = ApplyLanguages });
            first = false;
        }
        // The always-kept remainder as a LOCKED entry (checked, disabled) so the flyout's entries sum to
        // the side's total.
        if (neutralFiles > 0)
            LanguageOptions.Add(new LanguageOption("Neutral", neutralFiles, neutralBytes,
                isChecked: true, isPrimary: true, isLocked: true));

        RebuildVisibleLanguages();
        OnPropertyChanged(nameof(HasLanguageChoices)); OnPropertyChanged(nameof(HasOtherLanguages));
        OnPropertyChanged(nameof(ShowLanguagePolicy));
        OnPropertyChanged(nameof(TypeLegend));
        OnPropertyChanged(nameof(LanguageSummaryShort)); _root.Settings.RaisePolicySummaryText();
        OnPropertyChanged(nameof(OtherLanguagesLabel));
        RaiseLanguageVisibility();
        UpdateLanguageConsequence();
    }

    public double GamesRowOpacity => EffectiveScope.Includes(FileKind.Installer) ? 1.0 : 0.22;
    public double ExtrasRowOpacity => EffectiveScope.Includes(FileKind.Extra) ? 1.0 : 0.22;

    /// <summary>Domain-derived library counts, for the Overview readouts and the storage VM's system-drive hint.
    /// The health tally (Overview) recomputes it on its own pass, so the setter is part of the surface.</summary>
    internal Grog.Core.Sync.LibraryStats Stats { get => _stats; set => _stats = value; }
    /// <summary>The byte-weighted rollup behind the percent bars.</summary>
    internal Grog.Core.Sync.LibraryRollup Rollup => _rollup;
    internal bool IncludeGames => _includeGames;
    internal bool IncludeExtras => _includeExtras;
    /// <summary>A download state transition changed the in-flight / present split: the next progress tick rebuilds the cached base once.</summary>
    internal void InvalidateRollupBase() => _rollupBaseValid = false;
}

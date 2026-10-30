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
    [RelayCommand] private void EditGamesLanguages() => RetargetLanguageFlyout(false);
    [RelayCommand] private void EditExtrasLanguages() => RetargetLanguageFlyout(true);

    private void RetargetLanguageFlyout(bool extras)
    {
        if (_editingExtraLanguages == extras) return;   // the options already reflect this side
        _editingExtraLanguages = extras;
        RebuildLanguageOptions();                        // reseeds IsChecked from ActiveLanguageList
        OnPropertyChanged(nameof(LanguageFlyoutTitle));
    }

    public string LanguageFlyoutTitle => _editingExtraLanguages ? "Languages for extras" : "Languages for games";

    public string GamesLanguageLabel => $"Games · {LanguageListLabel(_languages, _gameLangCount, _gameHasNeutral)}";
    public string ExtrasLanguageLabel => $"Extras · {LanguageListLabel(_extraLanguages ?? _languages, _extraLangCount, _extraHasNeutral)}";

    private int _gameLangCount, _extraLangCount;
    private bool _gameHasNeutral, _extraHasNeutral;

    private static string LanguageListLabel(List<string> langs, int available, bool hasNeutral)
        => available <= 0 ? (langs.Count == 0 ? "All" : $"{langs.Count} languages")
         : langs.Count == 0 ? $"{available} of {available}"
         : $"{Math.Min(langs.Count + (hasNeutral ? 1 : 0), available)} of {available}";   // +1 only when that side HAS neutral files

    [RelayCommand] private void SelectAllLanguages() { foreach (var o in LanguageOptions) o.IsChecked = true; }

    [RelayCommand] private void SelectEnglishOnly()
    {
        foreach (var o in LanguageOptions) if (!o.IsLocked) o.IsChecked = o.Name.Equals("English", StringComparison.OrdinalIgnoreCase);
    }

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
        using (_manifest.Gate.Enter())   // 
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

    internal void AdoptManifestScope()
    {
        if (_manifest.Current.Scope is not { } ms)
        {
            _manifest.Mutate(m => m.Scope = new Grog.Core.Sync.ScopeSettings   // 
            {
                IncludeGames = _includeGames, IncludeExtras = _includeExtras,
                Languages = _languages.ToList(), LanguagesChosen = _settings.LanguagesChosen,
                ExtraLanguages = _extraLanguages?.ToList(), ExtraLanguagesChosen = _settings.ExtraLanguagesChosen,
                Platforms = _platforms.ToList(), PlatformsChosen = _settings.PlatformsChosen,
            });
            Runtime.ManifestSaves.Default.SaveInBackground(_manifest, "scope");

            return;
        }
        _includeGames = ms.IncludeGames; _includeExtras = ms.IncludeExtras; _includePatches = ms.IncludePatches;
        _languages = ms.Languages.ToList();
        _extraLanguages = ms.ExtraLanguagesChosen && ms.ExtraLanguages is { } ex ? ex.ToList() : null;
        _platforms = ms.Platforms.ToList();
        // Raise here: the header button binds before the manifest scope loads and would keep the pre-scope answer.
        // The two language BUTTON labels are not in RaiseState's set; raise them or they stay stale.
        OnPropertyChanged(nameof(GamesLanguageLabel)); OnPropertyChanged(nameof(ExtrasLanguageLabel));
        RefreshConditionalChips();   // the chips were seeded before the saved scope was known
    }

    private Grog.Core.Sync.ScopeSettings ManifestScope()
        => _manifest.Read(m => m.Scope ??= new Grog.Core.Sync.ScopeSettings());   // the create-on-demand is a graph write

    internal Grog.Core.Sync.Scope EffectiveScope => new(_includeGames, _includeExtras,
        _languages.Count > 0 ? _languages : null,
        _platforms.Count > 0 ? _platforms : null,
        ExtraLanguagesForScope,
        _includePatches);

    private IReadOnlyCollection<string>? ExtraLanguagesForScope
        => _extraLanguages is null ? null : (_extraLanguages.Count > 0 ? _extraLanguages : System.Array.Empty<string>());

    internal Grog.Core.Sync.Scope ScopeFor(bool games, bool extras) => new(games, extras,
        _languages.Count > 0 ? _languages : null,
        _platforms.Count > 0 ? _platforms : null,
        ExtraLanguagesForScope,
        _includePatches);
    private List<string> _languages = new();
    private List<string>? _extraLanguages;
    private bool _editingExtraLanguages;
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

    // Before the first scan nothing is known, so the Platforms and Languages pills show greyed with a reason
    // instead of vanishing (a first-run look at Settings read as "Content is all there is"). After a scan the
    // pills hide only when there is nothing to choose.
    public bool LibraryUnscanned => _allGames.Count == 0;
    public bool ShowPlatformsPill => HasPlatformOptions || LibraryUnscanned;
    public bool ShowLanguagesPill => HasLanguageChoices || LibraryUnscanned;
    public const string UnscannedPillText = "After first scan";

    private int _neutralFiles; private long _neutralBytes;
    public string NeutralPlatformCostText =>
        $"{Grog.Core.Format.ByteFormat.Size(_neutralBytes)} \u00b7 {_neutralFiles:N0} files";

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
            using (_manifest.Gate.Enter()) { var msHeal = ManifestScope(); msHeal.Platforms = new List<string>(); }   // 
            Runtime.ManifestSaves.Default.SaveInBackground(_manifest, "scope");
        }
        OnPropertyChanged(nameof(HasPlatformOptions)); OnPropertyChanged(nameof(PlatformsLabel));
        OnPropertyChanged(nameof(LibraryUnscanned)); OnPropertyChanged(nameof(ShowPlatformsPill)); OnPropertyChanged(nameof(ShowLanguagesPill));
        }
        finally { _seedingPlatforms = false; }
    }

    // Set while the list is being built: lazily realized CheckBoxes write their default back through the
    // two-way binding before the bound value lands, which would fire Changed and clear the selection.
    private bool _seedingPlatforms;

    // ---- CONTENT: the same three choices, but PRICED and single-pick. As pills they implied three
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
        public bool IsCurrent { get; }
    }

    public ObservableCollection<ContentOption> ContentOptions { get; } = new();

    // Patches are a sub-choice of Games: priced against the other axes like the content rows, toggled in place.
    public bool IncludePatches
    {
        get => _includePatches;
        set { if (_includePatches != value) ApplyPatches(value); }
    }
    public string PatchesCostText { get; private set; } = "";
    public bool ShowPatchesToggle => _includeGames;

    private void ApplyPatches(bool include)
    {
        if (_manifest is null) { OnPropertyChanged(nameof(IncludePatches)); return; }   // snap the box back
        _includePatches = include;
        var msScope = ManifestScope();
        msScope.IncludePatches = include;
        Runtime.ManifestSaves.Default.SaveInBackground(_manifest, "scope");
        PruneQueuedOutOfScope();
        _root.RefreshFromManifest();
        RaiseDetail();
        RaiseScopeButtons();
        _root.Settings.RaisePolicySummaryText();
        _root.Settings.RaisePolicyState();
        OnPropertyChanged(nameof(IncludePatches));
    }

    public string ContentLabel => ScopeGamesOn ? "Games only" : ScopeExtrasOn ? "Extras only" : "Games & Extras";

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
        PatchesCostText = $"{Grog.Core.Format.ByteFormat.Size(t.Patches.Bytes)} \u00b7 {t.Patches.Files:N0} files";
        OnPropertyChanged(nameof(PatchesCostText)); OnPropertyChanged(nameof(ShowPatchesToggle));

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
        using (_manifest.Gate.Enter())   // 
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
        // The extras placement (shape + device) is NOT touched here. Until then, dropping extras from
        // scope wrote Routing[Extras] = primary straight into the manifest: half of the one two-part decision,
        // with no move plan, so extras already on the secondary stayed there while the manifest said primary --
        // and it reset the card, discarding anything the user had staged. A scope change decides what is
        // FETCHED; where extras live is the user's separate choice and keeps until they change it. With extras
        // out of scope the storage row is hidden (ShowExtrasLocation), so nothing stale is shown either.
    }

    internal void RaiseScopeButtons()
    {
        OnPropertyChanged(nameof(ScopeBothOn)); OnPropertyChanged(nameof(ScopeGamesOn)); OnPropertyChanged(nameof(ScopeExtrasOn));
        OnPropertyChanged(nameof(ScopeHasExtras));
        // The content flyout prices every option against the OTHER axes, so a content change moves the
        // numbers inside it as well as which row is current: rebuild, never just re-raise the label.
        RebuildContentOptions();
        OnPropertyChanged(nameof(ContentLabel)); OnPropertyChanged(nameof(PlatformsLabel));
        RefreshConditionalChips();   // a kind that left the scope takes its chips with it
    }

    public string ScopeLabel =>
        _includeGames || _includeExtras ? $"Backing up {EffectiveScope.Caption.ToLowerInvariant()}"
        : "Nothing selected to back up";

    // Settings scope cards (single-select, mirrors onboarding)
    public bool ScopeBothOn => _includeGames && _includeExtras;
    public bool ScopeGamesOn => _includeGames && !_includeExtras;
    public bool ScopeExtrasOn => !_includeGames && _includeExtras;
    public bool ScopeHasExtras => _includeExtras;   // gates the folder-layout section (only relevant with extras)
    [RelayCommand] private void SetScope(string? s) => ApplyScope(s != "extras", s != "games");

    private bool _includeExtras = true;
    private bool _includeGames = true;
    private bool _includePatches = true;

    // ---- Language scope: built from the library's OWN tags; empty selection = everything ----
    public ObservableCollection<LanguageOption> LanguageOptions { get; } = new();

    public ObservableCollection<LanguageOption> VisibleLanguages { get; } = new();

    private void RebuildVisibleLanguages()
    {
        VisibleLanguages.Clear();
        foreach (var o in LanguageOptions)
            if (ShowOtherLanguages || o.IsPrimary) VisibleLanguages.Add(o);
    }
    public bool HasLanguageChoices => LanguageOptions.Count > 1;

    public bool ShowLanguagePolicy
    {
        get
        {
            var picked = LanguageOptions.Where(o => o.IsChecked && !o.IsLocked).Select(o => o.Name).ToList();
            return !(picked.Count == 1 && string.Equals(picked[0], "English", StringComparison.OrdinalIgnoreCase));
        }
    }

    [ObservableProperty] private string _languageConsequence = "";

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
            using (_manifest.Gate.Enter()) { var msHeal = ManifestScope(); msHeal.Languages = new List<string>(); }   // 
            Runtime.ManifestSaves.Default.SaveInBackground(_manifest, "scope");
            OnPropertyChanged(nameof(GamesLanguageLabel));
        }
        if (_extraLanguages is { Count: > 0 } exl && extraLangs.Count > 0 && extraLangs.All(l => exl.Contains(l, StringComparer.OrdinalIgnoreCase)))
        {
            _extraLanguages = new List<string>();
            using (_manifest.Gate.Enter()) { var msHeal = ManifestScope(); msHeal.ExtraLanguages = new List<string>(); }   // 
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
                using (_manifest.Gate.Enter())   // 
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
        OnPropertyChanged(nameof(LibraryUnscanned)); OnPropertyChanged(nameof(ShowPlatformsPill)); OnPropertyChanged(nameof(ShowLanguagesPill));
        OnPropertyChanged(nameof(ShowLanguagePolicy));
        OnPropertyChanged(nameof(TypeLegend));
        OnPropertyChanged(nameof(LanguageSummaryShort)); _root.Settings.RaisePolicySummaryText();
        OnPropertyChanged(nameof(OtherLanguagesLabel));
        RaiseLanguageVisibility();
        UpdateLanguageConsequence();
    }

    public double GamesRowOpacity => EffectiveScope.Includes(FileKind.Installer) ? 1.0 : 0.22;
    public double ExtrasRowOpacity => EffectiveScope.Includes(FileKind.Extra) ? 1.0 : 0.22;

    internal Grog.Core.Sync.LibraryStats Stats { get => _stats; set => _stats = value; }
    internal Grog.Core.Sync.LibraryRollup Rollup => _rollup;
    internal bool IncludeGames => _includeGames;
    internal bool IncludeExtras => _includeExtras;
    internal void InvalidateRollupBase() => _rollupBaseValid = false;
}

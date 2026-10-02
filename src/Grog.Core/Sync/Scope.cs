using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Models;

namespace Grog.Core.Sync;

/// <summary>
/// What the user chose to back up: games and/or extras, narrowed by language and platform. Cloud saves
/// are never in backup scope. Languages null or empty means "every language" (never silently drop
/// content); an untagged file is language-neutral and is ALWAYS kept, or filtering would break games.
/// </summary>
public readonly record struct Scope(bool Games, bool Extras, IReadOnlyCollection<string>? Languages = null,
                                   IReadOnlyCollection<string>? Platforms = null,
                                   IReadOnlyCollection<string>? ExtraLanguages = null)
{
    // Language scope is split games vs extras. ExtraLanguages null means "SAME AS GAMES", NOT "all" --
    // extras inherit the games pick unless given their own; widening is a hand-made opt-in. An EMPTY
    // collection still means "every language", matching the Languages contract.
    public static readonly Scope Both = new(true, true);
    public static readonly Scope GamesOnly = new(true, false);
    public static readonly Scope ExtrasOnly = new(false, true);
    public static readonly Scope Nothing = new(false, false);

    /// <summary>True when no language narrowing is in force for GAMES.</summary>
    public bool AllLanguages => Languages is null || Languages.Count == 0;
    /// <summary>True when no language narrowing is in force for EXTRAS (inheriting games when unset).</summary>
    public bool AllExtraLanguages => AllLanguagesFor(FileKind.Extra);
    /// <summary>True when no platform narrowing is in force.</summary>
    public bool AllPlatforms => Platforms is null || Platforms.Count == 0;

    /// <summary>The language list that governs a KIND. Extras use their own list; null inherits games.</summary>
    public IReadOnlyCollection<string>? LanguagesFor(FileKind kind)
        => kind == FileKind.Extra ? (ExtraLanguages ?? Languages) : Languages;

    /// <summary>True when the list governing this kind imposes no narrowing.</summary>
    public bool AllLanguagesFor(FileKind kind)
    {
        var set = LanguagesFor(kind);
        return set is null || set.Count == 0;
    }

    public Scope WithLanguages(IReadOnlyCollection<string>? languages) => this with { Languages = languages };
    public Scope WithExtraLanguages(IReadOnlyCollection<string>? languages) => this with { ExtraLanguages = languages };
    public Scope WithPlatforms(IReadOnlyCollection<string>? platforms) => this with { Platforms = platforms };

    /// <summary>The platform a file is FOR ("windows"/"mac"/"linux"), or null when it is platform-neutral.
    /// Extras carry no OS tag and are neutral: a soundtrack is not a Windows soundtrack.</summary>
    public static string? PlatformOf(GameFile f)
    {
        if (f.Kind == FileKind.Extra) return null;
        var os = f.Os?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(os) ? null : os switch
        {
            "osx" => "mac",
            _ => os,
        };
    }

    /// <summary>Platform check only, ignoring kind. Same contract as the language test: an untagged file is
    /// neutral and is ALWAYS kept, so narrowing can never drop a shared payload.</summary>
    public bool IncludesPlatform(GameFile f)
    {
        if (AllPlatforms) return true;
        var p = PlatformOf(f);
        return p is null || Platforms!.Contains(p, StringComparer.OrdinalIgnoreCase);
    }

    public bool Includes(FileKind kind)
        => kind == FileKind.Extra ? Extras : Games;   // Installer, Patch, LanguagePack, DlcInstaller

    /// <summary>Kind + language. The language of a file is GOG's tag when it has one, otherwise whatever
    /// its name reveals (localization extras). No language known -> neutral -> always included.</summary>
    public bool Includes(GameFile f)
    {
        if (!Includes(f.Kind)) return false;
        if (!IncludesPlatform(f)) return false;
        return LanguageAllowed(f);
    }

    /// <summary>The language test, picking the list BY KIND so an extra is judged against the extras list.
    /// One definition; never re-derive this at a call site.</summary>
    private bool LanguageAllowed(GameFile f)
    {
        if (AllLanguagesFor(f.Kind)) return true;
        var lang = LanguageOf(f);
        return lang is null || LanguagesFor(f.Kind)!.Contains(lang, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Language check only, ignoring kind. The Games/Extras cards each count their own kind but
    /// must still drop languages the user excluded, or the totals include work that will never happen.</summary>
    public bool IncludesLanguage(GameFile f)
    {
        // Platform narrowing rides along here too: every caller that uses this to build a denominator must
        // exclude out-of-scope platforms as well, or the totals include work that will never happen.
        if (!IncludesPlatform(f)) return false;
        return LanguageAllowed(f);
    }

    /// <summary>The language a file is FOR, or null when it's language-neutral.</summary>
    public static string? LanguageOf(GameFile f)
    {
        if (!string.IsNullOrWhiteSpace(f.Language)) return ExtraClassifier.CanonicalLanguage(f.Language);
        return f.Kind == FileKind.Extra ? ExtraClassifier.LanguageOf(f.Name) : null;
    }

    /// <summary>Parse the persisted scope key ("both" | "games" | "extras").</summary>
    public static Scope FromKey(string? key) => key switch
    {
        "games" => GamesOnly,
        "extras" => ExtrasOnly,
        _ => Both,
    };

    public string Key => (Games, Extras) switch
    {
        (true, false) => "games",
        (false, true) => "extras",
        _ => "both",
    };

    public string Label => (Games, Extras) switch
    {
        (true, true) => "Games & extras",
        (true, false) => "Games only",
        (false, true) => "Extras only",
        _ => "Nothing selected",
    };

    /// <summary>Caption for the dashboard: scope plus the language narrowing, if any. Long lists collapse
    /// to a count rather than becoming a wall of names.</summary>
    public string Caption
    {
        get
        {
            if (AllLanguages) return Label;
            var langs = Languages!.ToList();
            var tail = langs.Count <= 3 ? string.Join(", ", langs) : $"{langs.Count} languages";
            return $"{Label} · {tail}";
        }
    }
}

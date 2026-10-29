using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Models;

namespace Grog.Core.Sync;

public readonly record struct Scope(bool Games, bool Extras, IReadOnlyCollection<string>? Languages = null,
                                   IReadOnlyCollection<string>? Platforms = null,
                                   IReadOnlyCollection<string>? ExtraLanguages = null,
                                   bool IncludePatches = true)
{
    // Language scope is split games vs extras
    public static readonly Scope Both = new(true, true);
    public static readonly Scope GamesOnly = new(true, false);
    public static readonly Scope ExtrasOnly = new(false, true);
    public static readonly Scope Nothing = new(false, false);

    public bool AllLanguages => Languages is null || Languages.Count == 0;
    public bool AllExtraLanguages => AllLanguagesFor(FileKind.Extra);
    public bool AllPlatforms => Platforms is null || Platforms.Count == 0;

    public IReadOnlyCollection<string>? LanguagesFor(FileKind kind)
        => kind == FileKind.Extra ? (ExtraLanguages ?? Languages) : Languages;

    public bool AllLanguagesFor(FileKind kind)
    {
        var set = LanguagesFor(kind);
        return set is null || set.Count == 0;
    }

    public Scope WithLanguages(IReadOnlyCollection<string>? languages) => this with { Languages = languages };
    public Scope WithExtraLanguages(IReadOnlyCollection<string>? languages) => this with { ExtraLanguages = languages };
    public Scope WithPlatforms(IReadOnlyCollection<string>? platforms) => this with { Platforms = platforms };
    public Scope WithPatches(bool include) => this with { IncludePatches = include };

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

    public bool IncludesPlatform(GameFile f)
    {
        if (AllPlatforms) return true;
        var p = PlatformOf(f);
        return p is null || Platforms!.Contains(p, StringComparer.OrdinalIgnoreCase);
    }

    public bool Includes(FileKind kind)
        => kind == FileKind.Extra ? Extras
         : kind == FileKind.Patch ? Games && IncludePatches
         : Games;   // Installer, LanguagePack, DlcInstaller

    public bool Includes(GameFile f)
    {
        if (!Includes(f.Kind)) return false;
        if (!IncludesPlatform(f)) return false;
        return LanguageAllowed(f);
    }

    private bool LanguageAllowed(GameFile f)
    {
        if (AllLanguagesFor(f.Kind)) return true;
        var lang = LanguageOf(f);
        return lang is null || LanguagesFor(f.Kind)!.Contains(lang, StringComparer.OrdinalIgnoreCase);
    }

    public bool IncludesLanguage(GameFile f)
    {
        // Platform narrowing here too
        if (!IncludesPlatform(f)) return false;
        return LanguageAllowed(f);
    }

    public static string? LanguageOf(GameFile f)
    {
        if (!string.IsNullOrWhiteSpace(f.Language)) return ExtraClassifier.CanonicalLanguage(f.Language);
        return f.Kind == FileKind.Extra ? ExtraClassifier.LanguageOf(f.Name) : null;
    }

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

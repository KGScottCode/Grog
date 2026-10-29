// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Grog.Core.Sync;

public static class ExtraClassifier
{
    public const string Localizations = "Localizations";
    public const string Soundtracks = "Soundtracks";
    public const string AudioOther = "Audio (other)";
    public const string AlternateVersions = "Alternate versions";
    public const string Video = "Video";
    public const string Art = "Art";
    public const string GuidesAndDocs = "Manuals, Guides & Docs";
    public const string LevelEditors = "Level editors";
    public const string AddOns = "Add-ons";

    public static readonly IReadOnlyList<string> Buckets = new[]
    {
        Soundtracks, Art, GuidesAndDocs, Video, Localizations, AlternateVersions, LevelEditors, AudioOther, AddOns,
    };

    // First match wins, so narrow rules precede broad ones; Localizations runs first and includes English
    // so "(EN)" classifies as a localization. AlternateVersions is reserved for language-less builds.
    private static readonly (string Bucket, Regex Rule)[] Rules =
    {
        (Localizations, Rx(@"localizat|localis|english|russian|polish|german|french|spanish|italian|czech|hungarian|portuguese|japanese|korean|chinese|\((EN|ENG|PL|FR|IT|DE|ES|RU|CZ|JP|KO|ZH|PT|HU|GER)\)")),
        (AlternateVersions, Rx(@"alternate version|original version|re-?installer|premium modules")),
        (Soundtracks, Rx(@"soundtrack|\bost\b|music|score\b|tracks\b|medley|main theme")),
        (AudioOther, Rx(@"ringtone|sms tone|\btones?\b")),
        (LevelEditors, Rx(@"level editor|\beditor\b|toolset|\bsdk\b|mod tools")),
        (Video, Rx(@"video|making of|behind the scenes|interview|commentary|trailer|animation")),
        (Art, Rx(@"artbook|art book|concept art|artwork|wallpaper|wallaper|sketch|comic|\bskin\b|calendar|screensaver|avatar|photo|album")),
        (GuidesAndDocs, Rx(@"manual|reference card|cluebook|clue book|guide|walkthrough|hint book|strategy|spellbook|lorebook|lore book|technology tree|secret answers|achievement|\bmaps?\b|novella|\bbook\b|story\b|memoirs|chronicles|newspaper|handout|design document|examination|playing cards|\bforms?\b|reference")),
    };

    // GOG's own type is the safety net for anything the keywords miss.
    private static readonly Dictionary<string, string> GogTypeFallback = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio"] = AudioOther,
        ["video"] = Video,
        ["artworks"] = Art,
        ["wallpapers"] = Art,
        ["avatars"] = Art,
        ["manuals"] = GuidesAndDocs,
        ["guides & reference"] = GuidesAndDocs,
        ["game add-ons"] = AddOns,
    };

    private static Regex Rx(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // THE type word for any file, as the chips, legend, Storage grid, queue and move rows all show it:
    // an extra's bucket, "Patches" for a patch, "Game" for everything else that installs.
    public static string TypeLabelOf(Models.GameFile f) => f.Kind switch
    {
        Models.FileKind.Extra => Classify(f.Name, f.ExtraType),
        Models.FileKind.Patch => "Patches",
        _ => "Game",
    };

    public static string ShortLabel(string bucket) => bucket switch
    {
        AlternateVersions => "Alt Versions",
        GuidesAndDocs     => "Manuals",
        Video             => "Videos",
        AddOns or LevelEditors or AudioOther => "Other",
        _                 => bucket,
    };

    public static string Classify(string? name, string? gogType)
    {
        // GOG names can carry trailing spaces that defeat word-boundary rules; trim before matching.
        var n = (name ?? "").Trim();
        if (n.Length > 0)
            foreach (var (bucket, rule) in Rules)
                if (rule.IsMatch(n)) return bucket;

        var t = (gogType ?? "").Trim();
        return GogTypeFallback.TryGetValue(t, out var fallback) ? fallback : AddOns;
    }

    // GOG tags no language on extras, so a localized extra is recognized only by its name.
    private static readonly (string Language, Regex Rule)[] LanguageRules =
    {
        ("English",    Rx(@"\benglish\b|\(EN\)|\(ENG")),
        ("Russian",    Rx(@"\brussian\b|\(RU\)")),
        ("Polish",     Rx(@"\bpolish\b|\bpolski\b|\(PL\)")),
        ("German",     Rx(@"\bgerman\b|\bdeutsch\b|\(DE\)|\(GER\)")),
        ("French",     Rx(@"\bfrench\b|\bfrancais\b|\(FR\)")),
        ("Spanish",    Rx(@"\bspanish\b|\bespanol\b|\(ES\)")),
        ("Italian",    Rx(@"\bitalian\b|\(IT\)")),
        ("Czech",      Rx(@"\bczech\b|\(CZ\)")),
        ("Hungarian",  Rx(@"\bhungarian\b|\(HU\)")),
        ("Portuguese", Rx(@"\bportuguese\b|\(PT\)")),
        ("Japanese",   Rx(@"\bjapanese\b|\(JP\)")),
        ("Korean",     Rx(@"\bkorean\b|\(KO\)")),
        ("Chinese",    Rx(@"\bchinese\b|\(ZH\)")),
    };

    // GOG returns the same language under several spellings on different products; fold them so a
    // language appears once and selecting it takes all of its files.
    private static readonly Dictionary<string, string> LanguageAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deutsch"] = "German",      ["allemand"] = "German",
        ["francais"] = "French",     ["français"] = "French",
        ["polski"] = "Polish",
        ["русский"] = "Russian",     ["russkij"] = "Russian",
        ["espanol"] = "Spanish",     ["español"] = "Spanish",
        ["italiano"] = "Italian",
        ["português"] = "Portuguese",["portugues"] = "Portuguese",
        ["nederlands"] = "Dutch",
        ["svenska"] = "Swedish",
        ["dansk"] = "Danish",
        ["český"] = "Czech",         ["cesky"] = "Czech",
        ["türkçe"] = "Turkish",      ["turkce"] = "Turkish",
        ["日本語"] = "Japanese",
        ["中文"] = "Chinese",         ["中文(简体)"] = "Chinese",
        ["한국어"] = "Korean",
        ["magyar"] = "Hungarian",
    };

    public static string CanonicalLanguage(string raw)
    {
        var r = (raw ?? "").Trim();
        if (r.Length == 0) return r;
        if (LanguageAliases.TryGetValue(r, out var canon)) return canon;
        // Some aliases carry a bracketed variant suffix: try the part before any bracket too.
        var cut = r.Split('(', '[')[0].Trim();
        if (cut.Length > 0 && LanguageAliases.TryGetValue(cut, out canon)) return canon;
        return char.ToUpperInvariant(r[0]) + r[1..];
    }

    public static string? LanguageOf(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return null;
        foreach (var (lang, rule) in LanguageRules)
            if (rule.IsMatch(n)) return lang;
        return null;
    }

    public static string FolderFor(string bucket) => bucket switch
    {
        GuidesAndDocs => "Manuals, Guides and Docs",
        AudioOther => "Audio (other)",
        _ => bucket,
    };
}

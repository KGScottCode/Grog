// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.IO;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

/// <summary>Cases are taken from a real 334-extra library, so GOG's actual quirks stay covered.</summary>
[NewBatch]
[Trait("classifier")]
public class ExtraClassifierTests
{
    static string C(string name, string gogType = "") => ExtraClassifier.Classify(name, gogType);

    [Test] void Soundtracks_BeatGenericAudio()
    {
        Assert.Equal(ExtraClassifier.Soundtracks, C("soundtrack (FLAC)", "audio"), "soundtrack");
        Assert.Equal(ExtraClassifier.Soundtracks, C("music inspired by The Witcher (FLAC)", "audio"), "music");
        Assert.Equal(ExtraClassifier.Soundtracks, C("Syberia tracks", "audio"), "tracks");
        Assert.Equal(ExtraClassifier.Soundtracks, C("main theme", "audio"), "theme");
        Assert.Equal(ExtraClassifier.AudioOther, C("Duke ringtones", "audio"), "ringtones are not a soundtrack");
        Assert.Equal(ExtraClassifier.AudioOther, C("SMS tones", "audio"), "tones");
    }

    [Test] void Localizations_WinOverEverythingElse()
    {
        Assert.Equal(ExtraClassifier.Localizations, C("Russian localization (Fargus)", "game add-ons"), "named language");
        Assert.Equal(ExtraClassifier.Localizations, C("Linux Russian localization (Fargus)", "game add-ons"), "os + language");
        Assert.Equal(ExtraClassifier.Localizations, C("Alternate Version (PL)", "game add-ons"), "parenthesised language code");
        Assert.Equal(ExtraClassifier.Localizations, C("Artbook (GER)", "artworks"), "a localized artbook is a language decision first");
    }

    [Test] void AlternateVersions_AreGameBuilds()
    {
        Assert.Equal(ExtraClassifier.AlternateVersions, C("Alternate Version", "game add-ons"), "alternate build");
        Assert.Equal(ExtraClassifier.AlternateVersions, C("Postal 2 Complete original version", "game add-ons"), "original version");
        Assert.Equal(ExtraClassifier.AlternateVersions, C("premium modules re-installer", "game add-ons"), "re-installer");
    }

    [Test] void Art_AbsorbsTheThinBuckets()
    {
        Assert.Equal(ExtraClassifier.Art, C("artbook", "artworks"), "artbook");
        Assert.Equal(ExtraClassifier.Art, C("HD wallapers (Postal series)", "wallpapers"), "GOG's own typo still lands via keyword");
        Assert.Equal(ExtraClassifier.Art, C("design sketches", "artworks"), "sketches");
        Assert.Equal(ExtraClassifier.Art, C("The Witcher calendar", "game add-ons"), "calendar");
        Assert.Equal(ExtraClassifier.Art, C("Winamp Diggles skin", "artworks"), "skin");
        Assert.Equal(ExtraClassifier.Art, C("avatars", "avatars"), "avatars");
    }

    [Test] void GuidesAndDocs_FixesGogsInconsistentFiling()
    {
        // GOG files identical content under different types; the classifier must not care.
        Assert.Equal(ExtraClassifier.GuidesAndDocs, C("cluebook", "game add-ons"), "cluebook filed as add-on");
        Assert.Equal(ExtraClassifier.GuidesAndDocs, C("cluebook", "guides & reference"), "cluebook filed as guide");
        Assert.Equal(ExtraClassifier.GuidesAndDocs, C("manual", "manuals"), "manual");
        Assert.Equal(ExtraClassifier.GuidesAndDocs, C("Lorebook", "manuals"), "lorebook");
        Assert.Equal(ExtraClassifier.GuidesAndDocs, C("technology tree", "guides & reference"), "tech tree");
    }

    [Test] void TrailingWhitespace_DoesNotDefeatMatching()
    {
        // A real case: GOG ships "Venice " with a trailing space, which silently breaks word-boundary rules.
        Assert.Equal(ExtraClassifier.Video, C("Venice ", "video"), "trailing space still classifies via fallback");
        Assert.Equal(ExtraClassifier.Soundtracks, C("  soundtrack  ", "audio"), "padded name still matches keyword");
    }

    [Test] void GogTypeFallback_CoversWhatKeywordsMiss()
    {
        Assert.Equal(ExtraClassifier.Video, C("Venice ", "video"), "falls back to GOG video");
        Assert.Equal(ExtraClassifier.AddOns, C("Venba", "game add-ons"), "unknown name -> GOG type");
        Assert.Equal(ExtraClassifier.Art, C("something we've never seen", "wallpapers"), "fallback maps GOG type onto our bucket");
    }

    [Test] void NeverUnclassified()
    {
        Assert.Equal(ExtraClassifier.AddOns, C("", ""), "empty input still gets a bucket");
        // null! on purpose: passing null IS the case under test - the classifier must degrade rather than
        // throw when GOG omits both fields. The bang tells the compiler we mean it.
        Assert.Equal(ExtraClassifier.AddOns, C(null!, null!), "nulls still get a bucket");
        Assert.Equal(ExtraClassifier.AddOns, C("mystery", "a type GOG invents next year"), "unknown type degrades, never throws");
    }

    [Test] void FolderNames_AreFilesystemBoring()
    {
        Assert.Equal("Manuals, Guides and Docs", ExtraClassifier.FolderFor(ExtraClassifier.GuidesAndDocs), "ampersand normalized");
        Assert.Equal("Soundtracks", ExtraClassifier.FolderFor(ExtraClassifier.Soundtracks), "plain bucket unchanged");
    }
}

[NewBatch]
[Trait("classifier")]
public class TypeGroupedLayoutTests
{
    static (JsonManifestStore store, string root) New()
    {
        var baseDir = Directory.CreateTempSubdirectory("grog-layout-").FullName;
        var root = Path.Combine(baseDir, "primary");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(baseDir, "_cfg"));
        var store = new JsonManifestStore(GrogPaths.Resolve(root));
        store.LoadAsync().GetAwaiter().GetResult();
        return (store, root);
    }

    static GameFile Extra(string name, string type) =>
        new() { Kind = FileKind.Extra, Name = name, ExtraType = type };

    [Test] void Grouped_SortsExtrasByBucket_NotByGame()
    {
        var (store, root) = New();
        try
        {
            store.Current.SetExtrasLayout(ExtrasPlacement.SeparateByType);   // grouped
            var layout = new BackupLayout(store.Current, root);

            var st = layout.ResolveTargetDir(1, "witcher", FileKind.Extra, out _, Extra("soundtrack (FLAC)", "audio"));
            Assert.True(st!.EndsWith(Path.Combine("Extras", "Soundtracks", "witcher")), "soundtrack lands in Soundtracks");

            // A different game's soundtrack lands in the SAME bucket -- that's the whole point.
            var st2 = layout.ResolveTargetDir(2, "syberia", FileKind.Extra, out _, Extra("soundtrack (MP3)", "audio"));
            Assert.True(st2!.EndsWith(Path.Combine("Extras", "Soundtracks", "syberia")), "second game's soundtrack, same bucket");

            var man = layout.ResolveTargetDir(1, "witcher", FileKind.Extra, out _, Extra("game guide", "manuals"));
            Assert.True(man!.EndsWith(Path.Combine("Extras", "Manuals, Guides and Docs", "witcher")), "guide lands in docs");
        }
        finally { Directory.Delete(Path.GetFullPath(Path.Combine(root, "..")), true); }
    }

    [Test] void GameFirst_GroupsByGameThenBucket()
    {
        var (store, root) = New();
        try
        {
            store.Current.SetExtrasLayout(ExtrasPlacement.SeparateByGame);   // game-first
            var layout = new BackupLayout(store.Current, root);
            var st = layout.ResolveTargetDir(1, "witcher", FileKind.Extra, out _, Extra("soundtrack (FLAC)", "audio"));
            Assert.True(st!.EndsWith(Path.Combine("Extras", "witcher", "Soundtracks")), "game-first: Extras/<slug>/<bucket>");
        }
        finally { Directory.Delete(Path.GetFullPath(Path.Combine(root, "..")), true); }
    }

    [Test] void Games_AreUnaffectedByLayoutChoice()
    {
        var (store, root) = New();
        try
        {
            store.Current.SetExtrasLayout(ExtrasPlacement.SeparateByType);
            var layout = new BackupLayout(store.Current, root);
            var g = layout.ResolveTargetDir(1, "witcher", FileKind.Installer, out _,
                new GameFile { Kind = FileKind.Installer, Name = "setup.exe" });
            Assert.True(g!.EndsWith(Path.Combine("Games", "witcher")), "installers always live under Games/<slug>");
        }
        finally { Directory.Delete(Path.GetFullPath(Path.Combine(root, "..")), true); }
    }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Collections.Generic;
using System.Linq;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

[NewBatch]
[Trait("language")]
public class LanguageScopeTests
{
    static GameFile Installer(string language) =>
        new() { Kind = FileKind.Installer, Name = "setup.exe", Language = language };

    static GameFile Extra(string name) =>
        new() { Kind = FileKind.Extra, Name = name };

    [Test] void NoLanguagesChosen_MeansEverything()
    {
        var s = Scope.Both;
        Assert.True(s.AllLanguages, "default scope narrows nothing");
        Assert.True(s.Includes(Installer("Polish")), "polish kept when no filter set");
        Assert.True(s.Includes(Installer("")), "neutral kept");
    }

    [Test] void ChosenLanguage_KeepsMatching_DropsOthers()
    {
        var s = Scope.Both.WithLanguages(new[] { "English" });
        Assert.True(s.Includes(Installer("English")), "english kept");
        Assert.True(!s.Includes(Installer("Polish")), "polish dropped");
        Assert.True(!s.Includes(Installer("czech")), "case-insensitive drop");
    }

    [Test] void LanguageNeutralFiles_AreAlwaysKept()
    {
        // The single most important rule: most installers and every shared .bin carry no language, so
        // filtering them out would break the game.
        var s = Scope.Both.WithLanguages(new[] { "English" });
        Assert.True(s.Includes(Installer("")), "untagged installer survives an english-only filter");
        Assert.True(s.Includes(Extra("soundtrack (FLAC)")), "a neutral extra survives");
        Assert.True(s.Includes(Extra("artbook")), "art has no language");
    }

    [Test] void LocalizationExtras_AreFilteredByName()
    {
        // GOG tags NO language on extras, so the only signal is the name.
        var s = Scope.Both.WithLanguages(new[] { "English" });
        Assert.True(!s.Includes(Extra("Russian localization (Fargus)")), "russian localization dropped");
        Assert.True(!s.Includes(Extra("Alternate Version (PL)")), "polish alternate build dropped");
        Assert.True(s.Includes(Extra("Alternate Version (EN)")), "english alternate build kept");
    }

    [Test] void MultipleLanguages_KeepAllChosen()
    {
        var s = Scope.Both.WithLanguages(new[] { "English", "Polish" });
        Assert.True(s.Includes(Installer("English")), "english kept");
        Assert.True(s.Includes(Installer("Polish")), "polish kept");
        Assert.True(!s.Includes(Installer("German")), "german dropped");
    }

    [Test] void LanguageFilter_RespectsKindFilterFirst()
    {
        var s = Scope.GamesOnly.WithLanguages(new[] { "English" });
        Assert.True(!s.Includes(Extra("soundtrack")), "extras excluded by kind regardless of language");
        Assert.True(s.Includes(Installer("English")), "game still kept");
    }

    [Test] void Caption_StatesTheNarrowing()
    {
        Assert.Equal("Games & extras", Scope.Both.Caption, "no narrowing: scope only");
        Assert.Equal("Games & extras · English", Scope.Both.WithLanguages(new[] { "English" }).Caption, "one language");
        Assert.Equal("Games & extras · English, Polish", Scope.Both.WithLanguages(new[] { "English", "Polish" }).Caption, "two languages");
        Assert.Equal("Games & extras · 5 languages",
            Scope.Both.WithLanguages(new[] { "English", "Polish", "German", "French", "Czech" }).Caption,
            "long lists collapse to a count rather than a wall of names");
    }

    [Test] void DuplicateSpellings_FoldToOneLanguage()
    {
        // GOG really does return "Deutsch" (61 GB) and "German" (1 GB) as separate entries. Ticking
        // German must take both, or 61 GB silently stays behind.
        Assert.Equal("German", Scope.LanguageOf(Installer("Deutsch")), "native name folds to English");
        Assert.Equal("German", Scope.LanguageOf(Installer("German")), "english name unchanged");
        Assert.Equal("French", Scope.LanguageOf(Installer("français")), "accented native name");
        Assert.Equal("Russian", Scope.LanguageOf(Installer("русский")), "cyrillic");
        Assert.Equal("Chinese", Scope.LanguageOf(Installer("中文(简体)")), "bracketed variant");
        Assert.Equal("Czech", Scope.LanguageOf(Installer("český")), "diacritics");

        var s = Scope.Both.WithLanguages(new[] { "German" });
        Assert.True(s.Includes(Installer("Deutsch")), "selecting German takes the Deutsch files too");
    }

    [Test] void LanguageOf_ReadsTagFirst_ThenName()
    {
        Assert.Equal("English", Scope.LanguageOf(Installer("English")), "gog's tag wins");
        Assert.Equal("Russian", Scope.LanguageOf(Extra("Russian localization (Fargus)")), "extra falls back to its name");
        Assert.True(Scope.LanguageOf(Extra("soundtrack")) is null, "neutral extra has no language");
        Assert.True(Scope.LanguageOf(Installer("")) is null, "untagged installer is neutral");
    }

    [Test] void CompletenessFraction_UsesTheScopedDenominator()
    {
        // the owner's THIEF: Definitive Edition, exactly as it sits in the manifest: 7 English installer parts,
        // an English comic, a neutral soundtrack, and a FRENCH comic GOG tags with no language at all.
        // Under an English scope the French comic can never be fetched, so any "x of y" that counts it
        // reads as a permanent unexplained shortfall. The Folders group header re-derived its own
        // denominator from raw Item.Files and showed "9 of 10" forever.
        var files = new List<GameFile>();
        for (int i = 1; i <= 7; i++)
            files.Add(new GameFile { Kind = FileKind.Installer, Name = $"THIEF: Definitive Edition (Part {i} of 7)", Language = "English", Os = "windows" });
        files.Add(Extra("soundtrack (MP3)"));
        files.Add(Extra("Thief: Tales From The City digital comic (EN)"));
        files.Add(Extra("Thief: Tales From The City digital comic (FR)"));

        var scope = Scope.Both.WithLanguages(new[] { "English" }).WithPlatforms(new[] { "windows" });
        Assert.Equal(9, files.Count(f => scope.Includes(f)), "the French comic is out of an English scope");
        Assert.True(!scope.Includes(files[^1]), "language read from the (FR) suffix, since GOG tags extras with nothing");
        Assert.True(scope.Includes(files[^2]), "the (EN) comic stays in");
        Assert.True(scope.Includes(files[^3]), "a neutral soundtrack is never language-filtered");
    }

    // ---- Split language scope: games vs extras --------------------------------------------------------

    [Test] void ExtraLanguages_Unset_InheritsGames()
    {
        // THE MIGRATION RULE. Every Scope built before the split leaves ExtraLanguages null, and null must
        // mean "same as games" -- NOT "all". Widening extras to All would silently queue ~8 GB on the next
        // scheduled run, which is the one outcome this design exists to prevent.
        var s = Scope.Both.WithLanguages(new[] { "English" });
        Assert.True(s.ExtraLanguages is null, "unset by construction");
        Assert.True(!s.AllExtraLanguages, "extras are narrowed, because they follow the games list");
        Assert.True(s.Includes(Extra("comic (EN)")), "english extra kept");
        Assert.True(!s.Includes(Extra("comic (FR)")), "french extra dropped, inheriting the games pick");
    }

    [Test] void ExtraLanguages_WidenIndependently_WithoutTouchingGames()
    {
        // the owner's actual reason for the split: keep installers to one language, take every collectible.
        var s = Scope.Both.WithLanguages(new[] { "English" })
                          .WithExtraLanguages(System.Array.Empty<string>());   // empty == every language
        Assert.True(s.AllExtraLanguages, "extras narrowed by nothing");
        Assert.True(s.Includes(Extra("comic (FR)")), "french extra now kept");
        Assert.True(s.Includes(Extra("comic (EN)")), "english extra still kept");
        Assert.True(!s.Includes(Installer("French")), "GAMES are untouched by the extras list");
        Assert.True(s.Includes(Installer("English")), "english installer still kept");
    }

    [Test] void ExtraLanguages_NarrowIndependently_WithoutTouchingGames()
    {
        // The other direction: take every installer language, but only English collectibles.
        var s = Scope.Both.WithExtraLanguages(new[] { "English" });
        Assert.True(s.AllLanguages, "games narrowed by nothing");
        Assert.True(s.Includes(Installer("Polish")), "any installer language kept");
        Assert.True(!s.Includes(Extra("comic (FR)")), "french extra dropped by the extras list");
        Assert.True(s.Includes(Extra("comic (EN)")), "english extra kept");
    }

    [Test] void SplitLanguages_NeutralFilesStillAlwaysKept()
    {
        // The split must not weaken the most important rule in the file.
        var s = Scope.Both.WithLanguages(new[] { "English" }).WithExtraLanguages(new[] { "German" });
        Assert.True(s.Includes(Installer("")), "neutral installer kept");
        Assert.True(s.Includes(Extra("soundtrack (MP3)")), "neutral extra kept under a german extras list");
    }

    [Test] void IncludesLanguage_PicksTheListByKind()
    {
        // IncludesLanguage builds the DENOMINATORS (details pane, Folders fraction). If it read the games
        // list for an extra, the fraction and the queue would disagree again -- the d52/d54 bug shape.
        var s = Scope.Both.WithLanguages(new[] { "English" })
                          .WithExtraLanguages(new[] { "French" });
        Assert.True(!s.IncludesLanguage(Extra("comic (EN)")), "english extra excluded by the EXTRAS list");
        Assert.True(s.IncludesLanguage(Extra("comic (FR)")), "french extra included by the EXTRAS list");
        Assert.True(s.IncludesLanguage(Installer("English")), "installer judged by the GAMES list");
        Assert.True(!s.IncludesLanguage(Installer("French")), "french installer excluded by the GAMES list");
    }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

/// <summary>
/// The manifest-resident backup scope (step 4 of the architecture plan). Two things under test:
/// ScopeSettings.ToScope() speaks the ONE shared predicate vocabulary with exactly the GUI's semantics
/// (empty = everything, extras inherit games until deliberately split), and the record survives a manifest
/// round-trip - it is the contract that lets a Linux cron `grog backup` honor a Windows GUI's narrowing.
/// </summary>
[NewBatch]
[Trait("scope")]
public class ScopeSettingsTests
{
    [Test] void DefaultsImposeNoNarrowing()
    {
        var s = new ScopeSettings().ToScope();
        Assert.True(s.Games); Assert.True(s.Extras);
        Assert.True(s.AllLanguages); Assert.True(s.AllExtraLanguages); Assert.True(s.AllPlatforms);
    }

    [Test] void EmptyListsMeanEverything()
    {
        // The Languages/Platforms contract: empty = every language/platform, expressed as null narrowing so
        // Scope's own AllX tests hold. Nothing is ever dropped by accident.
        var s = new ScopeSettings { Languages = new(), Platforms = new() }.ToScope();
        Assert.Null(s.Languages); Assert.Null(s.Platforms);
    }

    [Test] void PopulatedListsNarrow()
    {
        var s = new ScopeSettings
        {
            IncludeExtras = false,
            Languages = { "English" },
            Platforms = { "linux" },
        }.ToScope();
        Assert.False(s.Extras);
        Assert.False(s.AllLanguages); Assert.False(s.AllPlatforms);
    }

    [Test] void ExtrasInheritGamesLanguagesUntilChosen()
    {
        // An ExtraLanguages list that was never DELIBERATELY chosen must not take effect: null means "same
        // as games", and only the explicit split breaks the inheritance. Mirrors the GUI's load rule.
        var notChosen = new ScopeSettings { Languages = { "English" }, ExtraLanguages = new() { "German" } }.ToScope();
        Assert.Null(notChosen.ExtraLanguages);
        Assert.Equal("English", System.Linq.Enumerable.Single(notChosen.LanguagesFor(FileKind.Extra)!));

        var chosen = new ScopeSettings
        {
            Languages = { "English" },
            ExtraLanguages = new() { "German" },
            ExtraLanguagesChosen = true,
        }.ToScope();
        Assert.Equal("German", System.Linq.Enumerable.Single(chosen.LanguagesFor(FileKind.Extra)!));
    }

    [Test] async Task ScopeRoundTripsThroughTheManifest()
    {
        var dir = Directory.CreateTempSubdirectory("grog-tests-").FullName;
        try
        {
            var store = new JsonManifestStore(dir);
            store.Current.Scope = new ScopeSettings
            {
                IncludeGames = true, IncludeExtras = false,
                Languages = { "English", "French" }, LanguagesChosen = true,
                ExtraLanguages = new() { "English" }, ExtraLanguagesChosen = true,
                Platforms = { "windows" }, PlatformsChosen = true,
            };
            await store.SaveAsync();

            var reloaded = new JsonManifestStore(dir);
            await reloaded.LoadAsync();
            var s = reloaded.Current.Scope;
            Assert.True(s is not null);
            Assert.False(s!.IncludeExtras);
            Assert.Equal(2, s.Languages.Count);
            Assert.True(s.LanguagesChosen);
            Assert.Equal("English", System.Linq.Enumerable.Single(s.ExtraLanguages!));
            Assert.True(s.ExtraLanguagesChosen);
            Assert.Equal("windows", System.Linq.Enumerable.Single(s.Platforms));
            Assert.True(s.PlatformsChosen);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test] async Task UnseededScopeStaysNull()
    {
        // Null is a SIGNAL (never seeded), so a save/load cycle must not invent a default record - the GUI
        // decides when to seed, and the CLI treats null as "no narrowing".
        var dir = Directory.CreateTempSubdirectory("grog-tests-").FullName;
        try
        {
            var store = new JsonManifestStore(dir);
            await store.SaveAsync();
            var reloaded = new JsonManifestStore(dir);
            await reloaded.LoadAsync();
            Assert.Null(reloaded.Current.Scope);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

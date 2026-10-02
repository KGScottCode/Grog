// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Collections.Generic;
using System.Linq;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

public class ProductTaxonomyTests
{
    static OwnedProduct Product(long id, string slug, string url, bool isMovie = false)
        => new(id, slug, slug, "Cat", IsGame: !isMovie, IsMovie: isMovie, DlcCount: 0, Url: url);

    static readonly HashSet<long> NoMods = new();

    [Test]
    void ModTagged_ClassifiesAsMod_EvenThoughGogTypesItAsGame()
    {
        var keeper = Product(1188533399, "keeperfx", "/en/game/keeperfx");
        var mods = new HashSet<long> { 1188533399 };
        Assert.Equal(ProductType.Mod, ProductTaxonomy.Classify(keeper, mods, hasDownloads: true, isReferencedAsParent: false), "mod-tagged");
    }

    [Test]
    void NormalGameWithDownloads_IsGame()
    {
        var g = Product(1, "age_of_wonders", "/en/game/age_of_wonders");
        Assert.Equal(ProductType.Game, ProductTaxonomy.Classify(g, NoMods, hasDownloads: true, isReferencedAsParent: false), "plain game");
    }

    [Test]
    void MovieFlag_IsMovie()
    {
        var m = Product(2, "some_movie", "/en/movie/some_movie", isMovie: true);
        Assert.Equal(ProductType.Movie, ProductTaxonomy.Classify(m, NoMods, hasDownloads: false, isReferencedAsParent: false), "movie");
    }

    [Test]
    void NoDownloads_ReferencedByChildren_IsPack()
    {
        var pack = Product(3, "alone_in_the_dark_the_trilogy_123", "/en/game/alone_in_the_dark_the_trilogy_123");
        Assert.Equal(ProductType.Pack, ProductTaxonomy.Classify(pack, NoMods, hasDownloads: false, isReferencedAsParent: true), "pack with children");
    }

    [Test]
    void NoDownloads_ChildPointingElsewhere_IsDlc()
    {
        // Its own slug differs from the parent slug in its url -> it's a child (DLC-like).
        var child = new OwnedProduct(4, "This War of Mine The Little Ones", "the_little_ones",
            "Cat", IsGame: true, IsMovie: false, DlcCount: 0, Url: "/en/game/this_war_of_mine");
        Assert.Equal(ProductType.Dlc, ProductTaxonomy.Classify(child, NoMods, hasDownloads: false, isReferencedAsParent: false), "child dlc");
    }
}

[Trait("integration")]
public class ModGroundTruthTests
{
    static bool HasStoredToken => System.IO.File.Exists(FileTokenStore.DefaultPath());
    const string NoToken = "No stored GOG token - run the CLI 'login' profile once first.";

    // The user confirmed (via GOG's "IN LIBRARY" mod tags) they own exactly these 4 mods.
    static readonly (long id, string name)[] ExpectedMods =
    {
        (1308742792, "DevilutionX"),
        (2040766783, "DOOM 3: Phobos"),
        (1158113613, "Diablo 1 HD Mod (Belzebub)"),
        (1188533399, "KeeperFX"),
    };

    [Test]
    async Task OwnedMods_MatchTheFourGroundTruthMods()
    {
        Assert.SkipUnless(HasStoredToken, NoToken);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Grog/0.1");
        var auth = new GogAuthService(http, new FileTokenStore(), new FailFast());
        var api = new GogApiClient(http, auth);

        var library = await api.GetLibraryProductsAsync();
        var modIds = await api.GetModTaggedProductIdsAsync();

        // Owned products that are mod-tagged.
        var ownedMods = library.Where(p => modIds.Contains(p.Id)).ToList();

        // If the mod-tag endpoint is reachable, we expect exactly the 4 known mods among owned.
        if (modIds.Count == 0)
        {
            Assert.Skip("Mod-tag catalog returned nothing (endpoint may have moved); skipping ground-truth check.");
        }

        foreach (var (id, name) in ExpectedMods)
            Assert.True(ownedMods.Any(p => p.Id == id),
                $"expected owned mod not detected: {name} ({id})");

        Assert.Equal(ExpectedMods.Length, ownedMods.Count, "exactly the four known mods");
    }

    sealed class FailFast : IInteractiveLoginProvider
    {
        public Task<string> AcquireAuthorizationCodeAsync(Uri loginUrl, CancellationToken ct = default)
            => throw new InvalidOperationException("Interactive login not available in tests.");
    }
}

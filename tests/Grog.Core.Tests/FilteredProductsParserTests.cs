// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.IO;
using System.Linq;
using Grog.Core.Api;
using Grog.Core.Tests.Framework;

public class FilteredProductsParserTests
{
    static string Fixture()
    {
        // Fixture lives next to the test assembly (copied via csproj). Fall back to source path.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "getFilteredProducts_page1.json"),
            Path.Combine("tests", "Grog.Core.Tests", "Fixtures", "getFilteredProducts_page1.json"),
        };
        foreach (var c in candidates) if (File.Exists(c)) return File.ReadAllText(c);
        throw new FileNotFoundException("getFilteredProducts fixture not found in: " + string.Join(", ", candidates));
    }

    [Test]
    void Parse_ReadsPagingMetadata()
    {
        var page = FilteredProductsParser.Parse(Fixture());
        Assert.Equal(1, page.Page, "page");
        Assert.Equal(2, page.TotalPages, "total pages");
        Assert.Equal(125, page.TotalProducts, "total products (your game count)");
    }

    [Test]
    void Parse_ReadsCoreProductFields()
    {
        var page = FilteredProductsParser.Parse(Fixture());
        var aow = Assert.Single(page.Products.Where(p => p.Title == "Age of Wonders"), "AoW present");
        Assert.Equal(1207658883, aow.Id, "id");
        Assert.Equal("age_of_wonders", aow.Slug, "slug");
        Assert.Equal("Strategy", aow.Category, "category");
        Assert.True(aow.IsGame, "is game");
        Assert.False(aow.IsMovie, "not movie");
    }

    [Test]
    void ChildOfPack_DetectedViaUrlSlug()
    {
        var page = FilteredProductsParser.Parse(Fixture());
        // The two Alone in the Dark entries both point at the trilogy pack slug.
        var aitd = page.Products.Where(p => p.Slug.StartsWith("alone_in_the_dark")).ToList();
        Assert.Equal(2, aitd.Count, "two AitD child games");
        Assert.All(aitd, p => Assert.True(p.IsChildOfPack, "each is a child of a pack"), "children");
        Assert.All(aitd, p => Assert.Equal("alone_in_the_dark_the_trilogy_123", p.UrlSlug, "point at trilogy pack"), "parent slug");
    }

    [Test]
    void StandaloneGame_IsNotChildOfPack()
    {
        var page = FilteredProductsParser.Parse(Fixture());
        // KeeperFX: url slug == own slug -> standalone (it's a mod, but a standalone game product).
        var keeper = Assert.Single(page.Products.Where(p => p.Slug == "keeperfx"), "KeeperFX present");
        Assert.False(keeper.IsChildOfPack, "standalone, not a pack child");
        Assert.Equal("keeperfx", keeper.UrlSlug, "url slug is its own");
    }

    [Test]
    void EmptyUrl_YieldsNoParentAndNoCrash()
    {
        var page = FilteredProductsParser.Parse(Fixture());
        var bg2 = Assert.Single(page.Products.Where(p => p.Slug == "baldurs_gate_2_complete"), "BG2 present");
        Assert.Equal("", bg2.UrlSlug, "empty url -> empty slug");
        Assert.False(bg2.IsChildOfPack, "empty url is not a child");
    }

    [Test]
    void PackChildrenMapToParent_EnablesCoverageCheck()
    {
        // The whole point: given the packs' child games, we can list which owned games belong
        // to a given pack slug -- authoritative coverage, no title-stem guessing.
        var page = FilteredProductsParser.Parse(Fixture());
        var childrenOfTrilogy = page.Products
            .Where(p => p.IsChildOfPack && p.UrlSlug == "alone_in_the_dark_the_trilogy_123")
            .Select(p => p.Title)
            .ToList();
        Assert.Equal(2, childrenOfTrilogy.Count, "trilogy has 2 owned children in fixture");
        Assert.Contains("Alone in the Dark 1", string.Join("|", childrenOfTrilogy), "AitD1 among children");
    }
}

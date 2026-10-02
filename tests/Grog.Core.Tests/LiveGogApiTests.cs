// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
//
// Live-API integration tests, [Trait("integration")]. They use the token stored by a
// previous `login` (Grog.Cli "login" launch profile). Without a stored token they SKIP
// (never fail), so CI and fresh clones stay green. Run only these: `dotnet run -- integration`.
namespace Grog.Core.Tests;

using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Tests.Framework;

[Trait("integration")]
public class LiveGogApiTests
{
    static bool HasStoredToken => File.Exists(FileTokenStore.DefaultPath());
    const string NoToken = "No stored GOG token - run the CLI 'login' profile once first.";

    static GogApiClient CreateClient(HttpClient http)
    {
        var auth = new GogAuthService(http, new FileTokenStore(), new FailFastLoginProvider());
        return new GogApiClient(http, auth);
    }

    [Test]
    async Task WhoAmI_ReturnsSignedInUser()
    {
        Assert.SkipUnless(HasStoredToken, NoToken);

        using var http = new HttpClient();
        var user = await CreateClient(http).GetUserInfoAsync();

        Assert.False(string.IsNullOrWhiteSpace(user.Username), "username non-empty");
        Assert.False(string.IsNullOrWhiteSpace(user.UserId), "user id non-empty");
    }

    [Test]
    async Task OwnedProducts_ReturnsNonEmptyLibrary()
    {
        Assert.SkipUnless(HasStoredToken, NoToken);

        using var http = new HttpClient();
        var owned = await CreateClient(http).GetOwnedProductIdsAsync();

        Assert.NotEmpty(owned, "library has products");
        Assert.All(owned, id => Assert.True(id > 0, "product id positive"), "all ids valid");
    }

    [Test]
    async Task GameDetails_ForFirstOwnedProduct_ParsesTitle()
    {
        Assert.SkipUnless(HasStoredToken, NoToken);

        using var http = new HttpClient();
        var client = CreateClient(http);
        var owned = await client.GetOwnedProductIdsAsync();

        // Walk until we find a product GOG returns details for (some 404).
        foreach (var id in owned.Take(10))
        {
            var details = await client.GetGameDetailsAsync(id);
            if (details is null) continue;
            Assert.False(string.IsNullOrWhiteSpace(details.Title), "details have a title");
            return;
        }
        Assert.Skip("None of the first 10 owned products returned details (unexpected).");
    }

    [Test]
    async Task LibraryProducts_ReturnRichRowsWithParentLinks()
    {
        Assert.SkipUnless(HasStoredToken, NoToken);

        using var http = new HttpClient();
        var products = await CreateClient(http).GetLibraryProductsAsync();

        Assert.NotEmpty(products, "library products returned");
        Assert.All(products, p => Assert.False(string.IsNullOrWhiteSpace(p.Title), "each has a title"), "titles");
        Assert.All(products, p => Assert.False(string.IsNullOrWhiteSpace(p.Slug), "each has a slug"), "slugs");
        Assert.True(products.Count >= 100, "paging fetched beyond the first page");
    }

    /// <summary>Tests must never pop a browser; reaching interactive login means something is wrong.</summary>
    sealed class FailFastLoginProvider : IInteractiveLoginProvider
    {
        public Task<string> AcquireAuthorizationCodeAsync(Uri loginUrl, CancellationToken ct = default)
            => throw new InvalidOperationException(
                "Interactive login required but not available in tests. " +
                "Run the CLI 'login' launch profile once, then re-run.");
    }
}

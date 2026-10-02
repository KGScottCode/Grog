// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.Core.Auth;
using Grog.Core.Tests.Framework;

public class GogOAuthTests
{
    [Test]
    void ExtractCode_FindsCode()
    {
        (string pasted, string expected)[] cases =
        {
            ("abc123DEF", "abc123DEF"), // bare code
            ("https://embed.gog.com/on_login_success?origin=client&code=XyZ789", "XyZ789"),
            ("https://embed.gog.com/on_login_success?code=first&other=second", "first"),
            ("  https://embed.gog.com/on_login_success?origin=client&code=trimmed  ", "trimmed"),
        };
        foreach (var (pasted, expected) in cases)
            Assert.Equal(expected, GogOAuth.ExtractCode(pasted), $"ExtractCode(\"{pasted.Trim()}\")");
    }

    [Test]
    void ExtractCode_ReturnsNullWhenAbsent()
    {
        string[] cases = { "", "   ", "https://embed.gog.com/on_login_success?origin=client" };
        foreach (var pasted in cases)
            Assert.Null(GogOAuth.ExtractCode(pasted), $"ExtractCode(\"{pasted}\") should be null");
    }

    [Test]
    void AuthorizeUrl_ContainsClientIdAndEncodedRedirect()
    {
        Assert.Contains(GogOAuth.ClientId, GogOAuth.AuthorizeUrl, "client id present");
        Assert.Contains("on_login_success", GogOAuth.AuthorizeUrl, "redirect present");
        Assert.Contains("response_type=code", GogOAuth.AuthorizeUrl, "code flow requested");
    }
}

public class AuthSessionExpiryTests
{
    static AuthSession Session(DateTimeOffset expiresAt) => new("access", "refresh", expiresAt, "user");

    [Test]
    void FreshToken_IsNotExpired()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(GogAuthService.IsExpired(Session(now.AddHours(1)), now), "1h remaining");
    }

    [Test]
    void PastExpiry_IsExpired()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(GogAuthService.IsExpired(Session(now.AddMinutes(-1)), now), "expired 1m ago");
    }

    [Test]
    void WithinSkewWindow_CountsAsExpired()
    {
        var now = DateTimeOffset.UtcNow;
        // 1 minute remaining < 2 minute skew => treat as expired, refresh early
        Assert.True(GogAuthService.IsExpired(Session(now.AddMinutes(1)), now), "inside skew window");
    }
}

public class FileTokenStoreTests
{
    string _dir = "";
    string StorePath => Path.Combine(_dir, "tokens.json");

    [Setup] void Setup() => _dir = Directory.CreateTempSubdirectory("grog-auth-tests-").FullName;
    [Teardown] void Teardown() => Directory.Delete(_dir, recursive: true);

    [Test]
    async Task Load_WhenMissing_ReturnsNull()
        => Assert.Null(await new FileTokenStore(StorePath).LoadAsync(), "missing file -> null session");

    [Test]
    async Task SaveLoadClear_RoundTrips()
    {
        var store = new FileTokenStore(StorePath);
        var session = new AuthSession("at", "rt", DateTimeOffset.UtcNow.AddHours(1), "42");

        await store.SaveAsync(session);
        var loaded = await store.LoadAsync();
        Assert.NotNull(loaded, "loads after save");
        Assert.Equal("at", loaded!.AccessToken, "access token");
        Assert.Equal("42", loaded.UserId, "user id");

        await store.ClearAsync();
        Assert.Null(await store.LoadAsync(), "null after clear");
        Assert.False(File.Exists(StorePath), "file deleted on clear");
    }

    [Test]
    async Task Load_WithCorruptFile_ReturnsNullInsteadOfThrowing()
    {
        await File.WriteAllTextAsync(StorePath, "{ not json !!");
        Assert.Null(await new FileTokenStore(StorePath).LoadAsync(), "corrupt store == logged out");
    }
}

// Sweep 2 #23: a refresh that failed for ANY reason was read as "the token expired".
public class RefreshFailureTests
{
    sealed class Store : ITokenStore
    {
        public AuthSession? S = new("at", "rt", DateTimeOffset.UtcNow.AddMinutes(-5), "u");
        public bool SaveFails;
        public Task<AuthSession?> LoadAsync() => Task.FromResult(S);
        public Task SaveAsync(AuthSession session) { if (SaveFails) throw new System.IO.IOException("disk full"); S = session; return Task.CompletedTask; }
        public Task ClearAsync() { S = null; return Task.CompletedTask; }
    }
    sealed class Login : IInteractiveLoginProvider
    {
        public int Asked;
        public Task<string> AcquireAuthorizationCodeAsync(Uri loginUrl, CancellationToken ct = default) { Asked++; return Task.FromResult("code"); }
    }
    sealed class TokenEndpoint : System.Net.Http.HttpMessageHandler
    {
        public System.Net.HttpStatusCode? RefreshAnswers;   // null = the network is down
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage req, CancellationToken ct)
        {
            bool refresh = req.RequestUri!.Query.Contains("refresh_token");
            if (refresh && RefreshAnswers is null) throw new System.Net.Http.HttpRequestException("No such host is known.");
            if (refresh && RefreshAnswers != System.Net.HttpStatusCode.OK)
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(RefreshAnswers!.Value) { Content = new System.Net.Http.StringContent("{}") });
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.StringContent("{\"access_token\":\"new\",\"refresh_token\":\"rt2\",\"expires_in\":3600,\"user_id\":\"u\"}") });
        }
    }

    [Test]
    async Task Offline_IsNotAnExpiredToken()
    {
        var login = new Login(); var store = new Store();
        var auth = new GogAuthService(new System.Net.Http.HttpClient(new TokenEndpoint()), store, login);
        bool threw = false;
        try { await auth.EnsureAuthenticatedAsync(); } catch (System.Net.Http.HttpRequestException) { threw = true; }
        Assert.True(threw, "the network failure surfaces as itself");
        Assert.Equal(0, login.Asked, "no login window for a laptop that is offline");
        Assert.Equal("rt", store.S!.RefreshToken, "the stored session is untouched");
    }

    [Test]
    async Task AServerError_IsNotAnExpiredTokenEither()
    {
        var login = new Login();
        var auth = new GogAuthService(new System.Net.Http.HttpClient(new TokenEndpoint { RefreshAnswers = System.Net.HttpStatusCode.BadGateway }), new Store(), login);
        try { await auth.EnsureAuthenticatedAsync(); } catch (System.Net.Http.HttpRequestException) { }
        Assert.Equal(0, login.Asked, "a 502 says nothing about the token");
    }

    [Test]
    async Task ARejectedRefreshToken_StillFallsBackToLogin()
    {
        var login = new Login();
        var auth = new GogAuthService(new System.Net.Http.HttpClient(new TokenEndpoint { RefreshAnswers = System.Net.HttpStatusCode.BadRequest }), new Store(), login);
        var s = await auth.EnsureAuthenticatedAsync();
        Assert.Equal(1, login.Asked, "GOG refused the token: that is the login case");
        Assert.Equal("new", s.AccessToken);
    }

    // A refreshed session became THE session before it was saved: a failed save left memory and disk disagreeing.
    [Test]
    async Task AFailedSave_KeepsTheOldSessionInMemory()
    {
        var store = new Store { SaveFails = true };
        var auth = new GogAuthService(new System.Net.Http.HttpClient(new TokenEndpoint { RefreshAnswers = System.Net.HttpStatusCode.OK }), store, new Login());
        bool threw = false;
        try { await auth.EnsureAuthenticatedAsync(); } catch (System.IO.IOException) { threw = true; }
        Assert.True(threw, "the save failure reaches the caller");
        Assert.Equal("rt", store.S!.RefreshToken, "disk still holds the old session");
        store.SaveFails = false;
        var s = await auth.EnsureAuthenticatedAsync();
        Assert.Equal("rt2", s.RefreshToken, "the next call refreshes from the OLD token: memory never adopted the unsaved one");
        Assert.Equal("rt2", store.S!.RefreshToken, "and this time it is on disk");
    }

    // Two GogAuthService instances over ONE token file (the App's interactive session and AccountSessions' chain).
    // GOG rotates the refresh token per exchange: an instance that refreshed with its own stale copy after the
    // other had rotated it presented a spent token and got a login window.
    sealed class RotatingEndpoint : System.Net.Http.HttpMessageHandler
    {
        public int Refreshes;
        public bool Offline;
        public string? LastRefreshToken;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage req, CancellationToken ct)
        {
            if (Offline) throw new System.Net.Http.HttpRequestException("No such host is known.");
            LastRefreshToken = System.Text.RegularExpressions.Regex.Match(req.RequestUri!.Query, "refresh_token=([^&]+)").Groups[1].Value;
            Refreshes++;
            var body = $"{{\"access_token\":\"at{Refreshes}\",\"refresh_token\":\"rt{Refreshes + 1}\",\"expires_in\":3600,\"user_id\":\"u\"}}";
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(body) });
        }
    }

    [Test]
    async Task ASecondInstance_SkipsTheRefresh_WhenDiskAlreadyHoldsAFreshSession()
    {
        var store = new Store { S = new("at", "rt1", DateTimeOffset.UtcNow.AddMinutes(-5), "u") };
        var ep = new RotatingEndpoint { Offline = true }; var http = new System.Net.Http.HttpClient(ep);
        var first = new GogAuthService(http, store, new Login());
        var second = new GogAuthService(http, store, new Login());
        // second loads the expired rt1 session while offline: it stays in memory, unrefreshed.
        try { await second.EnsureAuthenticatedAsync(); } catch (System.Net.Http.HttpRequestException) { }
        Assert.True(second.HasStoredSession, "second holds the stale session in memory");
        ep.Offline = false;
        await first.EnsureAuthenticatedAsync();      // refresh 1: rt1 -> rt2, fresh, saved
        Assert.Equal("rt2", store.S!.RefreshToken, "first rotated and saved");

        var s = await second.EnsureAuthenticatedAsync();
        Assert.Equal(1, ep.Refreshes, "second did not refresh: disk already had a fresh session");
        Assert.Equal("at1", s.AccessToken, "and it now serves the session first saved");
    }

    [Test]
    async Task ASecondInstance_RefreshesWithTheRotatedToken_NotItsStaleCopy()
    {
        var store = new Store { S = new("at", "rt1", DateTimeOffset.UtcNow.AddMinutes(-5), "u") };
        var ep = new RotatingEndpoint { Offline = true }; var http = new System.Net.Http.HttpClient(ep);
        var first = new GogAuthService(http, store, new Login());
        var second = new GogAuthService(http, store, new Login());
        try { await second.EnsureAuthenticatedAsync(); } catch (System.Net.Http.HttpRequestException) { }   // second holds rt1
        ep.Offline = false;
        await first.EnsureAuthenticatedAsync();      // refresh 1: rt1 -> rt2 saved
        Assert.Equal("rt1", ep.LastRefreshToken);
        // Pretend first's session lapsed again on disk so second must refresh too.
        store.S = store.S! with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await second.EnsureAuthenticatedAsync();     // refresh 2 must present rt2, never rt1
        Assert.Equal("rt2", ep.LastRefreshToken, "the refresh used the token on disk");
        Assert.Equal("rt3", store.S!.RefreshToken, "and rotated it again");

        // RefreshNow on first (holding rt2 in memory while disk says rt3) also rotates from disk.
        await first.RefreshNowAsync();
        Assert.Equal("rt3", ep.LastRefreshToken, "the keepalive refreshes from the newest token too");
    }
}

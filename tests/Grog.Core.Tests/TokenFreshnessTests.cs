// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using Grog.Core.Auth;
using Grog.Core.Cli;
using Grog.Core.Tests.Framework;

[NewBatch]
[Trait("freshness")]
public class TokenFreshnessTests
{
    static AuthSession Session(DateTimeOffset? obtained, DateTimeOffset expires, string refresh = "r")
        => new("a", refresh, expires, "u") { ObtainedAt = obtained };

    [Test] void LastRefreshed_ShowsAge()
    {
        var now = DateTimeOffset.UtcNow;
        var r = TokenFreshness.For(Session(now.AddHours(-3), now.AddMinutes(30)), now);
        Assert.True(r.LastRefreshed.Contains("3h"), "shows ~3h since refresh");
    }

    [Test] void AccessToken_ValidVsExpired()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(TokenFreshness.For(Session(now, now.AddMinutes(20)), now).AccessToken.StartsWith("valid"), "future expiry -> valid");
        Assert.True(TokenFreshness.For(Session(now, now.AddMinutes(-5)), now).AccessToken.StartsWith("expired"), "past expiry -> expired");
    }

    [Test] void LegacyToken_NoObtainedAt_IsUnknown()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(TokenFreshness.For(Session(null, now.AddMinutes(10)), now).LastRefreshed.Contains("unknown"), "no stamp -> unknown");
    }

    [Test] void MissingRefreshToken_Flagged()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(!TokenFreshness.For(Session(now, now, refresh: ""), now).HasRefreshToken, "empty refresh token -> false");
    }

    [Test] void Human_FormatsRanges()
    {
        Assert.Equal("under a minute", TokenFreshness.Human(TimeSpan.FromSeconds(30)), "sub-minute");
        Assert.Equal("42 min", TokenFreshness.Human(TimeSpan.FromMinutes(42)), "minutes");
        Assert.True(TokenFreshness.Human(TimeSpan.FromHours(3.1)).StartsWith("3h"), "hours");
        Assert.True(TokenFreshness.Human(TimeSpan.FromDays(6.2)).StartsWith("6d"), "days");
    }
}

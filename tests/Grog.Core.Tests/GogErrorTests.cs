namespace Grog.Core.Tests;

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Grog.Core.Api;
using Grog.Core.Tests.Framework;

public class GogErrorTests
{
    private static HttpRequestException Http(HttpStatusCode code) =>
        new("boom", null, code);

    [Test]
    void ServerError_IsActionable()
    {
        var msg = GogError.Describe(Http(HttpStatusCode.ServiceUnavailable));
        Assert.True(msg.Contains("503") && msg.Contains("Try again"), "5xx maps to a retry message");
    }

    [Test]
    void AuthFailure_PromptsReconnect()
    {
        Assert.True(GogError.Describe(Http(HttpStatusCode.Unauthorized)).Contains("reconnect"), "401 -> reconnect");
        Assert.True(GogError.IsAuthFailure(Http(HttpStatusCode.Forbidden)), "403 flagged as auth failure");
        Assert.False(GogError.IsAuthFailure(Http(HttpStatusCode.ServiceUnavailable)), "503 is not an auth failure");
    }

    [Test]
    void RateLimit_And_NotFound()
    {
        Assert.True(GogError.Describe(Http((HttpStatusCode)429)).Contains("rate-limit"), "429 -> rate limit");
        Assert.True(GogError.Describe(Http(HttpStatusCode.NotFound)).Contains("404"), "404 surfaced");
    }

    [Test]
    void Timeout_IsFriendly()
    {
        Assert.True(GogError.Describe(new TaskCanceledException()).Contains("didn't respond"), "timeout -> friendly");
    }

    [Test]
    void NoStatus_MeansNoConnection()
    {
        Assert.True(GogError.Describe(new HttpRequestException("dns")).Contains("internet"), "no status -> connection msg");
    }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Auth;
using Grog.Core.CloudSaves;
using Grog.Core.Tests.Framework;

// Sweep 2 #3 and #4: the containers listing read a failed call as "no saves" (and the reconciler then
// removed every entry of that account), and asked for page 1 only.
[Trait("cloud")]
public class CloudContainerListingTests
{
    sealed class FakeContainers : HttpMessageHandler
    {
        public int Total;
        public HttpStatusCode? FailWith;
        public List<string> Urls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            Urls.Add(url);
            if (FailWith is { } code) return Task.FromResult(new HttpResponseMessage(code));
            int page = int.Parse(System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query)["page"]!);
            int limit = int.Parse(System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query)["limit"]!);
            var ids = Enumerable.Range(1, Total).Skip((page - 1) * limit).Take(limit);
            var items = string.Join(",", ids.Select(i =>
                "{\"games\":[{\"id\":" + i + ",\"quota\":100}],\"container\":{\"size\":10,\"files\":1,\"space_id\":\"s" + i + "\"}}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"items\":[" + items + "]}", Encoding.UTF8, "application/json") });
        }
    }

    sealed class NoAuth : IAuthService
    {
        public bool HasStoredSession => true;
        public Task<AuthSession> EnsureAuthenticatedAsync(CancellationToken ct = default)
            => Task.FromResult(new AuthSession("at", "rt", DateTimeOffset.UtcNow.AddHours(1), "u"));
        public Task SignOutAsync() => Task.CompletedTask;
    }

    [Test]
    async Task EveryPageIsRead()
    {
        var fake = new FakeContainers { Total = 120 };
        var svc = new CloudSaveService(new HttpClient(fake), new NoAuth());

        var list = await svc.ListContainersAsync();

        Assert.Equal(120, list.Count, "three pages of 50/50/20");
        Assert.Equal(3, fake.Urls.Count, "stops at the short page");
    }

    [Test]
    async Task AFullLastPage_AsksOnceMoreAndStops()
    {
        var fake = new FakeContainers { Total = 50 };
        var svc = new CloudSaveService(new HttpClient(fake), new NoAuth());

        var list = await svc.ListContainersAsync();

        Assert.Equal(50, list.Count, "all of them");
        Assert.Equal(2, fake.Urls.Count, "a full page may not be the last; the empty one ends it");
    }

    [Test]
    async Task AFailedCall_Throws_SoTheReconcilerNeverSeesAnEmptyAccount()
    {
        var fake = new FakeContainers { FailWith = HttpStatusCode.InternalServerError };
        var svc = new CloudSaveService(new HttpClient(fake), new NoAuth());

        bool threw = false;
        try { await svc.ListContainersAsync(); } catch (HttpRequestException) { threw = true; }

        Assert.True(threw, "a 500 is not 'this account has no saves'");
    }

    [Test]
    async Task NotFound_IsAnAccountWithNoContainers()
    {
        var fake = new FakeContainers { FailWith = HttpStatusCode.NotFound };
        var svc = new CloudSaveService(new HttpClient(fake), new NoAuth());

        Assert.Equal(0, (await svc.ListContainersAsync()).Count, "404 = none");
    }
}

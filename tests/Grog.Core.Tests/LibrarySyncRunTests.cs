// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

/// <summary>
/// LibrarySyncService.RunAsync end to end against a fake GOG: owned ids, the rich listing, the mod tag
/// set and one gameDetails per product, including a product whose details 500 and one that returns
/// nothing. Written 09-02 before RunAsync was split into stages; keep it green through any sync change.
/// </summary>
[Trait("sync")]
public sealed class LibrarySyncRunTests
{
    private sealed class FakeGog : HttpMessageHandler
    {
        public List<long> Owned = new() { 1, 2, 3, 4 };
        public HashSet<long> Broken = new() { 3 };      // gameDetails 500s
        public HashSet<long> NoDetails = new() { 4 };   // gameDetails "[]" (DLC shell)
        public bool ListingDown;                        // getFilteredProducts 500s
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            string body;
            if (url.EndsWith("/user/data/games")) body = "{\"owned\":[" + string.Join(",", Owned) + "]}";
            else if (url.Contains("getFilteredProducts") && ListingDown)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("down") });
            else if (url.Contains("getFilteredProducts"))
                body = "{\"page\":1,\"totalPages\":1,\"totalProducts\":4,\"products\":["
                     + string.Join(",", Owned.Select(id => $"{{\"id\":{id},\"title\":\"Game {id}\",\"slug\":\"game_{id}\",\"category\":\"RPG\",\"isGame\":true,\"url\":\"/game/game_{id}\"}}"))
                     + "]}";
            else if (url.Contains("catalog.gog.com")) body = "{\"pages\":1,\"products\":[]}";
            else if (url.Contains("/account/gameDetails/"))
            {
                var id = long.Parse(url.Split('/').Last().Replace(".json", ""));
                if (Broken.Contains(id)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") });
                if (NoDetails.Contains(id)) body = "[]";
                else body = $"{{\"title\":\"Game {id}\",\"downloads\":[[\"English\",{{\"windows\":[{{\"manualUrl\":\"/downlink/game_{id}/en1installer0\",\"name\":\"Setup\",\"version\":\"1.0\",\"size\":\"10 MB\"}}]}}]],\"extras\":[]}}";
            }
            else return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class NoAuth : IAuthService
    {
        public bool HasStoredSession => true;
        public Task<AuthSession> EnsureAuthenticatedAsync(CancellationToken ct = default)
            => Task.FromResult(new AuthSession("at", "rt", DateTimeOffset.UtcNow.AddHours(1), "u"));
        public Task SignOutAsync() => Task.CompletedTask;
    }

    private sealed class MemoryStore : IManifestStore
    {
        public LibraryManifest Current { get; } = new();
        public ManifestGate Gate { get; } = new();
        public int Saves;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) { Saves++; return Task.CompletedTask; }
    }

    private static (LibrarySyncService Svc, MemoryStore Store, FakeGog Gog) Rig()
    {
        var gog = new FakeGog();
        var api = new GogApiClient(new HttpClient(gog), new NoAuth());
        api.DelayAsync = (_, _) => Task.CompletedTask;   // 500 retries: no real waiting
        var store = new MemoryStore();
        return (new LibrarySyncService(api, store) { AccountId = "acct-1" }, store, gog);
    }

    [Test]
    public async Task A_full_scan_adds_items_records_failures_and_excludes_shells()
    {
        var (svc, store, _) = Rig();
        var r = await svc.RunAsync();

        Assert.Equal(4, r.GamesSeen, "owned count");
        Assert.Equal(2, r.NewGames, "two products with downloads become items");
        Assert.True(r.NewGameIds.OrderBy(x => x).SequenceEqual(new long[] { 1, 2 }), "the new ids are named, for Back Up New");
        Assert.Equal(2, store.Current.Items.Count, "items stored");
        Assert.Equal(1, r.FetchFailures, "the 500 is recorded, not thrown");
        Assert.True(r.FetchFailedNames.Any(n => n.Contains("Game 3")), "failure named by title");
        Assert.Equal(1, r.NoDetailsProducts, "the [] shell is a no-details product");
        Assert.Equal(2, store.Current.Excluded.Count, "shell + failed product both excluded");
        Assert.True(store.Current.Excluded.Any(e => e.GogId == 3 && !string.IsNullOrEmpty(e.UnavailableReason)), "failed one carries a reason");
        Assert.True(store.Current.Excluded.Any(e => e.GogId == 4 && string.IsNullOrEmpty(e.UnavailableReason)), "shell carries none");
        Assert.True(r.SeenIds.SetEquals(new long[] { 1, 2, 3, 4 }), "every owned id counts as seen, failures included");
        var item = store.Current.ItemById(1)!;
        Assert.Equal("game_1", item.Slug, "slug from details title");
        Assert.True(item.Files.All(f => f.OwnerIds.Contains("acct-1")), "new files owned by the scanning account");
        Assert.True(store.Current.LastSyncCompleted is not null && store.Saves == 1, "stamped and saved once");
    }

    [Test]
    public async Task A_second_scan_merges_instead_of_duplicating()
    {
        var (svc, store, _) = Rig();
        await svc.RunAsync();
        var r2 = await svc.RunAsync();
        Assert.Equal(0, r2.NewGames, "nothing new");
        Assert.Equal(0, r2.NewGameIds.Count, "no ids to offer either");
        Assert.Equal(2, store.Current.Items.Count, "no duplicates");
    }

    [Test]
    public async Task A_scoped_scan_never_sweeps_the_unseen()
    {
        var (svc, store, gog) = Rig();
        await svc.RunAsync();
        gog.Owned = new() { 1 };
        var r = await svc.RunAsync(new long[] { 1 });
        Assert.Equal(0, r.Delisted, "a subset check says nothing about the rest");
        Assert.Equal(2, store.Current.Items.Count, "item 2 untouched");
    }

    // Sweep 2 #15: a product two accounts own is one product in the run's totals.
    [Test]
    public void Aggregate_counts_a_co_owned_product_once()
    {
        SyncResult Pass(params long[] seen) => new() { GamesSeen = seen.Length, SeenIds = new HashSet<long>(seen),
                                                       NoDetailsProducts = 1, NoDetailProductIds = new List<long> { 9 } };
        var agg = SyncResult.Aggregate(new[]
        {
            new AccountSyncPass("a", "A", false, null, Pass(1, 2, 9), null),
            new AccountSyncPass("b", "B", false, null, Pass(2, 3, 9), null),
        });
        Assert.Equal(4, agg.GamesSeen, "1, 2, 3 and 9: the union, not 6");
        Assert.Equal(1, agg.NoDetailsProducts, "the shared shell is one shell");
    }

    // Sweep 2 #14: a "check selected" pass rebuilt the WHOLE excluded list from its subset.
    [Test]
    public async Task A_scoped_scan_only_replaces_the_excluded_entries_it_asked_about()
    {
        var (svc, store, gog) = Rig();
        await svc.RunAsync();
        Assert.Equal(2, store.Current.Excluded.Count, "3 (failed) and 4 (shell)");

        await svc.RunAsync(new long[] { 1 });
        Assert.Equal(2, store.Current.Excluded.Count, "checking game 1 says nothing about 3 and 4");

        gog.Broken.Clear();   // 3 answers now
        await svc.RunAsync(new long[] { 3 });
        Assert.True(store.Current.Excluded.Select(e => e.GogId).SequenceEqual(new long[] { 4 }), "3 left the list, 4 stayed");
        Assert.True(store.Current.ItemById(3) is not null, "and 3 is an item now");
    }

    [Test]
    public async Task Without_the_rich_listing_taxonomy_and_excluded_list_are_kept_from_the_last_scan()
    {
        var (svc, store, gog) = Rig();
        await svc.RunAsync();
        var typeBefore = store.Current.ItemById(1)!.Type;
        var excludedBefore = store.Current.Excluded.Select(e => e.GogId).OrderBy(x => x).ToList();

        gog.ListingDown = true;
        var r = await svc.RunAsync();

        Assert.True(r.RichListingUnavailable, "the result says the listing failed");
        Assert.Equal(typeBefore, store.Current.ItemById(1)!.Type, "no evidence to reclassify: the type stands");
        Assert.Equal(string.Join(",", excludedBefore), string.Join(",", store.Current.Excluded.Select(e => e.GogId).OrderBy(x => x)), "excluded list kept, not rebuilt from nothing");
        Assert.Equal(2, store.Current.Items.Count, "items still merged");
    }
}

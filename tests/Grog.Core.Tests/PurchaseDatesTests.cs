// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Api;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Runs;
using Grog.Core.Tests.Framework;

/// <summary>(Purchase dates 09-09) Order history -> <see cref="LibraryItem.DateAcquired"/>. The payload
/// shapes asserted here are the MEASURED ones from docs/dev/GOG_Orders_API_2026-09-09.md: epoch-second
/// dates, string product ids, one order dating many products.</summary>
[NewBatch]
[Trait("dates")]
public class PurchaseDatesTests
{
    private sealed class MemoryStore : IManifestStore
    {
        public LibraryManifest Current { get; } = new();
        public ManifestGate Gate { get; } = new();
        public int Saves;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) { Saves++; return Task.CompletedTask; }
    }

    private static MemoryStore Library(params long[] ids)
    {
        var s = new MemoryStore();
        foreach (var id in ids) s.Current.Items.Add(new LibraryItem { GogId = id, Title = "g" + id, Slug = "g" + id });
        return s;
    }

    private static DateTimeOffset Epoch(long secs) => DateTimeOffset.FromUnixTimeSeconds(secs);

    // ---------------- parser ----------------

    /// <summary>The two traps that would silently produce zero dates: `date` is epoch SECONDS (not ISO) and
    /// a product `id` is a STRING (it is a number in every other GOG payload).</summary>
    [Test]
    void Parse_ReadsEpochSecondsAndStringProductIds()
    {
        var page = OrdersParser.Parse("""
        { "totalPages": 4, "orders": [
            { "date": 1788870014, "publicId": "141857263d2b", "status": "purchase", "paymentMethod": "Free Order",
              "products": [ { "id": "1472210676", "title": "A", "isRefunded": false },
                            { "id": "99", "title": "B", "isRefunded": true } ] } ] }
        """);
        Assert.Equal(4, page.TotalPages, "total pages");
        var order = Assert.Single(page.Orders);
        Assert.Equal(Epoch(1788870014), order.Date, "date is epoch seconds");
        Assert.Equal("141857263d2b", order.PublicId, "publicId");
        Assert.Equal(2, order.Products.Count, "products");
        Assert.Equal(1472210676L, order.Products[0].Id, "string id parsed to long");
        Assert.True(order.Products[1].IsRefunded, "isRefunded carried");
    }

    /// <summary>The mandatory filter flags are part of the URL. Without them the endpoint answers 200 with an
    /// EMPTY orders array, which reads exactly like an account that has never bought anything.</summary>
    [Test]
    void PageUrl_CarriesTheMandatoryFilterFlags()
    {
        var url = OrdersParser.PageUrl(3);
        foreach (var flag in new[] { "canceled=0", "completed=1", "in_progress=1", "not_redeemed=1", "pending=1", "redeemed=1" })
            Assert.Contains(flag, url, "mandatory flag " + flag);
        Assert.Contains("page=3", url, "page");
    }

    /// <summary>A junk or missing date leaves the ORDER out rather than throwing: one bad row must not cost
    /// a scan every other date on the page.</summary>
    [Test]
    void Parse_SkipsOrdersWithNoUsableDate()
    {
        var page = OrdersParser.Parse("""
        { "totalPages": 1, "orders": [
            { "publicId": "no-date", "products": [ { "id": "1" } ] },
            { "date": 0, "publicId": "zero", "products": [ { "id": "2" } ] },
            { "date": "not-a-date", "publicId": "junk", "products": [ { "id": "3" } ] },
            { "date": 1700000000, "publicId": "good", "products": [ { "id": "4" } ] } ] }
        """);
        Assert.Equal("good", Assert.Single(page.Orders).PublicId, "only the dated order survives");
    }

    /// <summary>An empty orders array parses to nothing, not an exception -- that is what the endpoint
    /// answers when the filter flags are missing, and what a brand-new account answers legitimately.</summary>
    [Test]
    void Parse_EmptyOrdersIsNotAnError()
    {
        var page = OrdersParser.Parse("""{ "totalPages": 1, "orders": [] }""");
        Assert.Empty(page.Orders, "no orders");
    }

    /// <summary>The same product in two orders keeps the EARLIEST: the question is when it entered the
    /// library, not the last time money moved (gift first, repurchase later).</summary>
    [Test]
    void Fold_KeepsTheEarliestDatePerProduct()
    {
        var later = new OrderRecord(Epoch(1_700_000_000), "later", new[] { new OrderProduct(7, "G", false) });
        var early = new OrderRecord(Epoch(1_500_000_000), "early", new[] { new OrderProduct(7, "G", false) });
        var map = new Dictionary<long, DateTimeOffset>();
        OrdersParser.Fold(new[] { later, early }, map);      // newest-first, as the endpoint returns them
        Assert.Equal(Epoch(1_500_000_000), map[7], "earliest wins");
    }

    /// <summary>One order dates EVERY product inside it. This is the fact that makes the cost track order
    /// count instead of library size, and it is what a free batch claim looks like.</summary>
    [Test]
    void Fold_OneOrderDatesEveryProductInIt()
    {
        var claim = new OrderRecord(Epoch(1_600_000_000), "free-batch",
            new[] { new OrderProduct(1, "A", false), new OrderProduct(2, "B", false), new OrderProduct(3, "C", false) });
        var map = new Dictionary<long, DateTimeOffset>();
        OrdersParser.Fold(new[] { claim }, map);
        Assert.Equal(3, map.Count, "all three dated by one order");
    }

    // ---------------- apply ----------------

    /// <summary>Only items we OWN get dated; an order for something no longer in the library is ignored,
    /// which is what makes refunds self-limiting without a refund rule of their own.</summary>
    [Test]
    void Apply_DatesOwnedItemsOnly()
    {
        var store = Library(1, 2);
        var n = PurchaseDatesRun.Apply(store, new Dictionary<long, DateTimeOffset>
        {
            [1] = Epoch(1_500_000_000),
            [999] = Epoch(1_500_000_000),   // refunded / no longer owned
        });
        Assert.Equal(1, n, "one item dated");
        Assert.Equal(Epoch(1_500_000_000), store.Current.ItemById(1)!.DateAcquired, "item 1 dated");
        Assert.Null(store.Current.ItemById(2)!.DateAcquired, "item 2 appears in no order and stays undated");
    }

    /// <summary>An item that already has a date is never rewritten: a purchase date does not change, and a
    /// later gift order must not overwrite the original acquisition.</summary>
    [Test]
    void Apply_NeverOverwritesADateAlreadyHeld()
    {
        var store = Library(1);
        store.Current.ItemById(1)!.DateAcquired = Epoch(1_400_000_000);
        var n = PurchaseDatesRun.Apply(store, new Dictionary<long, DateTimeOffset> { [1] = Epoch(1_900_000_000) });
        Assert.Equal(0, n, "nothing changed");
        Assert.Equal(Epoch(1_400_000_000), store.Current.ItemById(1)!.DateAcquired, "original date kept");
    }

    // ---------------- mode choice ----------------

    /// <summary>No evidence either way = the library has never been walked, so the run walks every page.</summary>
    [Test]
    void NeedsBackfill_TrueOnlyWhenThereIsNoEvidenceEitherWay()
    {
        var store = Library(1, 2);
        Assert.True(PurchaseDatesRun.NeedsBackfill(store.Current), "nothing dated and nothing answered");
        store.Current.ItemById(1)!.DateAcquired = Epoch(1_500_000_000);
        Assert.False(PurchaseDatesRun.NeedsBackfill(store.Current), "one date means the walk has happened");
        Assert.Equal(1, PurchaseDatesRun.UndatedCount(store.Current), "one still undated");
    }

    /// <summary>A SETTLED item is evidence of a walk just as much as a dated one: a library whose every
    /// product came back "no order record" must not keep re-backfilling forever.</summary>
    [Test]
    void NeedsBackfill_FalseWhenEverythingWasAnsweredNoRecord()
    {
        var store = Library(1, 2);
        PurchaseDatesRun.Settle(store);
        Assert.False(PurchaseDatesRun.NeedsBackfill(store.Current), "answered counts as walked");
        Assert.Equal(0, PurchaseDatesRun.UnansweredCount(store.Current), "nothing left to ask");
        Assert.Equal(2, PurchaseDatesRun.UndatedCount(store.Current), "both still show no date");
    }

    /// <summary>The distinction the whole run is built on: "no date" is two different states, and only one
    /// of them is worth a request.</summary>
    [Test]
    void Unanswered_SeparatesNotAskedYetFromAskedAndNotThere()
    {
        var store = Library(1, 2, 3);
        store.Current.ItemById(1)!.DateAcquired = Epoch(1_500_000_000);
        store.Current.ItemById(2)!.NoPurchaseRecord = true;
        Assert.Equal(2, PurchaseDatesRun.UndatedCount(store.Current), "two carry no date");
        Assert.Equal(1, PurchaseDatesRun.UnansweredCount(store.Current), "but only one is worth asking about");
        Assert.Equal(3L, Assert.Single(PurchaseDatesRun.Unanswered(store.Current)).GogId, "the unasked one");
    }

    /// <summary>Settling marks only what is undated, and never disturbs a date already held.</summary>
    [Test]
    void Settle_MarksTheUndatedAndLeavesDatedItemsAlone()
    {
        var store = Library(1, 2);
        store.Current.ItemById(1)!.DateAcquired = Epoch(1_500_000_000);
        Assert.Equal(1, PurchaseDatesRun.Settle(store), "one newly settled");
        Assert.False(store.Current.ItemById(1)!.NoPurchaseRecord, "a dated item is not 'no record'");
        Assert.True(store.Current.ItemById(2)!.NoPurchaseRecord, "the undated one is answered");
        Assert.Equal(0, PurchaseDatesRun.Settle(store), "settling twice changes nothing");
    }

    /// <summary>A date arriving for a settled item clears the flag: the two states are mutually exclusive,
    /// and leaving both set would be a manifest that contradicts itself.</summary>
    [Test]
    void Apply_ClearsNoPurchaseRecordWhenADateFinallyArrives()
    {
        var store = Library(1);
        store.Current.ItemById(1)!.NoPurchaseRecord = true;
        Assert.Equal(1, PurchaseDatesRun.Apply(store, new Dictionary<long, DateTimeOffset> { [1] = Epoch(1_500_000_000) }), "dated");
        Assert.False(store.Current.ItemById(1)!.NoPurchaseRecord, "flag cleared");
    }

    /// <summary>`--force` re-opens every settled answer so the next pass asks again from scratch.</summary>
    [Test]
    void Reopen_UnsettlesEverything()
    {
        var store = Library(1, 2);
        PurchaseDatesRun.Settle(store);
        Assert.Equal(2, PurchaseDatesRun.Reopen(store.Current), "both re-opened");
        Assert.Equal(2, PurchaseDatesRun.UnansweredCount(store.Current), "worth asking about again");
    }

    // ---- every account's orders (sweep 2 #9) ------------------------------------------------------------

    private static PurchaseDatesRun.AccountOrders Orders(string id, bool complete, List<string> asked, params (long Id, long At)[] dates)
        => new(id, (pages, _) =>
        {
            asked.Add(id);
            var d = new Dictionary<long, DateTimeOffset>();
            foreach (var (gid, at) in dates) d[gid] = Epoch(at);
            return Task.FromResult(new Grog.Core.Api.PurchaseDateSweep(d, 1, complete));
        });

    private static MemoryStore TwoAccountLibrary()
    {
        var store = Library(1, 2, 3);
        store.Current.ItemById(1)!.AccountId = "a";
        store.Current.ItemById(2)!.AccountId = "b";
        store.Current.ItemById(3)!.AccountId = "b";
        return store;
    }

    /// <summary>The bug: only the primary's client was asked, so the second account's purchases were never
    /// dated and were then settled as "no record".</summary>
    [Test]
    async Task EveryAccountsOrdersAreAsked_OnItsOwnClient()
    {
        var store = TwoAccountLibrary(); var asked = new List<string>();
        var r = await PurchaseDatesRun.RunForAccountsAsync(store, new[]
        {
            Orders("a", true, asked, (1, 1_500_000_000)),
            Orders("b", true, asked, (2, 1_600_000_000)),
        }, CancellationToken.None);

        Assert.Equal(2, r.Dated, "one from each account's history");
        Assert.True(asked.Contains("a") && asked.Contains("b"), "both histories were read");
        Assert.True(store.Current.ItemById(2)!.DateAcquired is not null, "the second account's purchase is dated");
        Assert.True(store.Current.ItemById(3)!.NoPurchaseRecord, "b's complete walk may settle b's undated item");
    }

    /// <summary>A signed-out owner (no entry) or an incomplete walk never settles that owner's items.</summary>
    [Test]
    async Task AnItemIsSettledOnlyWhenEveryOwnerWasWalkedToTheEnd()
    {
        var store = TwoAccountLibrary(); var asked = new List<string>();
        var r = await PurchaseDatesRun.RunForAccountsAsync(store, new[] { Orders("a", true, asked) }, CancellationToken.None);

        Assert.True(store.Current.ItemById(1)!.NoPurchaseRecord, "a was walked: a's item is answered");
        Assert.False(store.Current.ItemById(2)!.NoPurchaseRecord, "b was never asked: b's items stay unanswered");
        Assert.False(store.Current.ItemById(3)!.NoPurchaseRecord, "same");
        Assert.Equal(1, r.Settled);
    }

    /// <summary>An empty library never asks for a backfill: there is nothing to date.</summary>
    [Test]
    void NeedsBackfill_FalseOnAnEmptyLibrary()
        => Assert.False(PurchaseDatesRun.NeedsBackfill(new LibraryManifest()), "empty library");

    /// <summary>The whole reason this is safe to run after every interactive scan: with every item dated it
    /// returns immediately, spends NO requests, and saves nothing.</summary>
    [Test]
    async Task RunAsync_WithEverythingDatedSpendsNoRequests()
    {
        var store = Library(1);
        store.Current.ItemById(1)!.DateAcquired = Epoch(1_500_000_000);
        // A null client is the assertion: reaching the network at all would throw here.
        var r = await PurchaseDatesRun.RunAsync(store, null!, CancellationToken.None);
        Assert.Equal(0, r.Requests, "no requests");
        Assert.Equal(0, r.Dated, "nothing dated");
        Assert.Equal(0, store.Saves, "nothing saved");
    }

    /// <summary>THE REGRESSION THIS FLAG EXISTS FOR (found on real data 09-09): 53 of a 125-item library
    /// appear in no order at all. Before settling, every scan spent a request re-asking about them forever.
    /// Once answered, the pass costs nothing -- the null client proves it never reaches the network.</summary>
    [Test]
    async Task RunAsync_WithEverythingAnsweredNoRecordSpendsNoRequests()
    {
        var store = Library(1, 2, 3);
        PurchaseDatesRun.Settle(store);
        var r = await PurchaseDatesRun.RunAsync(store, null!, CancellationToken.None);
        Assert.Equal(0, r.Requests, "no requests: the answer is already known");
        Assert.Equal(0, store.Saves, "nothing saved");
    }

    /// <summary>A newly added product re-arms the pass on its own: it is unanswered by default, so nothing
    /// has to remember to un-settle anything when the library grows.</summary>
    [Test]
    void ANewItemIsUnansweredByDefault()
    {
        var store = Library(1, 2);
        PurchaseDatesRun.Settle(store);
        Assert.Equal(0, PurchaseDatesRun.UnansweredCount(store.Current), "quiet");
        store.Current.Items.Add(new LibraryItem { GogId = 3, Title = "new", Slug = "new" });
        Assert.Equal(1, PurchaseDatesRun.UnansweredCount(store.Current), "the new item re-arms the pass");
    }
}

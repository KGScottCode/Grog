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

    [Test]
    void Parse_ReadsEpochSecondsAndStringProductIds()
    {
        var page = OrdersParser.Parse("""
        { "totalPages": 4, "orders": [
            { "date": 1700000000, "publicId": "0a1b2c3d4e5f", "status": "purchase", "paymentMethod": "Free Order",
              "products": [ { "id": "1000000001", "title": "A", "isRefunded": false },
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

    [Test]
    void PageUrl_CarriesTheMandatoryFilterFlags()
    {
        var url = OrdersParser.PageUrl(3);
        foreach (var flag in new[] { "canceled=0", "completed=1", "in_progress=1", "not_redeemed=1", "pending=1", "redeemed=1" })
            Assert.Contains(flag, url, "mandatory flag " + flag);
        Assert.Contains("page=3", url, "page");
    }

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

    [Test]
    void Parse_EmptyOrdersIsNotAnError()
    {
        var page = OrdersParser.Parse("""{ "totalPages": 1, "orders": [] }""");
        Assert.Empty(page.Orders, "no orders");
    }

    [Test]
    void Fold_KeepsTheEarliestDatePerProduct()
    {
        var later = new OrderRecord(Epoch(1_700_000_000), "later", new[] { new OrderProduct(7, "G", false) });
        var early = new OrderRecord(Epoch(1_500_000_000), "early", new[] { new OrderProduct(7, "G", false) });
        var map = new Dictionary<long, DateTimeOffset>();
        OrdersParser.Fold(new[] { later, early }, map);      // newest-first, as the endpoint returns them
        Assert.Equal(Epoch(1_500_000_000), map[7], "earliest wins");
    }

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

    [Test]
    void NeedsBackfill_TrueOnlyWhenThereIsNoEvidenceEitherWay()
    {
        var store = Library(1, 2);
        Assert.True(PurchaseDatesRun.NeedsBackfill(store.Current), "nothing dated and nothing answered");
        store.Current.ItemById(1)!.DateAcquired = Epoch(1_500_000_000);
        Assert.False(PurchaseDatesRun.NeedsBackfill(store.Current), "one date means the walk has happened");
        Assert.Equal(1, PurchaseDatesRun.UndatedCount(store.Current), "one still undated");
    }

    [Test]
    void NeedsBackfill_FalseWhenEverythingWasAnsweredNoRecord()
    {
        var store = Library(1, 2);
        PurchaseDatesRun.Settle(store);
        Assert.False(PurchaseDatesRun.NeedsBackfill(store.Current), "answered counts as walked");
        Assert.Equal(0, PurchaseDatesRun.UnansweredCount(store.Current), "nothing left to ask");
        Assert.Equal(2, PurchaseDatesRun.UndatedCount(store.Current), "both still show no date");
    }

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

    [Test]
    void Apply_ClearsNoPurchaseRecordWhenADateFinallyArrives()
    {
        var store = Library(1);
        store.Current.ItemById(1)!.NoPurchaseRecord = true;
        Assert.Equal(1, PurchaseDatesRun.Apply(store, new Dictionary<long, DateTimeOffset> { [1] = Epoch(1_500_000_000) }), "dated");
        Assert.False(store.Current.ItemById(1)!.NoPurchaseRecord, "flag cleared");
    }

    [Test]
    void Reopen_UnsettlesEverything()
    {
        var store = Library(1, 2);
        PurchaseDatesRun.Settle(store);
        Assert.Equal(2, PurchaseDatesRun.Reopen(store.Current), "both re-opened");
        Assert.Equal(2, PurchaseDatesRun.UnansweredCount(store.Current), "worth asking about again");
    }

    // ---- every account's orders ------------------------------------------------------------

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

    [Test]
    void NeedsBackfill_FalseOnAnEmptyLibrary()
        => Assert.False(PurchaseDatesRun.NeedsBackfill(new LibraryManifest()), "empty library");

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

    [Test]
    async Task RunAsync_WithEverythingAnsweredNoRecordSpendsNoRequests()
    {
        var store = Library(1, 2, 3);
        PurchaseDatesRun.Settle(store);
        var r = await PurchaseDatesRun.RunAsync(store, null!, CancellationToken.None);
        Assert.Equal(0, r.Requests, "no requests: the answer is already known");
        Assert.Equal(0, store.Saves, "nothing saved");
    }

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

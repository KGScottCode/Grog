// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Api;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Runs;

public sealed record PurchaseDatesResult(int Dated, int Requests, bool Backfilled, int Settled = 0)
{
    public static readonly PurchaseDatesResult Nothing = new(0, 0, false);
    public bool DidAnything => Dated > 0 || Settled > 0;
}

public static class PurchaseDatesRun
{
    public static IEnumerable<LibraryItem> Unanswered(LibraryManifest m)
        => m.Items.Where(i => i.DateAcquired is null && !i.NoPurchaseRecord);

    public static int UnansweredCount(LibraryManifest m) => Unanswered(m).Count();

    public static int UndatedCount(LibraryManifest m) => m.Items.Count(i => i.DateAcquired is null);

    public static int SettledCount(LibraryManifest m) => m.Items.Count(i => i.NoPurchaseRecord);

    public static bool NeedsBackfill(LibraryManifest m)
        => m.Items.Count > 0 && m.Items.All(i => i.DateAcquired is null && !i.NoPurchaseRecord);

    public static async Task<PurchaseDatesResult> RunAsync(IManifestStore store, GogApiClient api,
                                                           CancellationToken ct = default)
    {
        bool backfill;
        using (store.Gate.Enter())
        {
            var m = store.Current;
            if (UnansweredCount(m) == 0) return PurchaseDatesResult.Nothing;
            backfill = NeedsBackfill(m);
        }

        var sweep = await api.GetPurchaseDatesAsync(backfill ? null : 1, ct);
        int requests = sweep.Requests;

        int dated = sweep.Dates.Count > 0 ? Apply(store, sweep.Dates) : 0;

        bool complete = sweep.Complete;
        if (!complete && store.Read(UnansweredCount) > 0)
        {
            var full = await api.GetPurchaseDatesAsync(null, ct);
            requests += full.Requests;
            complete = full.Complete;
            if (full.Dates.Count > 0) dated += Apply(store, full.Dates);
        }

        int settled = complete ? Settle(store) : 0;

        if (dated > 0 || settled > 0) await store.SaveAsync(ct);
        return new PurchaseDatesResult(dated, requests, backfill, settled);
    }

    public sealed record AccountOrders(string AccountId, Func<int?, CancellationToken, Task<PurchaseDateSweep>> Sweep);

    public static List<AccountOrders> OrdersOf(Grog.Core.Auth.AccountSessions sessions)
    {
        var list = new List<AccountOrders>();
        foreach (var a in sessions.Accounts.ToList())
        {
            if (!sessions.IsConnected(a.Id)) continue;
            var api = sessions.ApiFor(a.Id);
            list.Add(new AccountOrders(a.Id, (pages, ct) => api.GetPurchaseDatesAsync(pages, ct)));
        }
        return list;
    }

    private static bool OwnedBy(LibraryItem i, string accountId) => i.OwnerIds.Contains(accountId);

    public static async Task<PurchaseDatesResult> RunForAccountsAsync(IManifestStore store, IReadOnlyList<AccountOrders> accounts,
                                                           CancellationToken ct = default)
    {
        if (store.Read(UnansweredCount) == 0) return PurchaseDatesResult.Nothing;
        int requests = 0, dated = 0; bool anyBackfill = false;
        var walkedToTheEnd = new HashSet<string>();
        foreach (var acct in accounts)
        {
            ct.ThrowIfCancellationRequested();
            bool backfill; int mine;
            using (store.Gate.Enter())
            {
                var own = store.Current.Items.Where(i => OwnedBy(i, acct.AccountId)).ToList();
                mine = own.Count(i => i.DateAcquired is null && !i.NoPurchaseRecord);
                backfill = own.Count > 0 && own.All(i => i.DateAcquired is null && !i.NoPurchaseRecord);
            }
            if (mine == 0) { walkedToTheEnd.Add(acct.AccountId); continue; }   // nothing of theirs is waiting on an answer
            anyBackfill |= backfill;

            var sweep = await acct.Sweep(backfill ? null : 1, ct);
            requests += sweep.Requests;
            if (sweep.Dates.Count > 0) dated += Apply(store, sweep.Dates);
            bool complete = sweep.Complete;
            if (!complete && store.Read(m => m.Items.Any(i => OwnedBy(i, acct.AccountId) && i.DateAcquired is null && !i.NoPurchaseRecord)))
            {
                var full = await acct.Sweep(null, ct);
                requests += full.Requests;
                complete = full.Complete;
                if (full.Dates.Count > 0) dated += Apply(store, full.Dates);
            }
            if (complete) walkedToTheEnd.Add(acct.AccountId);
        }

        int settled = 0;
        store.Mutate(m =>
        {
            foreach (var item in m.Items)
                if (item.DateAcquired is null && !item.NoPurchaseRecord && item.OwnerIds.All(walkedToTheEnd.Contains))
                { item.NoPurchaseRecord = true; settled++; }
        });

        if (dated > 0 || settled > 0) await store.SaveAsync(ct);
        return new PurchaseDatesResult(dated, requests, anyBackfill, settled);
    }

    public static int Apply(IManifestStore store, IReadOnlyDictionary<long, DateTimeOffset> dates)
    {
        int n = 0;
        store.Mutate(m => n = Apply(m, dates));
        return n;
    }

    public static int Apply(LibraryManifest m, IReadOnlyDictionary<long, DateTimeOffset> dates)
    {
        int n = 0;
        foreach (var item in m.Items)
        {
            if (item.DateAcquired is not null) continue;
            if (!dates.TryGetValue(item.GogId, out var when)) continue;
            item.DateAcquired = when;
            item.NoPurchaseRecord = false;   // a date is proof there IS a record; never leave both set
            n++;
        }
        return n;
    }

    public static int Settle(IManifestStore store)
    {
        int n = 0;
        store.Mutate(m => n = Settle(m));
        return n;
    }

    public static int Settle(LibraryManifest m)
    {
        int n = 0;
        foreach (var item in m.Items)
            if (item.DateAcquired is null && !item.NoPurchaseRecord) { item.NoPurchaseRecord = true; n++; }
        return n;
    }

    public static int Reopen(LibraryManifest m)
    {
        int n = 0;
        foreach (var item in m.Items)
            if (item.NoPurchaseRecord) { item.NoPurchaseRecord = false; n++; }
        return n;
    }
}

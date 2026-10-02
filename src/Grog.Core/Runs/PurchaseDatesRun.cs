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

/// <summary>What a purchase-dates pass did. <see cref="Requests"/> is the honest cost: 0 when the run
/// decided there was nothing to ask about. <see cref="Settled"/> is how many products a complete walk
/// answered "not in your order history" -- they are never asked about again.</summary>
public sealed record PurchaseDatesResult(int Dated, int Requests, bool Backfilled, int Settled = 0)
{
    public static readonly PurchaseDatesResult Nothing = new(0, 0, false);
    public bool DidAnything => Dated > 0 || Settled > 0;
}

/// <summary>
/// Fills <see cref="LibraryItem.DateAcquired"/> from GOG's order history. The endpoint and its traps are
/// documented in `docs/dev/GOG_Orders_API_2026-09-09.md`; the SHAPE of the work follows from three measured
/// facts: orders are coarse (one order dates every product in it, so cost tracks order count, not library
/// size), a purchase date never changes (so a dated item is never asked about again), and a large minority
/// of owned products appear in NO order at all (53 of one reference library -- Connect imports, DLC bundled
/// under a base game, pre-order grants).
///
/// That third fact is what the run is really built around. "Undated" is not one state but two, and treating
/// them as one is what made an early cut spend a request on every single scan forever:
///   - NOT ASKED YET -- worth a request.
///   - ASKED, NOT THERE (<see cref="LibraryItem.NoPurchaseRecord"/>) -- a COMPLETE walk already put the
///     question to GOG and got an answer. Asking again cannot change it, so it never costs another request.
/// Only a complete walk may settle an item: a run cut short by an error or a page cap has not finished
/// asking, and freezing "no record" on a truncated answer would be permanent and wrong.
///
/// Modes, chosen from the manifest rather than by the caller:
///   - BACKFILL, once: nothing has ever been asked, so walk every page (4 requests on the reference account).
///   - TOP-UP: some items are unanswered, and orders come newest first, so page 1 is where a recent
///     acquisition must be. One request. If page 1 does not account for everything unanswered, the run
///     ESCALATES to a full walk in the same pass -- the case where more orders were placed than one page
///     holds -- so an unanswered item can never be settled on evidence that did not cover it.
///   - NOTHING: every item is dated or settled. No request at all. This is the steady state, and it is why
///     the pass is safe to hang off every interactive scan.
/// It is never called on the scheduled path: a cron backup does not pay for a cosmetic field (owner call).
/// </summary>
public static class PurchaseDatesRun
{
    /// <summary>Items still worth a request: no date, and no complete walk has answered for them yet.
    /// A newly added product is unanswered by default, which is what makes the top-up self-arming.</summary>
    public static IEnumerable<LibraryItem> Unanswered(LibraryManifest m)
        => m.Items.Where(i => i.DateAcquired is null && !i.NoPurchaseRecord);

    public static int UnansweredCount(LibraryManifest m) => Unanswered(m).Count();

    /// <summary>Items with no date at all, answered or not -- what the UI would show as blank.</summary>
    public static int UndatedCount(LibraryManifest m) => m.Items.Count(i => i.DateAcquired is null);

    /// <summary>Products a complete walk answered "no order record" for.</summary>
    public static int SettledCount(LibraryManifest m) => m.Items.Count(i => i.NoPurchaseRecord);

    /// <summary>True when order history has never been walked for this library: nothing is dated and
    /// nothing has been settled, so there is no evidence either way.</summary>
    public static bool NeedsBackfill(LibraryManifest m)
        => m.Items.Count > 0 && m.Items.All(i => i.DateAcquired is null && !i.NoPurchaseRecord);

    /// <summary>Run the pass. Returns <see cref="PurchaseDatesResult.Nothing"/> without touching the
    /// network when every item is dated or already settled.</summary>
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

        // Outside the gate: this is the network part, and the gate is not held across I/O.
        var sweep = await api.GetPurchaseDatesAsync(backfill ? null : 1, ct);
        int requests = sweep.Requests;

        int dated = sweep.Dates.Count > 0 ? Apply(store, sweep.Dates) : 0;

        // A capped top-up that left something unanswered has not asked about it at all -- its order could
        // sit on page 2 when more was bought than one page holds. Escalate rather than settle on evidence
        // that never covered the item.
        bool complete = sweep.Complete;
        if (!complete && store.Read(UnansweredCount) > 0)
        {
            var full = await api.GetPurchaseDatesAsync(null, ct);
            requests += full.Requests;
            complete = full.Complete;
            if (full.Dates.Count > 0) dated += Apply(store, full.Dates);
        }

        // Only a walk that read every page may conclude "GOG has no record of this".
        int settled = complete ? Settle(store) : 0;

        if (dated > 0 || settled > 0) await store.SaveAsync(ct);
        return new PurchaseDatesResult(dated, requests, backfill, settled);
    }

    /// <summary>One account's order history, as a callable: (maxPages, ct) -> sweep. A delegate rather than the
    /// client so the multi-account pass is testable without a network.</summary>
    public sealed record AccountOrders(string AccountId, Func<int?, CancellationToken, Task<PurchaseDateSweep>> Sweep);

    /// <summary>The account-aware list for a session set: one entry per CONNECTED account, each on its own
    /// client. A signed-out account is left out, so its items stay unanswered instead of being settled.</summary>
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

    /// <summary>The pass over EVERY account (sweep 2 #9). An order lives in the history of the account that
    /// placed it; asking only the primary's client dated nothing a secondary account bought and then SETTLED
    /// those items as "GOG has no record", permanently. Per account: the same backfill / top-up / escalate
    /// rules as the single pass, scoped to the items that account owns. An item is settled only when EVERY
    /// one of its owners was walked to the end in this run: one owner's complete "not here" says nothing
    /// about the other owner's orders.</summary>
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

    /// <summary>Write the dates onto the items we hold. Pure graph work under the gate; returns how many
    /// items actually changed. An item that already has a date is LEFT ALONE -- a purchase date does not
    /// change, and re-deriving it would let a later gift order overwrite the original acquisition.</summary>
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

    /// <summary>Mark every still-undated item as answered. ONLY legal after a walk that read every page.
    /// Returns how many were newly settled.</summary>
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

    /// <summary>Re-open every settled answer, so the next pass asks again from scratch. The escape hatch
    /// behind `grogcli dates --force`: used when GOG's history itself is believed to have changed, or when
    /// a settle is suspected of having been recorded on a bad walk.</summary>
    public static int Reopen(LibraryManifest m)
    {
        int n = 0;
        foreach (var item in m.Items)
            if (item.NoPurchaseRecord) { item.NoPurchaseRecord = false; n++; }
        return n;
    }
}

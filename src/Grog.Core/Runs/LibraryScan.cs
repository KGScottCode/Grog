// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;

namespace Grog.Core.Runs;

/// <summary>A full library scan, whichever shape the account list has: no registered accounts = one legacy
/// session; otherwise every connected account through <see cref="MultiAccountSync"/>. Both hosts had their
/// own copy of this fork and their own (unequal) aggregate before 09-02.</summary>
public static class LibraryScan
{
    public sealed record Outcome(SyncResult Aggregate, IReadOnlyList<AccountSyncPass>? Passes)
    {
        /// <summary>What the purchase-dates pass did, when the caller asked for one. Null when it was not
        /// requested -- which is every scheduled run. (Purchase dates 09-09)</summary>
        public PurchaseDatesResult? Dates { get; init; }

        /// <summary>Accounts exist and every one of them failed or was skipped: nothing was fetched.</summary>
        public bool AllAccountsFailed => Passes is { Count: > 0 } && Passes.All(p => p.Result is null);
    }

    /// <summary>Is the last completed scan fresh enough to skip? One rule (the App had two copies).</summary>
    public static bool IsRecent(LibraryManifest manifest, DateTimeOffset now, TimeSpan window)
        => manifest.LastSyncCompleted is { } t && now - t < window;

    /// <summary>A scoped "check selected" pass: only <paramref name="ids"/>, through the interactive session
    /// (no delisted sweep runs on a scoped pass, so one account's view is fine). The host dresses it as a
    /// legacy single pass (null account). (S2.2)</summary>
    public static Task<SyncResult> RunScopedAsync(IManifestStore manifest, GogApiClient api, IReadOnlyCollection<long> ids,
                                                  IBackupHost host, CancellationToken ct)
    {
        // The interactive session IS the first registered account's (RebindInteractiveSession), so the pass is
        // attributed to it. Left at "", every checked file gained an owner "" that no account answers to
        // (sweep 2 #14). With no accounts registered "" is still the legacy single slot.
        var first = manifest.Current.Accounts.Count > 0 ? manifest.Current.Accounts[0].Id : "";
        var svc = new LibrarySyncService(api, manifest) { AccountId = first };
        host.ConfigureScan(svc, null, 1, 1);
        return svc.RunAsync(ids, ct);
    }

    /// <summary>One account's library merged into a shared manifest (the App's Add account): the pass is
    /// tagged with the account's id so files attribute to it, and the host owns the reconcile (this scan sees
    /// ONE library, so a delisted sweep would flag the other accounts' games). Index 0 tells the host this is
    /// not a numbered pass of a full scan. (S2.2)</summary>
    public static Task<SyncResult> MergeAccountAsync(IManifestStore manifest, GogApiClient api, GrogAccount account,
                                                     IBackupHost host, CancellationToken ct)
    {
        var svc = new LibrarySyncService(api, manifest) { AccountId = account.Id, HostOwnsReconcile = true };
        host.ConfigureScan(svc, account, 0, 0);
        return svc.RunAsync(ct);
    }

    /// <summary>A full scan. <paramref name="datePurchases"/> adds the acquisition-date pass AFTER the merge
    /// (see <see cref="PurchaseDatesRun"/>): the two INTERACTIVE scans -- the App's Rescan and the CLI's
    /// `sync` -- pass true, and <see cref="BackupRun"/>'s scheduled scan does not, because a cron backup must
    /// not pay for a cosmetic field (owner call). The flag lives here so that rule is stated ONCE instead of
    /// once per host. It is cheap by construction: with nothing undated the pass makes no request at all.</summary>
    public static async Task<Outcome> RunAsync(IManifestStore manifest, GogApiClient api, AccountSessions? sessions,
                                               IBackupHost host, CancellationToken ct, bool datePurchases = false)
    {
        // (New items 09-09) Captured ONCE, before anything is merged: the whole RUN either started with an
        // empty library (the baseline: nothing is New) or it did not (flag whatever the manifest never had).
        // A per-pass check would be wrong -- account 2's pass already sees account 1's freshly added items.
        bool emptyBefore = manifest.Current.Items.Count == 0;
        if (manifest.Current.Accounts.Count == 0 || sessions is null)
        {
            var svc = new LibrarySyncService(api, manifest) { BaselineScan = emptyBefore };
            host.ConfigureScan(svc, null, 1, 1);
            var single = await svc.RunAsync(ct);
            return new Outcome(single, null) { Dates = await DateAsync(manifest, api, null, host, datePurchases, ct) };
        }
        var accounts = manifest.Current.Accounts;
        var runner = new MultiAccountSync(sessions, manifest)
        {
            Log = s => host.Log(s),
            Configure = (svc, acct) =>
            {
                svc.BaselineScan = emptyBefore;   // (New items 09-09) the RUN's answer, on every account's pass
                int idx = 0;
                for (int i = 0; i < accounts.Count; i++) if (accounts[i].Id == acct.Id) { idx = i + 1; break; }
                host.ConfigureScan(svc, acct, idx, accounts.Count);
            },
        };
        var passes = await runner.RunAsync(ct);
        return new Outcome(SyncResult.Aggregate(passes), passes)
        {
            Dates = await DateAsync(manifest, api, sessions, host, datePurchases, ct),   // every account's orders, each on its own client
        };
    }

    /// <summary>The dates pass, guarded and never fatal: an acquisition date is decoration, so a failure here
    /// is logged and the scan still succeeds. Cancellation is left to propagate -- a cancelled scan is not a
    /// failed one and must not be swallowed into a "dates unavailable" line.</summary>
    private static async Task<PurchaseDatesResult?> DateAsync(IManifestStore manifest, GogApiClient api, AccountSessions? sessions,
                                                              IBackupHost host, bool wanted, CancellationToken ct)
    {
        if (!wanted || ct.IsCancellationRequested) return null;
        try
        {
            var r = sessions is null
                ? await PurchaseDatesRun.RunAsync(manifest, api, ct)
                : await PurchaseDatesRun.RunForAccountsAsync(manifest, PurchaseDatesRun.OrdersOf(sessions), ct);
            if (r.Dated > 0)
                host.Log($"purchase dates: dated {r.Dated} item(s) from order history"
                       + $" ({r.Requests} request(s){(r.Backfilled ? ", first-time backfill" : "")})");
            return r;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            host.Log($"purchase dates: order history did not answer ({Api.GogError.Describe(ex)}); dates unchanged");
            return null;
        }
    }
}

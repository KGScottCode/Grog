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

public static class LibraryScan
{
    public sealed record Outcome(SyncResult Aggregate, IReadOnlyList<AccountSyncPass>? Passes)
    {
        public PurchaseDatesResult? Dates { get; init; }

        public bool AllAccountsFailed => Passes is { Count: > 0 } && Passes.All(p => p.Result is null);
    }

    public static bool IsRecent(LibraryManifest manifest, DateTimeOffset now, TimeSpan window)
        => manifest.LastSyncCompleted is { } t && now - t < window;

    public static Task<SyncResult> RunScopedAsync(IManifestStore manifest, GogApiClient api, IReadOnlyCollection<long> ids,
                                                  IBackupHost host, CancellationToken ct)
    {
        var first = manifest.Current.Accounts.Count > 0 ? manifest.Current.Accounts[0].Id : "";
        var svc = new LibrarySyncService(api, manifest) { AccountId = first };
        host.ConfigureScan(svc, null, 1, 1);
        return svc.RunAsync(ids, ct);
    }

    public static Task<SyncResult> MergeAccountAsync(IManifestStore manifest, GogApiClient api, GrogAccount account,
                                                     IBackupHost host, CancellationToken ct)
    {
        var svc = new LibrarySyncService(api, manifest) { AccountId = account.Id, HostOwnsReconcile = true };
        host.ConfigureScan(svc, account, 0, 0);
        return svc.RunAsync(ct);
    }

    public static async Task<Outcome> RunAsync(IManifestStore manifest, GogApiClient api, AccountSessions? sessions,
                                               IBackupHost host, CancellationToken ct, bool datePurchases = false)
    {
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
                svc.BaselineScan = emptyBefore;   // the RUN's answer, on every account's pass
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

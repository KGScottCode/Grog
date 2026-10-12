// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Auth;
using Grog.Core.Manifest;

namespace Grog.Core.Sync;

public sealed record AccountSyncPass(string AccountId, string Name, bool Skipped, string? SkipReason,
                                     SyncResult? Result, Exception? Error);

public sealed class MultiAccountSync
{
    private readonly AccountSessions _sessions;
    private readonly IManifestStore _manifest;

    public Action<LibrarySyncService, Grog.Core.Models.GrogAccount>? Configure { get; set; }

    public Action<string>? Log { get; set; }

    public bool IncludeCloudSaves { get; set; } = true;

    public MultiAccountSync(AccountSessions sessions, IManifestStore manifest)
    {
        _sessions = sessions;
        _manifest = manifest;
    }

    public async Task<IReadOnlyList<AccountSyncPass>> RunAsync(CancellationToken ct = default)
    {
        var passes = new List<AccountSyncPass>();
        var accounts = new List<Grog.Core.Models.GrogAccount>(_sessions.Accounts);
        foreach (var acct in accounts)
        {
            ct.ThrowIfCancellationRequested();
            var name = string.IsNullOrEmpty(acct.Username) ? (acct.Id.Length == 0 ? "primary" : acct.Id) : acct.Username;

            if (!_sessions.IsConnected(acct.Id))
            {
                Log?.Invoke($"Skipped {name} - signed out");
                passes.Add(new AccountSyncPass(acct.Id, name, Skipped: true, "signed out", null, null));
                continue;
            }

            Log?.Invoke($"Scanning {name}'s library…");
            var svc = new LibrarySyncService(_sessions.ApiFor(acct.Id), _manifest)
                { AccountId = acct.Id, HostOwnsReconcile = true };
            Configure?.Invoke(svc, acct);
            try
            {
                var result = await svc.RunAsync(ct);
                _sessions.MarkAuthOk(acct.Id);
                passes.Add(new AccountSyncPass(acct.Id, name, false, null, result, null));
            }
            catch (OperationCanceledException) { throw; }
            catch (AuthExpiredException ex)
            {
                _sessions.MarkAuthFailed(acct.Id);
                Log?.Invoke($"Skipped {name} - session expired, sign in again from the Accounts page");
                passes.Add(new AccountSyncPass(acct.Id, name, true, "session expired", null, ex));
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Scan failed for {name}: {Api.GogError.Describe(ex)}");
                passes.Add(new AccountSyncPass(acct.Id, name, false, null, null, ex));
            }
        }

        // OWNER NARROWING, per scanned account
        bool changed = false;
        using (_manifest.Gate.Enter())   // the run's whole-library writes, under the manifest gate
        {
        foreach (var p in passes)
            if (p.Result is { } pr)
                changed |= NarrowOwnersForScannedAccount(_manifest.Current, p.AccountId, pr.SeenIds) > 0;

        if (passes.Count > 0 && passes.All(p => p.Result is not null))
        {
            var seen = new HashSet<long>();
            foreach (var p in passes) seen.UnionWith(p.Result!.SeenIds);
            int delisted = LibrarySyncService.ApplyDelistedSweep(_manifest.Current, seen);
            if (delisted > 0) Log?.Invoke($"{delisted} game(s) no longer listed by GOG (kept, flagged at-risk)");

            _manifest.Current.Excluded = passes
                .SelectMany(p => p.Result!.ExcludedProducts)
                .GroupBy(e => e.GogId).Select(g => g.First())
                .OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            changed = true;

        }
        }

        if (IncludeCloudSaves && passes.Any(p => p.Result is not null))
        {
            var (byAccount, complete) = await ListCloudContainersByAccountAsync(ct);
            if (byAccount.Count > 0)
            {
                int games;
                using (_manifest.Gate.Enter()) games = CloudSaves.CloudSaveReconciler.ApplyAccounts(_manifest.Current, byAccount);
                Log?.Invoke($"Cloud saves: {games} game(s) with saves on GOG"
                    + (complete ? "" : " (a signed-out account's saves kept as last seen)"));
                changed = true;
            }
        }
        if (changed) await _manifest.SaveAsync(ct);
        return passes;
    }

    public async Task<(IReadOnlyList<(string AccountId, IReadOnlyList<CloudSaves.CloudContainer> Containers)> ByAccount, bool Complete)>
        ListCloudContainersByAccountAsync(CancellationToken ct = default)
    {
        var result = new List<(string, IReadOnlyList<CloudSaves.CloudContainer>)>();
        bool complete = true;
        foreach (var acct in new List<Grog.Core.Models.GrogAccount>(_sessions.Accounts))
        {
            ct.ThrowIfCancellationRequested();
            var name = string.IsNullOrEmpty(acct.Username) ? (acct.Id.Length == 0 ? "primary" : acct.Id) : acct.Username;
            if (!_sessions.IsConnected(acct.Id)) { complete = false; continue; }
            try
            {
                var cloud = new CloudSaves.CloudSaveService(_sessions.Http, _sessions.AuthFor(acct.Id));
                result.Add((acct.Id, await cloud.ListContainersAsync(ct)));
            }
            catch (OperationCanceledException) { throw; }
            catch (AuthExpiredException)
            {
                _sessions.MarkAuthFailed(acct.Id);
                complete = false;
                Log?.Invoke($"Cloud saves: skipped {name} - session expired");
            }
            catch (Exception ex)
            {
                complete = false;
                Log?.Invoke($"Cloud saves: {name}: {Api.GogError.Describe(ex)}");
            }
        }
        return (result, complete && result.Count > 0);
    }

    public static int NarrowOwnersForScannedAccount(LibraryManifest manifest, string accountId,
                                                    IReadOnlyCollection<long> seenIds)
    {
        int removed = 0;
        foreach (var item in manifest.Items)
        {
            if (seenIds.Contains(item.GogId)) continue;
            foreach (var f in item.Files)
                if (f.OwnerIds.Remove(accountId)) removed++;
        }
        return removed;
    }
}

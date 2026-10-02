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

/// <summary>One account's slice of a multi-account scan: what ran, what it found, or why it didn't.</summary>
public sealed record AccountSyncPass(string AccountId, string Name, bool Skipped, string? SkipReason,
                                     SyncResult? Result, Exception? Error);

/// <summary>
/// Scans ALL connected accounts sequentially (signed-out accounts skip with a log line), one
/// LibrarySyncService per account with AccountId set so owner merge and attribution do the rest.
/// Sequential ON PURPOSE: the per-IP rate budget is one body, and interleaved scans would also
/// interleave their backoff gates.
/// </summary>
public sealed class MultiAccountSync
{
    private readonly AccountSessions _sessions;
    private readonly IManifestStore _manifest;

    /// <summary>Host hook to dress each account's service (art fetch, Discovered preview, Progress
    /// forwarding) before it runs. The GUI wires its usual hooks here; the CLI wires console output.</summary>
    public Action<LibrarySyncService, Grog.Core.Models.GrogAccount>? Configure { get; set; }

    /// <summary>One line per account: "Scanning kevin...", "Skipped kids-account - signed out".</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Cloud-save discovery rides the scan. On by default; a host that must not touch the cloud
    /// endpoints (tests, a scoped tool) turns it off.</summary>
    public bool IncludeCloudSaves { get; set; } = true;

    public MultiAccountSync(AccountSessions sessions, IManifestStore manifest)
    {
        _sessions = sessions;
        _manifest = manifest;
    }

    public async Task<IReadOnlyList<AccountSyncPass>> RunAsync(CancellationToken ct = default)
    {
        var passes = new List<AccountSyncPass>();
        // Snapshot: a pass may save the manifest; iterating the live list while promotion or dedupe
        // mutates it is the kind of surprise this file exists to not have.
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
            // The per-service delisted sweep is OFF: one account's library is not the whole shared
            // library, and each account's sweep would flag every game the others own. ONE sweep runs
            // after the loop, over the union of what every scanned account saw.
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
                // The token file exists but no longer refreshes. ONE account's problem: mark it so
                // the download picker stops offering it this run, say so, carry on with the rest.
                _sessions.MarkAuthFailed(acct.Id);
                Log?.Invoke($"Skipped {name} - session expired, sign in again from the Accounts page");
                passes.Add(new AccountSyncPass(acct.Id, name, true, "session expired", null, ex));
            }
            catch (Exception ex)
            {
                // A mid-scan failure for one account must not abort the others; the pass records it
                // and the host decides how loudly to say it.
                Log?.Invoke($"Scan failed for {name}: {Api.GogError.Describe(ex)}");
                passes.Add(new AccountSyncPass(acct.Id, name, false, null, null, ex));
            }
        }

        // OWNER NARROWING, per scanned account: a COMPLETED full scan that never saw a game means the
        // account is not entitled to it, so the account leaves that game's owner lists. Skipped or failed
        // accounts narrow NOTHING: their entitlements are unknown today, not absent.
        bool changed = false;
        using (_manifest.Gate.Enter())   // the run's whole-library writes, under the manifest gate (09-08)
        {
        foreach (var p in passes)
            if (p.Result is { } pr)
                changed |= NarrowOwnersForScannedAccount(_manifest.Current, p.AccountId, pr.SeenIds) > 0;

        // The run's ONE whole-library reconcile (delisted sweep + Excluded rebuild), over the union of
        // every scanned account's results -- and only when EVERY registered account actually scanned,
        // because "unseen" says nothing when an account's slice of the library was never fetched.
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

        // Cloud saves are per-account facts, never merged. Replacement is per scanned account, so partial
        // knowledge is safe: a signed-out account keeps its section as it was; no full-knowledge gate.
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

    /// <summary>Every connected account's cloud-save containers, kept per account for the reconciler.
    /// Complete = every registered account answered; a partial result is still applied (per-account
    /// replacement), the flag only shapes the log/status line.</summary>
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

    /// <summary>One scanned account's owner narrowing (see RunAsync). Pure and static so the rule is
    /// testable without a network: remove <paramref name="accountId"/> from the owner lists of every
    /// game its completed scan did not see. Returns how many files lost the owner.</summary>
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

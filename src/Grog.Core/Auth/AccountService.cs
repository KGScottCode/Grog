// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;

namespace Grog.Core.Auth;

/// <summary>Manages the set of GOG accounts Grog syncs from. Multiple accounts merge into ONE combined
/// library (items tagged by AccountId); volumes, routing policy, and scope stay GLOBAL. Each account has
/// its own token file (tokens-&lt;id&gt;.json) and no account is special.</summary>
public sealed class AccountService
{
    private readonly IManifestStore _manifest;
    private readonly GrogPaths _paths;

    public AccountService(IManifestStore manifest, GrogPaths paths)
    {
        _manifest = manifest;
        _paths = paths;
    }

    public IReadOnlyList<GrogAccount> Accounts => _manifest.Current.Accounts;

    /// <summary>The token file for an account: tokens-&lt;id&gt;.json, every account alike. An empty id here
    /// is a caller bug -- MigrateLegacyPrimary retires the unnamed slot on load.</summary>
    public string TokenPathFor(string accountId)
    {
        if (string.IsNullOrEmpty(accountId))
            throw new ArgumentException("Account id is required; the legacy unnamed slot was migrated away.", nameof(accountId));
        return Path.Combine(_paths.ConfigDir, $"tokens-{Sanitize(accountId)}.json");
    }

    /// <summary>Migrates the pre-multi-account layout (unnamed account, bare tokens.json) to an ordinary
    /// account: id from username, token file renamed, "" attributions rewritten. Crash-safe order -- COPY the
    /// token, commit the manifest, only then delete the original. Re-entrant across interrupted runs.</summary>
    public async Task<bool> MigrateLegacyPrimaryAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var m = _manifest.Current;
            var legacy = m.Accounts.FirstOrDefault(a => string.IsNullOrEmpty(a.Id));
            var legacyToken = _paths.TokensPath;
            if (legacy is null)
            {
                // Manifest already migrated; a leftover tokens.json is a stale copy from an interrupted
                // run. Finishing the delete is credential cleanup.
                if (File.Exists(legacyToken)
                    && m.Accounts.Any(a => File.Exists(TokenPathFor(a.Id))))
                {
                    try { File.Delete(legacyToken); } catch { }
                }
                // Heal any stray "" attributions: they belong to the first account, same as the
                // migration itself would have assigned.
                bool healed;
                using (_manifest.Gate.Enter())   // (manifest gate 09-08)
                    healed = m.Accounts.FirstOrDefault()?.Id is { Length: > 0 } firstId && RewriteEmptyAttributions(m, firstId);
                if (healed) await _manifest.SaveAsync(ct);
                return false;
            }

            var id = Sanitize(string.IsNullOrWhiteSpace(legacy.Username) ? "primary" : legacy.Username);
            if (id.Length == 0) id = "primary";
            while (m.Accounts.Any(a => a.Id == id)) id += "1";

            var newToken = Path.Combine(_paths.ConfigDir, $"tokens-{id}.json");
            if (File.Exists(legacyToken) && !File.Exists(newToken))
                File.Copy(legacyToken, newToken);

            using (_manifest.Gate.Enter())   // (manifest gate 09-08)
            {
                legacy.Id = id;
                RewriteEmptyAttributions(m, id);
            }

            await _manifest.SaveAsync(ct);
            try { if (File.Exists(legacyToken)) File.Delete(legacyToken); } catch { /* retried next launch */ }
            return true;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Register a new account (or update an existing one's display fields). Id should be a
    /// short stable slug the user picks (e.g. "main", "kids"); we sanitize it for filesystem use.</summary>
    public async Task<GrogAccount> AddOrUpdateAsync(string id, string username, string login, CancellationToken ct = default)
    {
        // STATIC gate, because callers construct a NEW AccountService per call -- an instance lock would
        // guard nothing.
        await Gate.WaitAsync(ct);
        try
        {
            var acct = AddOrUpdateCore(id, username, login);
            await _manifest.SaveAsync(ct);
            return acct;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Serializes registration across ALL AccountService instances. A check-then-act across an await
    /// is not atomic: the gate must be held across the whole read-modify-save or overlapping registrations
    /// produce duplicate account rows.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static bool RewriteEmptyAttributions(Grog.Core.Manifest.LibraryManifest m, string id)
    {
        var changed = false;
        foreach (var item in m.Items)
            changed |= RewriteItem(item, id);
        return changed;
    }

    private static bool RewriteItem(Models.LibraryItem item, string id)
    {
        var changed = false;
        if (item.AccountId == "") { item.AccountId = id; changed = true; }
        foreach (var f in item.Files)
            for (int i = 0; i < f.OwnerIds.Count; i++)
                if (f.OwnerIds[i] == "") { f.OwnerIds[i] = id; changed = true; }
        foreach (var c in item.CloudByAccount)
            if (c.AccountId == "") { c.AccountId = id; changed = true; }
        // Dlcs holds nested items with the same attribution fields; a sweep that skips them leaves
        // exactly the strays it exists to heal.
        foreach (var d in item.Dlcs)
            changed |= RewriteItem(d, id);
        return changed;
    }

    private GrogAccount AddOrUpdateCore(string id, string username, string login)
    {
        id = Sanitize(id);
        // No unnamed slot: registering without an id derives one from the username, so every account is
        // ordinary from the moment it exists.
        if (id.Length == 0) id = Sanitize(username);
        if (id.Length == 0) throw new ArgumentException("An account needs an id or a username.", nameof(id));
        // Username is the identity; the id is just its filesystem-safe form. Matching id first covers a
        // rename, the username fallback covers the same person arriving under a differently-derived id.
        using (_manifest.Gate.Enter())   // (manifest gate 09-08)
        {
            _manifest.Gate.AssertHeld();
            var existing = _manifest.Current.Accounts.FirstOrDefault(a => a.Id == id)
                        ?? (string.IsNullOrWhiteSpace(username) ? null
                            : _manifest.Current.Accounts.FirstOrDefault(
                                a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)));
            if (existing is null)
            {
                existing = new GrogAccount { Id = id, Username = username, Login = login };
                _manifest.Current.Accounts.Add(existing);
            }
            else
            {
                existing.Username = username;
                existing.Login = login;
            }
            return existing;
        }
    }

    /// <summary>Collapses rows that are the same GOG account (username is the identity, case-insensitive) and
    /// reports how many went. The survivor is the row whose token file exists, ties to earliest registration;
    /// item attribution is preserved.</summary>
    public async Task<int> DedupeAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var accounts = _manifest.Current.Accounts;
            var removed = 0;

            foreach (var group in accounts
                         .Where(a => !string.IsNullOrWhiteSpace(a.Username))
                         .GroupBy(a => a.Username!.Trim(), StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1)
                         .ToList())
            {
                // The survivor is the row whose token file actually exists (that is the login the app can
                // still use); ties go to the earliest registration.
                var keep = group.FirstOrDefault(a => !string.IsNullOrEmpty(a.Id) && File.Exists(TokenPathFor(a.Id)))
                           ?? group.First();
                foreach (var dup in group.Where(a => !ReferenceEquals(a, keep)).ToList())
                {
                    using (_manifest.Gate.Enter()) accounts.Remove(dup);   // (manifest gate 09-08)
                    removed++;
                }
            }

            if (removed > 0) await _manifest.SaveAsync(ct);
            return removed;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Log OUT of an account: forget its stored token so it stops syncing until the user signs
    /// back in. The registration, its library items and every downloaded byte stay -- Remove is the one
    /// that unregisters. Returns true when a token was actually forgotten.</summary>
    public bool LogOut(string id)
    {
        id = Sanitize(id);
        try
        {
            var tokenPath = TokenPathFor(id);
            if (File.Exists(tokenPath)) { File.Delete(tokenPath); return true; }
        }
        catch { /* best-effort; a locked file just means the button reads Log Out a little longer */ }
        return false;
    }

    /// <summary>Removes an account: stops future syncs and deletes its token file, but NEVER deletes its
    /// already-synced library items (owned backups; they keep their AccountId tag as attribution).
    /// No promotion: every account runs on its own token file, so the rest keep syncing unchanged.</summary>
    public async Task<bool> RemoveAsync(string id, CancellationToken ct = default)
    {
        id = Sanitize(id);
        using (_manifest.Gate.Enter())   // (manifest gate 09-08)
        {
            _manifest.Gate.AssertHeld();
            var acct = _manifest.Current.Accounts.FirstOrDefault(a => a.Id == id);
            if (acct is null) return false;
            _manifest.Current.Accounts.Remove(acct);
        }

        try
        {
            var tokenPath = TokenPathFor(id);
            if (File.Exists(tokenPath)) File.Delete(tokenPath);
        }
        catch { /* best-effort token cleanup */ }

        await _manifest.SaveAsync(ct);
        return true;
    }


    /// <summary>Per-account item counts for display. OWNERS-BASED: a co-owned game counts for every owner
    /// (DOOM is in both accounts' libraries and both cards should say so), so the columns can sum to more
    /// than the library total -- cards answer "how many games does THIS account have", not "who contributed
    /// what". Falls back to legacy AccountId via the OwnerIds union's own fallback.</summary>
    public Dictionary<string, int> ItemCountsByAccount()
    {
        var counts = new Dictionary<string, int>();
        foreach (var item in _manifest.Current.Items)
            foreach (var owner in item.OwnerIds)
                counts[owner] = counts.TryGetValue(owner, out var n) ? n + 1 : 1;
        return counts;
    }

    /// <summary>Ids become FILENAMES (tokens-&lt;id&gt;.json) and the config can travel between case-insensitive
    /// and case-sensitive disks; lowercasing at the gate keeps one identity = one file on every OS.</summary>
    private static string Sanitize(string id)
    {
        var cleaned = (id ?? "").Trim().ToLowerInvariant();
        foreach (var c in Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(c, '_');
        return cleaned.Replace(' ', '_');
    }
}

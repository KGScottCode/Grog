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

    public string TokenPathFor(string accountId)
    {
        if (string.IsNullOrEmpty(accountId))
            throw new ArgumentException("Account id is required; the legacy unnamed slot was migrated away.", nameof(accountId));
        return Path.Combine(_paths.ConfigDir, $"tokens-{Sanitize(accountId)}.json");
    }

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
                if (File.Exists(legacyToken)
                    && m.Accounts.Any(a => File.Exists(TokenPathFor(a.Id))))
                {
                    try { File.Delete(legacyToken); } catch { }
                }
                bool healed;
                using (_manifest.Gate.Enter())   // 
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

            using (_manifest.Gate.Enter())   // 
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

    public async Task<GrogAccount> AddOrUpdateAsync(string id, string username, string login, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var acct = AddOrUpdateCore(id, username, login);
            await _manifest.SaveAsync(ct);
            return acct;
        }
        finally { Gate.Release(); }
    }

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
        foreach (var d in item.Dlcs)
            changed |= RewriteItem(d, id);
        return changed;
    }

    private GrogAccount AddOrUpdateCore(string id, string username, string login)
    {
        id = Sanitize(id);
        if (id.Length == 0) id = Sanitize(username);
        if (id.Length == 0) throw new ArgumentException("An account needs an id or a username.", nameof(id));
        using (_manifest.Gate.Enter())   // 
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
                var keep = group.FirstOrDefault(a => !string.IsNullOrEmpty(a.Id) && File.Exists(TokenPathFor(a.Id)))
                           ?? group.First();
                foreach (var dup in group.Where(a => !ReferenceEquals(a, keep)).ToList())
                {
                    using (_manifest.Gate.Enter()) accounts.Remove(dup);   // 
                    removed++;
                }
            }

            if (removed > 0) await _manifest.SaveAsync(ct);
            return removed;
        }
        finally { Gate.Release(); }
    }

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

    public async Task<bool> RemoveAsync(string id, CancellationToken ct = default)
    {
        id = Sanitize(id);
        using (_manifest.Gate.Enter())   // 
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


    public Dictionary<string, int> ItemCountsByAccount()
    {
        var counts = new Dictionary<string, int>();
        foreach (var item in _manifest.Current.Items)
            foreach (var owner in item.OwnerIds)
                counts[owner] = counts.TryGetValue(owner, out var n) ? n + 1 : 1;
        return counts;
    }

    private static string Sanitize(string id)
    {
        var cleaned = (id ?? "").Trim().ToLowerInvariant();
        foreach (var c in Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(c, '_');
        return cleaned.Replace(' ', '_');
    }
}

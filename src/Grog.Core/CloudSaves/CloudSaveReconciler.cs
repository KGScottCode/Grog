// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.CloudSaves;

/// <summary>Pure reconcile of GOG cloud-storage containers onto the manifest -- no HTTP, no save --
/// so the GUI and CLI drive identical discovery and the mapping is unit-testable.</summary>
public static class CloudSaveReconciler
{
    /// <summary>Flag which library items have cloud saves (caching size + space id) and clear the flag
    /// on the rest. Returns display rows for matched items plus the container product ids that aren't in
    /// the library (their titles are fetched separately, so cold-start discovery still works).</summary>
    public static (List<CloudSaveRow> Rows, List<long> UnmatchedIds) Apply(
        LibraryManifest manifest, IReadOnlyList<CloudContainer> containers)
    {
        // The legacy single-session path: everything it sees belongs to the tokens.json slot ("").
        ApplyAccounts(manifest, new[] { ("", containers) });
        var known = new HashSet<long>(manifest.Items.Select(i => i.GogId));
        var rows = new List<CloudSaveRow>();
        var unmatched = new List<long>();
        foreach (var c in containers)
        {
            var item = manifest.ItemById(c.ProductId);
            if (item is null) { unmatched.Add(c.ProductId); continue; }
            rows.Add(new CloudSaveRow(item.GogId, item.Title, item.CloudClientId, c.SizeBytes,
                item.CloudLastBackup, item.CloudLocalSaveCount, item.CloudBackedUpSize,
                c.Files, c.UpdatedUtc, item.CloudBackedUpFiles, item.CloudBackedUpChangeUtc));
        }
        return (rows, unmatched);
    }

    /// <summary>Per-account reconcile (saves are per-account facts, never merged): each SCANNED account's
    /// entries are replaced from its containers; unscanned accounts keep theirs, so a partial check is safe.
    /// Entries stay in registration order; the legacy Cloud* fields re-mirror from the first entry.</summary>
    public static int ApplyAccounts(LibraryManifest manifest,
        IReadOnlyList<(string AccountId, IReadOnlyList<CloudContainer> Containers)> byAccount)
    {
        SeedLegacy(manifest);
        var scanned = new HashSet<string>(byAccount.Select(x => x.AccountId));
        var byProduct = new Dictionary<(long, string), CloudContainer>();
        foreach (var (acct, containers) in byAccount)
            foreach (var c in containers)
                byProduct[(c.ProductId, acct)] = c;

        // Registration order for stable entry ordering (mirror = first entry).
        var order = new Dictionary<string, int>();
        for (int i = 0; i < manifest.Accounts.Count; i++) order[manifest.Accounts[i].Id] = i;
        int Rank(string id) => order.TryGetValue(id, out var r) ? r : int.MaxValue;

        int games = 0;
        foreach (var it in manifest.Items)
        {
            // Server-side facts are REPLACED per scanned account; local backup BOOKKEEPING carries over
            // (the archive on disk did not change because GOG answered a listing call).
            var old = it.CloudByAccount.Where(e => scanned.Contains(e.AccountId)).ToDictionary(e => e.AccountId);
            it.CloudByAccount.RemoveAll(e => scanned.Contains(e.AccountId));
            foreach (var (acct, _) in byAccount)
                if (byProduct.TryGetValue((it.GogId, acct), out var c))
                {
                    old.TryGetValue(acct, out var prev);
                    it.CloudByAccount.Add(new CloudAccountSave
                    {
                        AccountId = acct, SizeBytes = c.SizeBytes, Files = c.Files,
                        UpdatedUtc = c.UpdatedUtc, SpaceId = c.SpaceId,
                        LastBackup = prev?.LastBackup, LocalSaveCount = prev?.LocalSaveCount ?? 0,
                        BackedUpSize = prev?.BackedUpSize ?? 0, BackedUpFiles = prev?.BackedUpFiles ?? 0,
                        BackedUpChangeUtc = prev?.BackedUpChangeUtc,
                    });
                }
            it.CloudByAccount = it.CloudByAccount.OrderBy(e => Rank(e.AccountId)).ToList();

            if (MirrorLegacy(it)) games++;
        }
        return games;
    }

    /// <summary>Gets (or adds) one account's cloud entry -- THE write path outside a reconcile: update the
    /// entry, then <see cref="MirrorLegacy"/>; never write the legacy single-slot fields directly.</summary>
    public static CloudAccountSave Entry(LibraryItem it, string accountId)
    {
        var e = it.CloudByAccount.FirstOrDefault(c => c.AccountId == accountId);
        if (e is null) { e = new CloudAccountSave { AccountId = accountId }; it.CloudByAccount.Add(e); }
        return e;
    }

    /// <summary>Re-derives one account's local backup bookkeeping after pruning; an emptied history also
    /// clears the backed-up fingerprint so the game honestly reads "not backed up". Re-mirrors legacy fields.</summary>
    public static void RecordLocalSaves(LibraryItem it, string accountId, int count, DateTimeOffset? newest)
    {
        var e = Entry(it, accountId);
        e.LocalSaveCount = count;
        e.LastBackup = count > 0 ? newest : null;
        if (count == 0)
        {
            e.BackedUpSize = 0;
            e.BackedUpFiles = 0;
            e.BackedUpChangeUtc = null;
        }
        MirrorLegacy(it);
    }

    /// <summary>Re-mirrors the legacy single-slot Cloud* fields from the first per-account entry (HasCloudSaves
    /// = any); no entries drops the cached cloud identity too. Returns whether saves remain anywhere.</summary>
    public static bool MirrorLegacy(LibraryItem it)
    {
        var first = it.CloudByAccount.FirstOrDefault();
        it.HasCloudSaves = first is not null;
        if (first is null)
        {
            it.CloudClientId = "";
            it.CloudSaveSize = 0;
            it.CloudSaveFiles = 0;
            it.CloudLatestChangeUtc = null;
            it.CloudSpaceId = "";
            return false;
        }
        it.CloudSaveSize = first.SizeBytes;
        it.CloudSaveFiles = first.Files;
        it.CloudLatestChangeUtc = first.UpdatedUtc;
        it.CloudSpaceId = first.SpaceId;
        it.CloudLastBackup = first.LastBackup;
        it.CloudLocalSaveCount = first.LocalSaveCount;
        it.CloudBackedUpSize = first.BackedUpSize;
        it.CloudBackedUpFiles = first.BackedUpFiles;
        it.CloudBackedUpChangeUtc = first.BackedUpChangeUtc;
        return true;
    }

    /// <summary>Migrates pre-account manifests: a single-slot-flagged game seeds one entry under the first
    /// registered account. Idempotent -- fires only while the per-account list is empty, never with no accounts.</summary>
    public static void SeedLegacy(LibraryManifest manifest)
    {
        var owner = manifest.Accounts.FirstOrDefault()?.Id;
        if (string.IsNullOrEmpty(owner)) return;
        foreach (var it in manifest.Items)
            if ((it.HasCloudSaves ?? false) && it.CloudByAccount.Count == 0)
                it.CloudByAccount.Add(new CloudAccountSave
                {
                    AccountId = owner, SizeBytes = it.CloudSaveSize, Files = it.CloudSaveFiles,
                    UpdatedUtc = it.CloudLatestChangeUtc, SpaceId = it.CloudSpaceId,
                    // The legacy archive folder belongs to the tokens.json slot; its bookkeeping moves in.
                    LastBackup = it.CloudLastBackup, LocalSaveCount = it.CloudLocalSaveCount,
                    BackedUpSize = it.CloudBackedUpSize, BackedUpFiles = it.CloudBackedUpFiles,
                    BackedUpChangeUtc = it.CloudBackedUpChangeUtc,
                });
    }

    /// <summary>Folder name an account's saves live under: its username, filesystem-safe and lowercase
    /// (same rule as token filenames, so Windows and Linux agree on one folder).</summary>
    public static string AccountDirName(LibraryManifest manifest, string accountId)
    {
        var a = manifest.Accounts.FirstOrDefault(x => x.Id == accountId);
        var name = a is not null && !string.IsNullOrEmpty(a.Username) ? a.Username : accountId;
        var cleaned = name.Trim().ToLowerInvariant();
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(c, '_');
        return cleaned.Replace(' ', '_');
    }

    /// <summary>One account's cloud-save area: its CloudSavesFolder override when set, else
    /// &lt;base&gt;/&lt;account name&gt;/. Game folders nest inside, so two accounts' saves can never mix.</summary>
    public static string AccountArchiveDir(LibraryManifest manifest, string cloudBaseDir, string accountId)
    {
        var a = manifest.Accounts.FirstOrDefault(x => x.Id == accountId);
        if (a is not null && !string.IsNullOrEmpty(a.CloudSavesFolder)) return a.CloudSavesFolder;
        return System.IO.Path.Combine(cloudBaseDir, AccountDirName(manifest, accountId));
    }

    /// <summary>One game's archive folder for one account. The first registered account reads the pre-account
    /// flat layout (&lt;base&gt;/&lt;game&gt;) when an archive exists only there, so existing bytes stay live
    /// in place; everything new writes under the account folder. A per-account override skips the fallback.</summary>
    public static string GameArchiveDir(LibraryManifest manifest, string cloudBaseDir, string accountId, string gameSlug)
    {
        var a = manifest.Accounts.FirstOrDefault(x => x.Id == accountId);
        bool overridden = a is not null && !string.IsNullOrEmpty(a.CloudSavesFolder);
        bool isFirst = a is not null && ReferenceEquals(manifest.Accounts.FirstOrDefault(), a);
        var dir = System.IO.Path.Combine(AccountArchiveDir(manifest, cloudBaseDir, accountId), gameSlug);
        if (!overridden && isFirst
            && !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "cloud-saves"))
            && System.IO.Directory.Exists(System.IO.Path.Combine(cloudBaseDir, gameSlug, "cloud-saves")))
            return System.IO.Path.Combine(cloudBaseDir, gameSlug);
        return dir;
    }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Sync;

/// <summary>
/// Removing items from TRACKING -- the manifest record only. Bytes on disk are never touched (the
/// never-delete rule); a rescan by an owning account re-adds anything removed, and Hide is the
/// mechanism for keeping a game off the grid permanently. Three entry points share this one
/// operation: account removal (sole-owned items leave with the account), per-item Remove on the
/// grid, and the Settings "fresh library" reset. Callers own saving the manifest and refreshing.
/// </summary>
public static class LibraryUntrack
{
    /// <summary>Items that would leave the library if <paramref name="accountId"/> were removed:
    /// every item whose ONLY owner is that account. Preview for the remove-account dialog.</summary>
    public static List<LibraryItem> SoleOwnedBy(LibraryManifest manifest, string accountId)
        => manifest.Items.Where(i => i.OwnerIds.Count == 1 && i.OwnerIds[0] == accountId).ToList();

    /// <summary>
    /// Strip <paramref name="accountId"/> from every file's owner list. A file left with no owner is
    /// removed from tracking (no remaining account is entitled to fetch it), and an item left with no
    /// files leaves the library, its queue entries purged. The legacy item-level AccountId attribution
    /// is blanked where it names the removed account, so the load-time owner seeding can never
    /// resurrect the removed ownership. The account's per-item cloud-save state goes with it, and a
    /// FILE-LESS item attributed only to the account (what SoleOwnedBy previews) leaves too. Returns the
    /// removed items.
    /// </summary>
    public static List<LibraryItem> RemoveOwner(LibraryManifest manifest, string accountId)
    {
        var removedItems = new List<LibraryItem>();
        foreach (var item in manifest.Items)
        {
            bool hadFiles = item.Files.Count > 0;
            // Only prune files that HAD the owner and lost their last one -- an already-empty owner
            // list (pre-migration data not yet load-seeded) says "unknown", not "owned by nobody".
            var orphaned = new List<GameFile>();
            foreach (var f in item.Files)
                if (f.OwnerIds.Remove(accountId) && f.OwnerIds.Count == 0) orphaned.Add(f);
            foreach (var f in item.OldVersionFiles) f.OwnerIds.Remove(accountId);
            // Per-account cloud-save state goes with the account (a dangling entry drew a Cloud Saves row
            // for an account that no longer exists).
            // The legacy single-slot mirror goes with it. Left standing (HasCloudSaves true over an empty list),
            // the next reconcile's SeedLegacy read it as a pre-account manifest and re-seeded the removed
            // account's saves under whoever is first NOW (sweep 2 #31).
            if (item.CloudByAccount.RemoveAll(c => c.AccountId == accountId) > 0)
                Grog.Core.CloudSaves.CloudSaveReconciler.MirrorLegacy(item);
            // A FILE-LESS item owned by attribution alone (the OwnerIds fallback names its AccountId) is
            // sole-owned in the preview and must leave here too, or the dialog's count lies.
            bool attributedOnly = !hadFiles && item.AccountId == accountId;
            if (item.AccountId == accountId) item.AccountId = "";
            foreach (var f in orphaned) item.Files.Remove(f);
            if ((hadFiles && item.Files.Count == 0) || attributedOnly) removedItems.Add(item);
        }
        if (removedItems.Count > 0)
        {
            var gone = new HashSet<long>(removedItems.Select(i => i.GogId));
            manifest.Items.RemoveAll(i => gone.Contains(i.GogId));
            manifest.ItemsChanged();   // a same-Count remove+add elsewhere must not resolve a removed item
            PurgeQueue(manifest, gone);
        }
        return removedItems;
    }

    /// <summary>Remove the named items from tracking and purge their queue entries. Returns how many
    /// items were actually removed.</summary>
    public static int RemoveItems(LibraryManifest manifest, IReadOnlyCollection<long> gogIds)
    {
        var set = new HashSet<long>(gogIds);
        MarkRootsHoldingBytes(manifest, manifest.Items.Where(i => set.Contains(i.GogId)));
        int removed = manifest.Items.RemoveAll(i => set.Contains(i.GogId));
        if (removed > 0) { manifest.ItemsChanged(); PurgeQueue(manifest, set); }
        return removed;
    }

    /// <summary>The fresh-library reset: everything LIBRARY-DERIVED leaves the manifest -- items, queue,
    /// scan history, excluded products, cloud view state and every sync stamp -- so the next scan is a
    /// true first-run rebuild. Accounts (registration + tokens), storage roots, routing, scope, schedule
    /// and all bytes on disk stay; the verify pass re-adopts whatever is already downloaded.</summary>
    public static int RemoveAllItems(LibraryManifest manifest)
    {
        int n = manifest.Items.Count;
        manifest.Items.Clear();
        manifest.ItemsChanged();
        manifest.Downloads.Clear();
        manifest.SyncLog.Clear();
        manifest.Excluded.Clear();
        manifest.CloudCollapsedAccounts.Clear();
        // Sync stamps describe items that no longer exist; a recent one would let the backup verb
        // skip its catalog refresh ("synced recently") and report an empty library as backed up.
        manifest.LastSyncCompleted = null;
        foreach (var a in manifest.Accounts) a.LastSync = null;
        // The bytes stay on every drive and the records that pointed at them are gone: each root owes an
        // import again, or the rebuild scan lists the whole library as not downloaded and the next run
        // fetches it all a second time (sweep 2 #12). PendingImports runs it once the scan has items.
        foreach (var r in manifest.Roots) Grog.Core.Verify.PendingImports.Mark(r);
        return n;
    }

    /// <summary>The bytes stay on disk when their records leave: every root that holds some owes an import again,
    /// or a re-add of the game lists it as not downloaded and the next run fetches it a second time (the reset
    /// already did this for every root; the per-item remove did not).</summary>
    private static void MarkRootsHoldingBytes(LibraryManifest manifest, IEnumerable<LibraryItem> leaving)
    {
        var rootIds = leaving.SelectMany(i => i.Files.Concat(i.OldVersionFiles))
                             .Where(BackupScope.HoldsBytes)
                             .Select(manifest.EffectiveRootId)
                             .Where(id => !string.IsNullOrEmpty(id))
                             .ToHashSet(StringComparer.Ordinal);
        foreach (var r in manifest.Roots.Where(r => rootIds.Contains(r.Id))) Grog.Core.Verify.PendingImports.Mark(r);
    }

    private static void PurgeQueue(LibraryManifest manifest, HashSet<long> gogIds)
    {
        foreach (var q in manifest.Downloads.Snapshot())
            if (gogIds.Contains(q.GogId))
                manifest.Downloads.Remove(q.GogId, q.FileKey);
    }
}

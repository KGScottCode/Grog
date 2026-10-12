// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Sync;

public static class LibraryUntrack
{
    public static List<LibraryItem> SoleOwnedBy(LibraryManifest manifest, string accountId)
        => manifest.Items.Where(i => i.OwnerIds.Count == 1 && i.OwnerIds[0] == accountId).ToList();

    public static List<LibraryItem> RemoveOwner(LibraryManifest manifest, string accountId)
    {
        var removedItems = new List<LibraryItem>();
        foreach (var item in manifest.Items)
        {
            bool hadFiles = item.Files.Count > 0;
            var orphaned = new List<GameFile>();
            foreach (var f in item.Files)
                if (f.OwnerIds.Remove(accountId) && f.OwnerIds.Count == 0) orphaned.Add(f);
            foreach (var f in item.OldVersionFiles) f.OwnerIds.Remove(accountId);
            if (item.CloudByAccount.RemoveAll(c => c.AccountId == accountId) > 0)
                Grog.Core.CloudSaves.CloudSaveReconciler.MirrorLegacy(item);
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

    public static int RemoveItems(LibraryManifest manifest, IReadOnlyCollection<long> gogIds)
    {
        var set = new HashSet<long>(gogIds);
        MarkRootsHoldingBytes(manifest, manifest.Items.Where(i => set.Contains(i.GogId)));
        int removed = manifest.Items.RemoveAll(i => set.Contains(i.GogId));
        if (removed > 0) { manifest.ItemsChanged(); PurgeQueue(manifest, set); }
        return removed;
    }

    public static int RemoveAllItems(LibraryManifest manifest)
    {
        int n = manifest.Items.Count;
        manifest.Items.Clear();
        manifest.ItemsChanged();
        manifest.Downloads.Clear();
        manifest.SyncLog.Clear();
        manifest.Excluded.Clear();
        manifest.CloudCollapsedAccounts.Clear();
        manifest.LastSyncCompleted = null;
        foreach (var a in manifest.Accounts) a.LastSync = null;
        foreach (var r in manifest.Roots) Grog.Core.Verify.PendingImports.Mark(r);
        return n;
    }

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

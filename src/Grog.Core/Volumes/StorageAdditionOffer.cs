// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;

namespace Grog.Core.Volumes;

public static class StorageAdditionOffer
{
    public sealed record Result(
        int QueuedToNewCount, long QueuedToNewBytes,
        int PrimaryFileCount, long PrimaryBytes,
        IReadOnlyList<(LibraryItem Item, GameFile File)> AllPrimaryFiles,
        IReadOnlyList<(LibraryItem Item, GameFile File)> SplitGameFiles,
        int SplitGameCount, long SplitGameBytes, int ExtrasHeldBackByPin)
    {
        public bool HasSplitGames => SplitGameCount > 0;
    }

    public static bool ShouldOffer(LibraryManifest m, string newRootId)
        => m.Downloads.Snapshot().Any(q => q.Fits && q.TargetRootId == newRootId);

    public static Result Compute(LibraryManifest m, string newRootId, string oldPrimaryRootId)
    {
        var byId = m.Items.ToDictionary(i => i.GogId);
        int qn = 0; long qbytes = 0;
        var gamesQueuedToNew = new HashSet<long>();
        foreach (var q in m.Downloads.Snapshot())
        {
            if (!q.Fits || q.TargetRootId != newRootId) continue;
            if (!byId.TryGetValue(q.GogId, out var item)) continue;
            var f = item.Files.FirstOrDefault(x => x.FileKey == q.FileKey);
            if (f is null) continue;
            qn++;
            qbytes += Math.Max(0L, (f.ExpectedSizeBytes ?? 0L) - (f.HasPartial ? f.PartialBytes ?? 0L : 0L));
            gamesQueuedToNew.Add(q.GogId);
        }

        bool OnOldPrimary(GameFile f) => BackupScope.IsPresent(f) && (f.RootId ?? oldPrimaryRootId) == oldPrimaryRootId;
        var all = new List<(LibraryItem, GameFile)>();
        long allBytes = 0;
        foreach (var item in m.Items)
            foreach (var f in item.Files.Concat(item.OldVersionFiles))
                if (OnOldPrimary(f)) { all.Add((item, f)); allBytes += Rollups.HeldBytesOf(f); }

        bool extrasPinnedElsewhere = m.ExtrasLayout != ExtrasPlacement.WithGame
            && m.Routing.RoleRoots.TryGetValue(ContentRole.Extras, out var extrasRoot)
            && !string.IsNullOrEmpty(extrasRoot) && extrasRoot != newRootId;
        var split = new List<(LibraryItem, GameFile)>();
        long splitBytes = 0; int heldBack = 0;
        var splitGames = new HashSet<long>();
        foreach (var (item, f) in all)
        {
            if (!gamesQueuedToNew.Contains(item.GogId)) continue;
            splitGames.Add(item.GogId);
            if (extrasPinnedElsewhere && f.Kind == FileKind.Extra) { heldBack++; continue; }
            split.Add((item, f)); splitBytes += Rollups.HeldBytesOf(f);
        }

        return new Result(qn, qbytes, all.Count, allBytes, all, split, splitGames.Count, splitBytes, heldBack);
    }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;

namespace Grog.Core.Volumes;

/// <summary>
/// The numbers behind the "New storage added" offer (owner, 09-13). When a second storage is added while the
/// queue holds files that had nowhere to go, the queue is re-planned onto the new drive and the user is asked
/// ONE question about the files already on the old primary: leave them, move only the games that will
/// otherwise end up split across two drives, or move everything and make the new drive primary.
///
/// Pure: reads the manifest, touches no disk. The App runs the chosen move through the existing move engine
/// (ReorgPlanner.ForFileMoves), which honours the folder SHAPE for every file it lands. The STORAGE half of
/// the extras decision (Routing.RoleRoots[Extras]) is honoured here: a pinned extra is not offered for the
/// per-game move, because that move must not silently undo where the user said extras live.
/// </summary>
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

    /// <summary>True when at least one queued row now fits on the new root: the question exists only then.</summary>
    public static bool ShouldOffer(LibraryManifest m, string newRootId)
        => m.Downloads.Snapshot().Any(q => q.Fits && q.TargetRootId == newRootId);

    public static Result Compute(LibraryManifest m, string newRootId, string oldPrimaryRootId)
    {
        // N / X: what will download onto the new drive, priced at what it still needs (as the planner does).
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

        // M / Y: what is present on the old primary, the Old Versions archive included (it travels with its file).
        bool OnOldPrimary(GameFile f) => BackupScope.IsPresent(f) && (f.RootId ?? oldPrimaryRootId) == oldPrimaryRootId;
        var all = new List<(LibraryItem, GameFile)>();
        long allBytes = 0;
        foreach (var item in m.Items)
            foreach (var f in item.Files.Concat(item.OldVersionFiles))
                if (OnOldPrimary(f)) { all.Add((item, f)); allBytes += Rollups.HeldBytesOf(f); }

        // K: games that will have files on BOTH drives once the queue lands, and the per-game move that keeps
        // each of them whole. Extras pinned to another root by the placement setting are counted, not moved.
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

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Volumes;

namespace Grog.Core.Runs;

/// <summary>The queue-order moves the Downloading pane offers around the won't-fit line (09-16), as PURE
/// functions over a queue snapshot: where "the bottom of the fitting block" is, whether one flagged file could
/// be lifted, and the greedy lift of every flagged file that fits. The App only renders and commits. Every rule
/// here is the planner's own (<see cref="PlacementPlanner.WouldFit"/>), so a move is never offered that the
/// next re-plan would undo.</summary>
public static class QueueShaper
{
    /// <summary>The slot right after the LAST fitting row (ignoring <paramref name="moving"/>): where a drop on
    /// the divider, "Pull up" and "Pull up what fits" land. Not "the first red slot": fitting and red rows
    /// interleave once anything is dragged, and the drawn white group lists every fitting row in queue order,
    /// so a row put above the first red file drew with white rows still under it (owner-measured 09-16).
    /// The index is for <see cref="DownloadQueue.MoveTo"/>, which removes the row first. -1 when nothing fits.</summary>
    public static int EndOfFittingBlock(IReadOnlyList<QueuedDownload> snapshot, (long GogId, string FileKey)? moving = null)
    {
        int last = -1, i = 0;
        foreach (var q in snapshot)
        {
            if (moving is { } m && q.GogId == m.GogId && q.FileKey == m.FileKey) continue;
            if (q.Fits) last = i;
            i++;
        }
        return last < 0 ? -1 : last + 1;
    }

    /// <summary>Could this file land somewhere with the room left after every fitting file has its own?
    /// A fitting file always can (it already does).</summary>
    public static bool CouldFit(LibraryManifest m, GameFile f, IReadOnlyList<DeviceSpace> roomInFillOrder)
        => m.PrimaryRootId is { } primary
           && PlacementPlanner.WouldFit(PlacementPlanner.ToPlace(f), roomInFillOrder, m.Routing, primary, m.Routing.Mode, m.ExtrasLayout) is not null;

    /// <summary>Why a won't-fit file has nowhere to land: null = room, or a drive name such as "FAT32" when a per-file
    /// ceiling is what stops it (09-19). True of a file that is over the ceiling of every online drive, or of one
    /// that an online drive has the room for and still cannot hold. A label on the row, never a placement input.</summary>
    public static string? FileLimitBlocking(GameFile f, IReadOnlyList<DeviceSpace> roomInFillOrder)
    {
        var place = PlacementPlanner.ToPlace(f);
        var online = roomInFillOrder.Where(d => d.IsOnline).ToList();
        var over = online.Where(d => PlacementPlanner.OverCeiling(place, d.MaxFileBytes)).ToList();
        if (over.Count == 0) return null;
        var blocked = over.FirstOrDefault(d => d.FreeBytes >= d.Charge(place.Bytes));
        if (blocked is null && over.Count < online.Count) return null;   // a drive without the ceiling is simply full
        return Grog.Core.Storage.DriveResolver.FileLimitName((blocked ?? over[0]).MaxFileBytes);
    }

    /// <summary>Greedy lift: walk the flagged rows in queue order, and every one that fits in the room left
    /// (spent device by device the way the planner spends it) moves, in that order, to right after the last
    /// fitting row. Returns the new order and the keys that moved; empty when nothing fits.</summary>
    public static (List<(long GogId, string FileKey)> Order, List<(long GogId, string FileKey)> Pulled) PullUpWhatFits(
        LibraryManifest m, IReadOnlyList<QueuedDownload> snapshot, IReadOnlyList<DeviceSpace> roomInFillOrder)
    {
        var order = snapshot.Select(q => (q.GogId, q.FileKey)).ToList();
        var pulled = new List<(long, string)>();
        int lastFit = snapshot.ToList().FindLastIndex(q => q.Fits);
        if (lastFit < 0 || m.PrimaryRootId is not { } primary) return (order, pulled);
        var room = roomInFillOrder.ToList();
        foreach (var q in snapshot)
        {
            if (q.Fits) continue;
            var f = m.ItemById(q.GogId)?.Files.FirstOrDefault(x => x.FileKey == q.FileKey);
            if (f is null) continue;
            var place = PlacementPlanner.ToPlace(f);
            var rid = PlacementPlanner.WouldFit(place, room, m.Routing, primary, m.Routing.Mode, m.ExtrasLayout);
            if (rid is null) continue;
            int di = room.FindIndex(d => d.RootId == rid);
            room[di] = room[di] with { FreeBytes = room[di].FreeBytes - PlacementPlanner.CostOf(place, rid, room, m.Routing, primary, m.ExtrasLayout) };
            pulled.Add((q.GogId, q.FileKey));
        }
        if (pulled.Count == 0) return (order, pulled);
        var anchor = order[lastFit];
        foreach (var k in pulled) order.Remove(k);
        order.InsertRange(order.IndexOf(anchor) + 1, pulled);
        return (order, pulled);
    }
}

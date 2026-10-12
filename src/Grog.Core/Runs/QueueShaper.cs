// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Volumes;

namespace Grog.Core.Runs;

public static class QueueShaper
{
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

    public static bool CouldFit(LibraryManifest m, GameFile f, IReadOnlyList<DeviceSpace> roomInFillOrder)
        => m.PrimaryRootId is { } primary
           && PlacementPlanner.WouldFit(PlacementPlanner.ToPlace(f), roomInFillOrder, m.Routing, primary, m.Routing.Mode, m.ExtrasLayout) is not null;

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

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Models;

namespace Grog.Core.Volumes;

public static class PlacementPlanner
{
    public sealed record FileToPlace(string FileKey, long GameGogId, FileKind Kind, long Bytes)
    {
        public long FullBytes { get; init; }

        public string? PartialRootId { get; init; }

        public string? CurrentRootId { get; init; }

        public bool SizeIsLabel { get; init; }
    }

    public static long LabelSlack(long max) => max / 50;

    public static bool OverCeiling(FileToPlace f, long max)
        => f.FullBytes > max && !(f.SizeIsLabel && f.FullBytes - max <= LabelSlack(max));

    public sealed record FilePlacement(string FileKey, string? RootId, bool Fits);

    /// <param name="FreeAfter">The online devices IN FILL ORDER with the free bytes left once every placed file
    /// has its room: the one figure for "what could still be pulled above the line", and the input
    /// <see cref="WouldFit"/> tests a single file against.</param>
    public sealed record PlacementPlan(IReadOnlyList<FilePlacement> Placements, long ShortfallBytes,
                                       IReadOnlyList<DeviceSpace> FreeAfter)
    {
        public bool AllFit => ShortfallBytes == 0;
    }

    public static PlacementPlan Plan(
        IReadOnlyList<FileToPlace> files,
        IReadOnlyList<DeviceSpace> devices,
        RoutingPolicy routing,
        string primaryRootId,
        RoutingMode mode,
        ExtrasPlacement layout)
    {
        var online = devices.Where(d => d.IsOnline && !DriveWaits.Contains(d.RootId)).ToList();
        var free = new Dictionary<string, long>();
        foreach (var d in online) free[d.Key] = free.TryGetValue(d.Key, out var had) ? Math.Min(had, d.FreeBytes) : d.FreeBytes;
        var keyOf = online.ToDictionary(d => d.RootId, d => d.Key);
        var order = online.Select(d => d.RootId).ToList();
        var maxOf = online.ToDictionary(d => d.RootId, d => d.MaxFileBytes);   // per-file ceiling (FAT32)
        var clusterOf = online.ToDictionary(d => d.RootId, d => d.ClusterBytes);

        var placements = new List<FilePlacement>(files.Count);
        long shortfall = 0;
        foreach (var f in files)   // queue order: this loop IS the rule
        {
            var intended = routing.ResolveRootId(f.GameGogId, f.Kind, primaryRootId, layout);
            string? target = null;
            long Cost(string rid) => Storage.DriveResolver.OnDisk(CostOn(f, rid, intended, keyOf), clusterOf[rid]);
            string? waitOn = f.CurrentRootId is { } cr && !keyOf.ContainsKey(cr) && DriveWaits.Contains(cr) ? cr
                : (f.CurrentRootId is null || f.CurrentRootId == intended) && !keyOf.ContainsKey(intended) && DriveWaits.Contains(intended) ? intended
                : null;
            if (waitOn is not null)
            {
                placements.Add(new FilePlacement(f.FileKey, waitOn, true));
                continue;
            }
            if (keyOf.TryGetValue(intended, out var ik) && free[ik] >= Cost(intended) && !OverCeiling(f, maxOf[intended])) target = intended;
            else if (mode != RoutingMode.Manual)
                target = order.FirstOrDefault(rid => rid != intended && free[keyOf[rid]] >= Cost(rid) && !OverCeiling(f, maxOf[rid]));

            if (target is not null)
            {
                free[keyOf[target]] -= Cost(target);
                placements.Add(new FilePlacement(f.FileKey, target, true));
            }
            else
            {
                // Fits nowhere
                shortfall += f.Bytes;
                placements.Add(new FilePlacement(f.FileKey, intended, false));
            }
        }
        return new PlacementPlan(placements, shortfall, online.Select(d => d with { FreeBytes = free[d.Key] }).ToList());
    }

    public static string? WouldFit(FileToPlace f, IReadOnlyList<DeviceSpace> roomInFillOrder,
                                   RoutingPolicy routing, string primaryRootId, RoutingMode mode, ExtrasPlacement layout)
    {
        var intended = routing.ResolveRootId(f.GameGogId, f.Kind, primaryRootId, layout);
        var home = roomInFillOrder.FirstOrDefault(d => d.RootId == intended && d.IsOnline);
        var keyOf = roomInFillOrder.Where(d => d.IsOnline).GroupBy(d => d.RootId).ToDictionary(g => g.Key, g => g.First().Key);
        long Cost(string rid) => Storage.DriveResolver.OnDisk(CostOn(f, rid, intended, keyOf), roomInFillOrder.FirstOrDefault(d => d.RootId == rid)?.ClusterBytes ?? 0L);
        if (home is not null && home.FreeBytes >= Cost(intended) && !OverCeiling(f, home.MaxFileBytes)) return intended;
        if (mode == RoutingMode.Manual) return null;
        return roomInFillOrder.FirstOrDefault(d => d.IsOnline && d.RootId != intended && d.FreeBytes >= Cost(d.RootId) && !OverCeiling(f, d.MaxFileBytes))?.RootId;
    }

    public static long CostOf(FileToPlace f, string rootId, IReadOnlyList<DeviceSpace> roomInFillOrder,
                              RoutingPolicy routing, string primaryRootId, ExtrasPlacement layout)
    {
        var intended = routing.ResolveRootId(f.GameGogId, f.Kind, primaryRootId, layout);
        var keyOf = roomInFillOrder.Where(d => d.IsOnline).GroupBy(d => d.RootId).ToDictionary(g => g.Key, g => g.First().Key);
        return Storage.DriveResolver.OnDisk(CostOn(f, rootId, intended, keyOf), roomInFillOrder.FirstOrDefault(d => d.RootId == rootId)?.ClusterBytes ?? 0L);
    }

    private static long CostOn(FileToPlace f, string rootId, string intended, IReadOnlyDictionary<string, string> keyOf)
    {
        if (f.FullBytes <= f.Bytes) return f.Bytes;
        var partRoot = f.PartialRootId ?? intended;
        if (partRoot == rootId) return f.Bytes;
        return keyOf.TryGetValue(partRoot, out var pk) && keyOf.TryGetValue(rootId, out var rk) && pk == rk ? f.Bytes : f.FullBytes;
    }

    public static long StillNeeds(GameFile f)
        => Math.Max(0L, (f.PlanSizeBytes ?? 0L) - (f.HasPartial ? f.PartialBytes ?? 0L : 0L));

    public static FileToPlace ToPlace(GameFile f) => new(f.FileKey, f.GameGogId, f.Kind, StillNeeds(f)) { FullBytes = f.PlanSizeBytes ?? 0L, PartialRootId = f.HasPartial ? f.PartialRootId : null, SizeIsLabel = f.WireSizeBytes is null };

    public static PlacementPlan PlanQueueDetailed(
        IReadOnlyList<GameFile> files,
        IReadOnlyList<DeviceSpace> devicesInFillOrder,
        RoutingPolicy routing,
        string primaryRootId,
        RoutingMode mode,
        ExtrasPlacement layout,
        IReadOnlyList<string?>? currentRoots = null)
    {
        var toPlace = files.Select((f, i) => currentRoots is null ? ToPlace(f) : ToPlace(f) with { CurrentRootId = currentRoots[i] }).ToList();
        return Plan(toPlace, devicesInFillOrder, routing, primaryRootId, mode, layout);
    }
}

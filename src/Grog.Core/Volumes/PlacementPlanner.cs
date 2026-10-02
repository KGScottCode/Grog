// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Models;

namespace Grog.Core.Volumes;

/// <summary>Front-run device placement: from each device's free space and every queued file's size, decides
/// up front which device each file lands on (including where spill starts). Pure and deterministic; live
/// out-of-space handling is a separate backstop.</summary>
public static class PlacementPlanner
{
    /// <summary>A file to place: its routing inputs (game id + kind) and its size.</summary>
    public sealed record FileToPlace(string FileKey, long GameGogId, FileKind Kind, long Bytes)
    {
        /// <summary>The file's WHOLE size, for a volume's per-file ceiling (DeviceSpace.MaxFileBytes). Bytes is what it
        /// still needs; a 6 GB file with a 4 GB partial still cannot exist on FAT32. 0 = unknown, not tested.</summary>
        public long FullBytes { get; init; }

        /// <summary>The root holding this file's partial, when Bytes is discounted by one (null = the routed root,
        /// for records older than 09-19). The discount is room already spent on THAT volume only: anywhere else
        /// the file costs <see cref="FullBytes"/> (QA 09-19: a 5 GB file with 4.5 GB received on a full drive
        /// "fit" a second drive with 1 GB free, and the engine then needed all 5 GB there).</summary>
        public string? PartialRootId { get; init; }

        /// <summary>(09-25) Where the queue has this file headed now. When that drive is WAITING (it went away
        /// mid-run) the file stays on it: a file that spilled there because the primary was full must not be
        /// moved back, or marked "no room", just because its drive is unplugged (review 09-25).</summary>
        public string? CurrentRootId { get; init; }

        /// <summary>FullBytes is GOG's ROUNDED label, not a length the CDN gave (no <see cref="GameFile.WireSizeBytes"/>).
        /// A "4 GB" label is 4 GiB exactly, one byte over FAT32's ceiling, on a part GOG cut to fit under it.</summary>
        public bool SizeIsLabel { get; init; }
    }

    /// <summary>How far a rounded label can sit from the real length: 1 part in 50 (09-19).</summary>
    public static long LabelSlack(long max) => max / 50;

    /// <summary>Is this file over a volume's per-file ceiling? A REAL size answers exactly. A label within
    /// <see cref="LabelSlack"/> above the ceiling does not say: it is planned as fitting, <see cref="Runs.CeilingSizeCheck"/>
    /// asks the CDN for the real length before a run places it, and the engine's own check is the last gate.</summary>
    public static bool OverCeiling(FileToPlace f, long max)
        => f.FullBytes > max && !(f.SizeIsLabel && f.FullBytes - max <= LabelSlack(max));

    /// <summary>Where a file was planned to go. <see cref="Fits"/> is false when nothing had room for it
    /// (Manual mode, or genuinely out of combined space) -- the caller warns rather than silently dropping.</summary>
    public sealed record FilePlacement(string FileKey, string? RootId, bool Fits);

    /// <param name="FreeAfter">The online devices IN FILL ORDER with the free bytes left once every placed file
    /// has its room (09-16): the one figure for "what could still be pulled above the line", and the input
    /// <see cref="WouldFit"/> tests a single file against.</param>
    public sealed record PlacementPlan(IReadOnlyList<FilePlacement> Placements, long ShortfallBytes,
                                       IReadOnlyList<DeviceSpace> FreeAfter)
    {
        public bool AllFit => ShortfallBytes == 0;
    }

    /// <summary>Plans placement for files IN QUEUE ORDER against devices in fill order (primary first).
    ///
    /// ONE RULE (owner, 09-11): the queue order is the user's, and this never reorders, groups or chooses on
    /// their behalf. Each file, in turn, lands on its routed device if that device still has room after the
    /// files ahead of it; otherwise (unless Manual) on the first other device with room; otherwise it is
    /// marked won't-fit and KEEPS ITS POSITION. Free space is decremented as files are placed, so a large file
    /// that does not fit is skipped and the smaller files behind it still land. Inform, never choose.
    ///
    /// This replaces the whole-game knapsack (09-05 to 09-11), which chose WHICH games got the device and, on a
    /// size-sorted queue, silently demoted the head rows the user had just put there. A partial game is now
    /// possible; the Storage page shows per-game completeness, so the consequence is visible where the user
    /// would look for it.</summary>
    public static PlacementPlan Plan(
        IReadOnlyList<FileToPlace> files,
        IReadOnlyList<DeviceSpace> devices,
        RoutingPolicy routing,
        string primaryRootId,
        RoutingMode mode,
        ExtrasPlacement layout)
    {
        // (09-25) A drive files are waiting for is not a place to put anything, whatever a probe says: on macOS a
        // force-detached /Volumes folder resolved to "/" (the boot volume) and its small free figure starved the
        // primary's ledger ("276 files had no room" with 3 TB free, walk 09-25).
        var online = devices.Where(d => d.IsOnline && !DriveWaits.Contains(d.RootId)).ToList();
        // One ledger per VOLUME (DeviceSpace.Key), not per root: roots sharing a drive spend the same free space.
        // Each such root reports the volume's free figure less its own in-flight reservation, so the smallest is the truth.
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
            // (09-25) Its storage went away mid-run: the file WAITS for it (owner's default). Not a spill to another
            // drive (the user chooses that on the card) and not "won't fit" (it is not a space problem).
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
                // Fits nowhere: stays in the queue at its position, flagged. The routed device is recorded so
                // the row still says where it WOULD go.
                shortfall += f.Bytes;
                placements.Add(new FilePlacement(f.FileKey, intended, false));
            }
        }
        return new PlacementPlan(placements, shortfall, online.Select(d => d with { FreeBytes = free[d.Key] }).ToList());
    }

    /// <summary>The device ONE file would land on given the room left per device, or null: the same rule as
    /// <see cref="Plan"/> for one file (routed root first; spill to the next device in fill order only when the
    /// mode is not Manual). The App's "Pull up if it fits" / "Download this next" gates and "Pull up what fits"
    /// all ask this, so a move is never offered that the next plan would undo (review 09-16).</summary>
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

    /// <summary>What <see cref="WouldFit"/> charged <paramref name="f"/> on <paramref name="rootId"/>: the same cost
    /// <see cref="Plan"/> spends, so a caller walking several files through WouldFit decrements the room by the
    /// planner's figure, not the file's bare remainder (QA 09-30 A6: the tail of a pull-up bounced back red).</summary>
    public static long CostOf(FileToPlace f, string rootId, IReadOnlyList<DeviceSpace> roomInFillOrder,
                              RoutingPolicy routing, string primaryRootId, ExtrasPlacement layout)
    {
        var intended = routing.ResolveRootId(f.GameGogId, f.Kind, primaryRootId, layout);
        var keyOf = roomInFillOrder.Where(d => d.IsOnline).GroupBy(d => d.RootId).ToDictionary(g => g.Key, g => g.First().Key);
        return Storage.DriveResolver.OnDisk(CostOn(f, rootId, intended, keyOf), roomInFillOrder.FirstOrDefault(d => d.RootId == rootId)?.ClusterBytes ?? 0L);
    }

    /// <summary>What <paramref name="f"/> costs on <paramref name="rootId"/>: what it still needs on the VOLUME
    /// that holds its partial, its whole size anywhere else. No partial (Bytes == FullBytes, or FullBytes unknown):
    /// Bytes everywhere.</summary>
    private static long CostOn(FileToPlace f, string rootId, string intended, IReadOnlyDictionary<string, string> keyOf)
    {
        if (f.FullBytes <= f.Bytes) return f.Bytes;
        var partRoot = f.PartialRootId ?? intended;
        if (partRoot == rootId) return f.Bytes;
        return keyOf.TryGetValue(partRoot, out var pk) && keyOf.TryGetValue(rootId, out var rk) && pk == rk ? f.Bytes : f.FullBytes;
    }

    /// <summary>A file is priced at what it STILL NEEDS (09-13): the bytes of a resumable .part already sit on the
    /// drive and are already out of its free figure. Every room test uses this, never the raw size.</summary>
    public static long StillNeeds(GameFile f)
        => Math.Max(0L, (f.PlanSizeBytes ?? 0L) - (f.HasPartial ? f.PartialBytes ?? 0L : 0L));

    public static FileToPlace ToPlace(GameFile f) => new(f.FileKey, f.GameGogId, f.Kind, StillNeeds(f)) { FullBytes = f.PlanSizeBytes ?? 0L, PartialRootId = f.HasPartial ? f.PartialRootId : null, SizeIsLabel = f.WireSizeBytes is null };

    /// <summary>Same planning, keeping the WHOLE plan. <c>Placements[i]</c> is <paramref name="files"/>[i]:
    /// index by position, never by FileKey alone (shared across games). PlanQueue flattens to FileKey -&gt; RootId and drops
    /// <see cref="PlacementPlan.ShortfallBytes"/> and each placement's <see cref="FilePlacement.Fits"/>,
    /// which is why a caller could place files correctly and still be unable to say what did not fit.</summary>
    public static PlacementPlan PlanQueueDetailed(
        IReadOnlyList<GameFile> files,
        IReadOnlyList<DeviceSpace> devicesInFillOrder,
        RoutingPolicy routing,
        string primaryRootId,
        RoutingMode mode,
        ExtrasPlacement layout,
        IReadOnlyList<string?>? currentRoots = null)
    {
        // Priced at what each file STILL NEEDS (StillNeeds): charging the full size again read a fully received
        // 190 MB partial as "won't fit" on a drive with 190 MB of room -- its own. The .part travels with the
        // file to whichever device it lands on (PreparePartial adopts a stray), so the discount holds everywhere.
        var toPlace = files.Select((f, i) => currentRoots is null ? ToPlace(f) : ToPlace(f) with { CurrentRootId = currentRoots[i] }).ToList();
        return Plan(toPlace, devicesInFillOrder, routing, primaryRootId, mode, layout);
    }
}

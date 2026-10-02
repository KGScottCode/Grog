// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Collections.Generic;
using System.Linq;
using Grog.Core.Models;
using Grog.Core.Volumes;
using Grog.Core.Tests.Framework;

/// <summary>
/// Placement is IN QUEUE ORDER, per file, greedy (owner, 09-11). Rewritten from the whole-game knapsack suite
/// (09-05 to 09-11): that rule chose WHICH games got a device and, on a size-sorted queue, silently demoted the
/// rows the user had just put at the head. The rule now is the one a person expects from a list they ordered
/// themselves: top to bottom, each file lands if there is room, a file with no room is flagged and STAYS PUT,
/// and the files behind it still land. Inform, never choose.
/// </summary>
[Trait("placement")]
public class PlacementPlannerTests
{
    const string P = "primary", S = "secondary";

    static DeviceSpace Dev(string id, long free, bool online = true) => new(id, free, online);

    static PlacementPlanner.FileToPlace File(string key, long bytes, FileKind kind = FileKind.Installer, long gog = 1)
        => new(key, gog, kind, bytes);

    static RoutingPolicy Routing(RoutingMode mode) => new() { Mode = mode };

    static string Where(PlacementPlanner.PlacementPlan plan, string key)
        => plan.Placements.First(p => p.FileKey == key).RootId ?? "(none)";
    static bool Fits(PlacementPlanner.PlacementPlan plan, string key)
        => plan.Placements.First(p => p.FileKey == key).Fits;

    // ---- THE RULE ----

    [Test] void InOrder_FillsThePrimaryTopDown_ThenSpills()
    {
        var files = new[] { File("a", 40, gog: 1), File("b", 40, gog: 2), File("c", 40, gog: 3) };   // 120 total
        var devices = new[] { Dev(P, 100), Dev(S, 1000) };
        var plan = PlacementPlanner.Plan(files, devices, Routing(RoutingMode.Overflow), P, RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);

        Assert.Equal(P, Where(plan, "a"), "first in line lands first");
        Assert.Equal(P, Where(plan, "b"), "second fills the rest");
        Assert.Equal(S, Where(plan, "c"), "third has no room on P and spills");
        Assert.True(plan.AllFit, "everything fits across the two devices");
    }

    /// <summary>(review 09-25) The primary filled, later files spilled to the secondary, then the secondary was
    /// unplugged mid-run. Those files wait on it: not moved back, not "no room".</summary>
    [Test] void SpilledFiles_WaitOnTheirUnpluggedDrive()
    {
        Grog.Core.Volumes.DriveWaits.Clear();
        Grog.Core.Volumes.DriveWaits.Add(S);
        try
        {
            var files = new[] { File("a", 40, gog: 1) with { CurrentRootId = S }, File("b", 40, gog: 2) with { CurrentRootId = S } };
            // Primary full: the old rule marked both won't-fit.
            var plan = PlacementPlanner.Plan(files, new[] { Dev(P, 10), new DeviceSpace(S, 0, false) }, Routing(RoutingMode.Overflow), P, RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);
            Assert.True(plan.Placements.All(p => p.Fits && p.RootId == S), "full primary: both wait on the secondary");
            // Primary with room: the old rule moved both back to it.
            plan = PlacementPlanner.Plan(files, new[] { Dev(P, 1000), new DeviceSpace(S, 0, false) }, Routing(RoutingMode.Overflow), P, RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);
            Assert.True(plan.Placements.All(p => p.Fits && p.RootId == S), "roomy primary: still wait on the secondary");
        }
        finally { Grog.Core.Volumes.DriveWaits.Clear(); }
    }

    // THE CASE THE KNAPSACK GOT WRONG FOR THE USER. Order is the user's; the planner does not improve on it.
    /// <summary>(QA 09-18) Two roots on ONE drive spend the same free space: each reports the volume's 10 GB, and
    /// a ledger per root let 8 GB land on each -- the second transfer died out of space mid-stream.</summary>
    [Test] void TwoRootsOnOneVolume_ShareOneLedger()
    {
        var files = new[] { File("a", 8, gog: 1), File("b", 8, gog: 2) };
        var devices = new[] { new DeviceSpace(P, 10, true, "d:"), new DeviceSpace(S, 10, true, "d:") };
        var plan = PlacementPlanner.Plan(files, devices, Routing(RoutingMode.Overflow), P, RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);

        Assert.Equal(P, Where(plan, "a"));
        Assert.False(Fits(plan, "b"), "the 2 GB left on the drive is the same 2 GB whichever root asks");
        Assert.Equal(8, plan.ShortfallBytes);
        Assert.True(plan.FreeAfter.All(d => d.FreeBytes == 2), "both roots report the volume's leftover");
    }

    [Test] void InOrder_NeverReordersToFillBetter()
    {
        // 80 + 20 would fill the 100 exactly. The user put 40 first, so 40 lands first and 80 spills.
        var files = new[] { File("a", 40, gog: 1), File("b", 80, gog: 2), File("c", 20, gog: 3) };
        var devices = new[] { Dev(P, 100), Dev(S, 1000) };
        var plan = PlacementPlanner.Plan(files, devices, Routing(RoutingMode.Overflow), P, RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);

        Assert.Equal(P, Where(plan, "a"), "a is first, a lands");
        Assert.Equal(S, Where(plan, "b"), "b does not fit what is left and spills; nobody swaps it in");
        Assert.Equal(P, Where(plan, "c"), "c still fits the remainder and lands behind a");
    }

    // A big file that does not fit is SKIPPED, not a wall: the smaller files behind it keep landing.
    [Test] void ABigFileWithNoRoom_IsSkippedAndTheRestStillLand()
    {
        var files = new[] { File("big", 500, gog: 1), File("s1", 30, gog: 2), File("s2", 30, gog: 3) };
        var devices = new[] { Dev(P, 100) };
        var plan = PlacementPlanner.Plan(files, devices, Routing(RoutingMode.Overflow), P, RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);

        Assert.False(Fits(plan, "big"), "nowhere for 500");
        Assert.Equal(P, Where(plan, "s1"), "the file behind it still lands");
        Assert.Equal(P, Where(plan, "s2"), "and the next");
        Assert.Equal(500L, plan.ShortfallBytes, "the shortfall is exactly the one held file");
    }

    // The flagged file keeps its POSITION. The plan comes back in queue order with the flag in place --
    // it is never moved to the end.
    [Test] void AWontFitFile_KeepsItsPositionInThePlan()
    {
        var files = new[] { File("s1", 30, gog: 1), File("big", 500, gog: 2), File("s2", 30, gog: 3) };
        var devices = new[] { Dev(P, 100) };
        var plan = PlacementPlanner.Plan(files, devices, Routing(RoutingMode.Overflow), P, RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);

        Assert.Equal("s1,big,s2", string.Join(",", plan.Placements.Select(p => p.FileKey)), "same order in, same order out");
        Assert.False(plan.Placements[1].Fits, "the middle one is the flagged one");
        Assert.Equal(P, plan.Placements[1].RootId, "and it still names where it would go");
    }

    // Games are NOT kept whole any more. A game can be split across devices, or partially held, and the
    // user sees that on the Storage page. That is the trade the owner chose over the planner choosing.
    [Test] void AGamesFiles_ArePlacedIndependently()
    {
        var files = new[] { File("a", 40, gog: 1), File("b", 40, gog: 1), File("c", 40, gog: 1) };
        var devices = new[] { Dev(P, 100), Dev(S, 1000) };
        var plan = PlacementPlanner.Plan(files, devices, Routing(RoutingMode.Overflow), P, RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);

        Assert.Equal(P, Where(plan, "a"), "a fits P");
        Assert.Equal(P, Where(plan, "b"), "b fits P");
        Assert.Equal(S, Where(plan, "c"), "c spills alone; its siblings are not dragged after it");
    }

    // ---- Manual (no spill) ----

    [Test] void Manual_NeverSpills_FlagsShortfall()
    {
        var files = new[] { File("a", 40), File("b", 40), File("c", 40) };
        var devices = new[] { Dev(P, 100), Dev(S, 1000) };
        var plan = PlacementPlanner.Plan(files, devices, Routing(RoutingMode.Manual), P, RoutingMode.Manual, ExtrasPlacement.SeparateByGame);

        Assert.Equal(P, Where(plan, "a"), "a primary");
        Assert.Equal(P, Where(plan, "b"), "b primary");
        Assert.False(plan.AllFit, "c has nowhere to go -- Manual never spills");
        Assert.Equal(40L, plan.ShortfallBytes, "the one 40-byte file that didn't fit");
        Assert.False(Fits(plan, "c"), "c flagged not-fitting");
    }

    // ---- ByContent (role routing + spill fallback) ----

    [Test] void ByContent_RoutesByRole_ThenSpillsWhenRoleDeviceFull()
    {
        var routing = new RoutingPolicy { Mode = RoutingMode.ByContent };
        routing.RoleRoots[ContentRole.Extras] = S;   // extras pinned to secondary; games default to primary
        var files = new[]
        {
            File("game1", 30, FileKind.Installer, gog: 1),
            File("extra1", 30, FileKind.Extra, gog: 1),
            File("extra2", 30, FileKind.Extra, gog: 2),   // secondary only has room for one 30
        };
        var devices = new[] { Dev(P, 1000), Dev(S, 50) };
        var plan = PlacementPlanner.Plan(files, devices, routing, P, RoutingMode.ByContent, ExtrasPlacement.SeparateByGame);

        Assert.Equal(P, Where(plan, "game1"), "game routed to primary (Games default)");
        Assert.Equal(S, Where(plan, "extra1"), "extra routed to secondary");
        Assert.Equal(P, Where(plan, "extra2"), "second extra spills to primary -- secondary full");
    }

    [Test] void ByContent_ExtrasOnSecondary_DoNotConsumeThePrimary()
    {
        var routing = new RoutingPolicy { Mode = RoutingMode.ByContent };
        routing.RoleRoots[ContentRole.Extras] = S;
        var files = new[]
        {
            File("g1", 60, FileKind.Installer, gog: 1),
            File("x1", 90, FileKind.Extra, gog: 1),       // pinned to S: must not count against P's 100
            File("g2", 40, FileKind.Installer, gog: 2),   // 60 + 40 = 100: fits only if x1 took nothing from P
            File("x2", 90, FileKind.Extra, gog: 2),
        };
        var devices = new[] { Dev(P, 100), Dev(S, 1000) };
        var plan = PlacementPlanner.Plan(files, devices, routing, P, RoutingMode.ByContent, ExtrasPlacement.SeparateByGame);

        Assert.Equal(P, Where(plan, "g1"), "g1 on the primary");
        Assert.Equal(P, Where(plan, "g2"), "g2 completes the exact fit");
        Assert.Equal(S, Where(plan, "x1"), "extras go where they are pinned");
        Assert.Equal(S, Where(plan, "x2"), "and never take primary room");
    }

    [Test] void ByContent_SpillNeverOversubscribesTheDeviceItLandsOn()
    {
        var routing = new RoutingPolicy { Mode = RoutingMode.ByContent };
        routing.RoleRoots[ContentRole.Extras] = S;
        var files = new[]
        {
            File("g1", 60, FileKind.Installer, gog: 1),
            File("g2", 40, FileKind.Installer, gog: 2),   // 100 on P, exactly full
            File("x1", 30, FileKind.Extra, gog: 3),       // S holds one 30
            File("x2", 30, FileKind.Extra, gog: 4),       // nowhere has room
        };
        var devices = new[] { Dev(P, 100), Dev(S, 30) };
        var plan = PlacementPlanner.Plan(files, devices, routing, P, RoutingMode.ByContent, ExtrasPlacement.SeparateByGame);

        long onP = plan.Placements.Where(p => p.RootId == P && p.Fits).Sum(p => files.First(f => f.FileKey == p.FileKey).Bytes);
        Assert.True(onP <= 100, $"primary never oversubscribed (got {onP})");
        Assert.Equal(S, Where(plan, "x1"), "first extra fits the secondary");
        Assert.False(Fits(plan, "x2"), "the second extra fits nowhere and says so");
    }

    // WithGame changes ROUTING (an extra goes where its game goes), not grouping: each file is still placed
    // in its own turn.
    [Test] void WithGame_RoutesExtrasWithTheirGame_StillPerFile()
    {
        var files = new[]
        {
            File("g1", 50, FileKind.Installer, gog: 1), File("x1", 40, FileKind.Extra, gog: 1),
            File("g2", 30, FileKind.Installer, gog: 2), File("x2", 10, FileKind.Extra, gog: 2),
        };
        var devices = new[] { Dev(P, 100), Dev(S, 1000) };
        var plan = PlacementPlanner.Plan(files, devices, Routing(RoutingMode.Overflow), P, RoutingMode.Overflow, ExtrasPlacement.WithGame);

        Assert.Equal(P, Where(plan, "g1")); Assert.Equal(P, Where(plan, "x1"));   // 90 on P
        Assert.Equal(S, Where(plan, "g2"), "g2 (30) does not fit the 10 left and spills");
        Assert.Equal(P, Where(plan, "x2"), "x2 (10) fits the 10 left and lands -- its game spilling does not drag it");
        Assert.Equal(0L, plan.ShortfallBytes, "everything placed");
    }

    // ---- offline device ----

    [Test] void OfflineDevice_IsNotUsed()
    {
        var files = new[] { File("a", 40), File("b", 40) };
        var devices = new[] { Dev(P, 50), Dev(S, 1000, online: false) };   // secondary offline
        var plan = PlacementPlanner.Plan(files, devices, Routing(RoutingMode.Overflow), P, RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);

        Assert.Equal(P, Where(plan, "a"), "a primary");
        Assert.False(plan.AllFit, "b can't spill to the offline secondary");
    }

    [Test] void SharedFileKeyAcrossGames_PlacesEachGamesCopy()
    {
        // Ultima Underworld I and II share a FileKey. Each gets its own row, in order.
        var files = new[] { File("shared", 60, gog: 1), File("only1", 40, gog: 1), File("shared", 60, gog: 2), File("only2", 40, gog: 2) };
        var devices = new[] { Dev(P, 100), Dev(S, 1000) };
        var plan = PlacementPlanner.Plan(files, devices, Routing(RoutingMode.Overflow), P, RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);

        Assert.Equal(4, plan.Placements.Count, "one placement per file, in order");
        Assert.Equal(P, plan.Placements[0].RootId, "game 1 shared on P");
        Assert.Equal(P, plan.Placements[1].RootId, "game 1 only1 fills P");
        Assert.Equal(S, plan.Placements[2].RootId, "game 2 shared on S");
        Assert.True(plan.AllFit);
    }

    // ---- what removing the won't-fit rows does ----

    // After the flagged rows are removed, what remains fits BY CONSTRUCTION, so any reorder of the rest
    // changes only order, never fit. This is what makes "Remove them from the queue" a one-shot, stable action.
    [Test] void RemovingTheWontFitRows_LeavesASetThatFitsInAnyOrder()
    {
        var files = new[] { File("a", 30, gog: 1), File("big", 500, gog: 2), File("b", 30, gog: 3), File("c", 30, gog: 4) };
        var devices = new[] { Dev(P, 100) };
        var first = PlacementPlanner.Plan(files, devices, Routing(RoutingMode.Overflow), P, RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);
        var kept = files.Where(f => first.Placements.First(p => p.FileKey == f.FileKey).Fits).ToList();
        Assert.Equal(3, kept.Count, "one row was flagged");

        foreach (var order in new[] { kept, kept.AsEnumerable().Reverse().ToList(), new[] { kept[1], kept[0], kept[2] }.ToList() })
        {
            var again = PlacementPlanner.Plan(order, devices, Routing(RoutingMode.Overflow), P, RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);
            Assert.True(again.AllFit, "reordering the survivors never produces a new won't-fit");
        }
    }

    /// <summary>(09-13) A resumable partial already occupies its bytes on the drive, so the file needs only the
    /// remainder. Charging the full size flagged a 100%-received file as won't-fit next to its own .part.</summary>
    [Test]
    public void A_partial_is_priced_at_what_it_still_needs()
    {
        var f = new GameFile { GameGogId = 1, FileKey = "/a", Kind = FileKind.Installer, ExpectedSizeBytes = 190, HasPartial = true, PartialBytes = 189 };
        var devices = new[] { new DeviceSpace("p", 5, true) };   // room for the last byte, not for 190
        var plan = PlacementPlanner.PlanQueueDetailed(new[] { f }, devices, new RoutingPolicy(), "p", RoutingMode.Overflow, ExtrasPlacement.WithGame);
        Assert.True(plan.Placements[0].Fits, "189 of 190 bytes are already on the drive: it fits");
        Assert.Equal(0L, plan.ShortfallBytes);

        f.HasPartial = false; f.PartialBytes = null;
        var again = PlacementPlanner.PlanQueueDetailed(new[] { f }, devices, new RoutingPolicy(), "p", RoutingMode.Overflow, ExtrasPlacement.WithGame);
        Assert.False(again.Placements[0].Fits, "no partial: the whole 190 is needed");
    }
    /// <summary>(walk 09-19) FAT32 holds no file over 4 GiB - 1, and Windows reports the overrun as disk full. A
    /// 6.1 GB installer was planned onto a FAT32 stick with room and died at byte 4,294,981,975. The WHOLE size is
    /// tested, so a partial does not sneak it under; it spills to a device that can hold it, else won't fit.</summary>
    [Test]
    public void A_file_over_the_volumes_per_file_ceiling_never_lands_there()
    {
        const long Fat32 = 4294967295L;
        Assert.Equal(Fat32, Grog.Core.Storage.DriveResolver.MaxFileBytesFor("FAT32"));
        Assert.Equal(Fat32, Grog.Core.Storage.DriveResolver.MaxFileBytesFor("vfat"));
        Assert.Equal(long.MaxValue, Grog.Core.Storage.DriveResolver.MaxFileBytesFor("NTFS"));
        Assert.Equal(long.MaxValue, Grog.Core.Storage.DriveResolver.MaxFileBytesFor("exFAT"));

        var big = new GameFile { GameGogId = 1, FileKey = "/big", Kind = FileKind.Installer, ExpectedSizeBytes = 6_549_825_126, HasPartial = true, PartialBytes = 4_294_000_000 };
        var small = new GameFile { GameGogId = 1, FileKey = "/small", Kind = FileKind.Installer, ExpectedSizeBytes = 1000 };
        var fat = new DeviceSpace("p", 10_000_000_000, true) { MaxFileBytes = Fat32 };

        var alone = PlacementPlanner.PlanQueueDetailed(new[] { big, small }, new[] { fat }, new RoutingPolicy(), "p", RoutingMode.Overflow, ExtrasPlacement.WithGame);
        Assert.False(alone.Placements[0].Fits, "room is not the problem: FAT32 cannot hold the file");
        Assert.True(alone.Placements[1].Fits, "the file behind it still lands");
        Assert.Null(PlacementPlanner.WouldFit(PlacementPlanner.ToPlace(big), new[] { fat }, new RoutingPolicy(), "p", RoutingMode.Overflow, ExtrasPlacement.WithGame));

        var ntfs = new DeviceSpace("s", 10_000_000_000, true);
        var spill = PlacementPlanner.PlanQueueDetailed(new[] { big }, new[] { fat, ntfs }, new RoutingPolicy(), "p", RoutingMode.Overflow, ExtrasPlacement.WithGame);
        Assert.True(spill.Placements[0].Fits);
        Assert.Equal("s", spill.Placements[0].RootId);
    }

    /// <summary>(1288) A file costs its size rounded up to the volume's allocation unit, the disk's own arithmetic, and a
    /// fixed floor per volume is nobody's to spend. Nothing is given back on landing, so the planned set is final.</summary>
    [Test]
    public void Files_cost_their_cluster_rounded_size_and_the_floor_is_held_back()
    {
        long floor = Grog.Core.Download.DownloadEngine.FloorBytes;
        var d = DeviceSpace.ForRoot("p", floor + 2 * 4096, true, null) with { ClusterBytes = 4096 };
        Assert.Equal(2 * 4096, d.FreeBytes, "the floor is off the top, once");
        Assert.Equal(4096, d.Charge(100), "a 100-byte file takes one unit");
        Assert.Equal(8192, d.Charge(4097), "one byte over takes two");
        var files = Enumerable.Range(0, 3).Select(i => new GameFile { GameGogId = 1, FileKey = "/" + i, Kind = FileKind.Installer, ExpectedSizeBytes = 100 }).ToArray();
        var plan = PlacementPlanner.PlanQueueDetailed(files, new[] { d }, new RoutingPolicy(), "p", RoutingMode.Overflow, ExtrasPlacement.WithGame);
        Assert.Equal("True,True,False", string.Join(",", plan.Placements.Select(p => p.Fits)), "two units of room: two files");
        Assert.Equal(32 * 1024, Grog.Core.Storage.DriveResolver.ClusterBytesFor("FAT32"));
        Assert.Equal(0, Grog.Core.Storage.DriveResolver.OnDisk(0, 4096));
    }

    /// <summary>(QA 09-19) The bytes a partial already holds are room spent on ITS drive. Planned onto another, the
    /// file needs its whole size there: a 5000-byte file with 4500 received on a full primary "fit" a secondary
    /// with 1000 free, and the engine then needed all 5000 on it.</summary>
    [Test]
    public void A_partial_discounts_the_file_only_on_the_volume_that_holds_it()
    {
        var f = new GameFile { GameGogId = 1, FileKey = "/a", Kind = FileKind.Installer, ExpectedSizeBytes = 5000, HasPartial = true, PartialBytes = 4500, PartialRootId = "p" };
        var routing = new RoutingPolicy();
        var tight = new[] { new DeviceSpace("p", 300, true), new DeviceSpace("s", 1000, true) };
        var plan = PlacementPlanner.PlanQueueDetailed(new[] { f }, tight, routing, "p", RoutingMode.Overflow, ExtrasPlacement.WithGame);
        Assert.False(plan.Placements[0].Fits, "500 more do not fit on p, and s would need all 5000");
        Assert.Null(PlacementPlanner.WouldFit(PlacementPlanner.ToPlace(f), tight, routing, "p", RoutingMode.Overflow, ExtrasPlacement.WithGame));

        var roomyS = new[] { new DeviceSpace("p", 300, true), new DeviceSpace("s", 6000, true) };
        var spill = PlacementPlanner.PlanQueueDetailed(new[] { f }, roomyS, routing, "p", RoutingMode.Overflow, ExtrasPlacement.WithGame);
        Assert.Equal("s", spill.Placements[0].RootId);
        Assert.Equal(1000L, spill.FreeAfter.First(d => d.RootId == "s").FreeBytes, "charged the whole 5000 on the other drive");

        var sameVolume = new[] { new DeviceSpace("p", 0, true, "d:"), new DeviceSpace("s", 600, true, "d:") };
        sameVolume[0] = sameVolume[0] with { FreeBytes = 600 };
        var shared = PlacementPlanner.PlanQueueDetailed(new[] { f }, sameVolume, routing, "p", RoutingMode.Overflow, ExtrasPlacement.WithGame);
        Assert.True(shared.Placements[0].Fits, "one volume: the partial's bytes are already out of its free figure");
    }
}

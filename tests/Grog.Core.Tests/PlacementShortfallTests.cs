// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

namespace Grog.Core.Tests;

/// <summary>The out-of-space contract: files that fit are placed (spilling to the next device with room),
/// files that fit nowhere are reported as such rather than dropped or force-assigned, and the shortfall
/// names the bytes involved. PlanQueue's flattened map must agree with the detailed plan it derives from.</summary>
public sealed class PlacementShortfallTests
{
    private static GameFile File(string key, long bytes) =>
        new() { FileKey = key, GameGogId = 1, Kind = FileKind.Installer, ExpectedSizeBytes = bytes };

    private static List<DeviceSpace> Devices(params (string Id, long Free)[] d) =>
        d.Select(x => new DeviceSpace(x.Id, x.Free, IsOnline: true)).ToList();

    private static RoutingPolicy Routing() => new();

    [Test]
    public void Everything_fits_nothing_is_short()
    {
        var plan = PlacementPlanner.PlanQueueDetailed(
            new[] { File("a", 100), File("b", 200) }, Devices(("p", 1000)), Routing(), "p", RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);
        Assert.True(plan.AllFit, "a queue inside free space has no shortfall");
        Assert.True(plan.Placements.All(p => p.Fits && p.RootId == "p"), "every file lands on the primary");
    }

    [Test]
    public void Overflow_spills_to_the_secondary_before_reporting_shortfall()
    {
        var plan = PlacementPlanner.PlanQueueDetailed(
            new[] { File("a", 800), File("b", 800) }, Devices(("p", 1000), ("s", 1000)), Routing(), "p", RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);
        Assert.True(plan.AllFit, "two devices with room = no shortfall");
        Assert.Equal("p", plan.Placements.First(p => p.FileKey == "a").RootId, "first file on the primary");
        Assert.Equal("s", plan.Placements.First(p => p.FileKey == "b").RootId, "second spills to the secondary");
    }

    [Test]
    public void What_fits_nowhere_is_reported_not_dropped_and_not_forced()
    {
        var plan = PlacementPlanner.PlanQueueDetailed(
            new[] { File("a", 900), File("big", 5000) }, Devices(("p", 1000)), Routing(), "p", RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);
        Assert.True(!plan.AllFit, "a file larger than all free space is a shortfall");
        Assert.Equal(5000, (int)plan.ShortfallBytes, "the shortfall names the unplaceable bytes");
        var big = plan.Placements.First(p => p.FileKey == "big");
        Assert.True(!big.Fits, "the file is marked as not fitting");
        Assert.Equal(2, plan.Placements.Count, "and it is still IN the plan - reported, never dropped");
        var a = plan.Placements.First(p => p.FileKey == "a");
        Assert.True(a.Fits && a.RootId == "p", "the file that fits still downloads normally");
    }

    [Test]
    public void Cumulative_fill_counts_earlier_placements_against_free_space()
    {
        // Three 400s against 1000 free: the third must not fit -- a naive per-file check would pass it.
        var plan = PlacementPlanner.PlanQueueDetailed(
            new[] { File("a", 400), File("b", 400), File("c", 400) }, Devices(("p", 1000)), Routing(), "p", RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);
        Assert.True(!plan.AllFit, "free space is consumed by the plan as it goes");
        Assert.Equal(400, (int)plan.ShortfallBytes, "exactly the third file is short");
        Assert.True(!plan.Placements.First(p => p.FileKey == "c").Fits, "and it is the LAST one, preserving queue order");
    }

    [Test]
    public void Replanning_after_freeing_space_places_previously_short_files()
    {
        // The restart/re-plan story: the same queue against grown free space stops being short. This is
        // the property the Folders page relies on when it re-plans on every refresh.
        var files = new[] { File("a", 900), File("b", 900) };
        var before = PlacementPlanner.PlanQueueDetailed(files, Devices(("p", 1000)), Routing(), "p", RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);
        Assert.True(!before.AllFit, "short before space is freed");
        var after = PlacementPlanner.PlanQueueDetailed(files, Devices(("p", 2000)), Routing(), "p", RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);
        Assert.True(after.AllFit, "the identical queue fits after the drive gains room");
    }
}

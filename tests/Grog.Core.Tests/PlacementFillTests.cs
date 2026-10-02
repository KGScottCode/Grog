// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Models;
using Grog.Core.Volumes;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

/// <summary>How full does in-order greedy placement leave a small primary against a library-sized queue? A realistic
/// shape (700 files, 200 games, sizes from 2 MB manuals to 12 GB installers) must end within a few MB.</summary>
public sealed class PlacementFillTests
{
    [Test]
    public void A_14GB_primary_against_490GB_ends_within_a_few_MB_of_full()
    {
        var rng = new Random(20260905);
        var files = new List<PlacementPlanner.FileToPlace>();
        long total = 0;
        for (int g = 1; g <= 200; g++)
        {
            int n = rng.Next(1, 8);
            for (int i = 0; i < n; i++)
            {
                // log-uniform 2 MB .. 12 GB
                double lo = Math.Log(2L << 20), hi = Math.Log(12L << 30);
                long size = (long)Math.Exp(lo + rng.NextDouble() * (hi - lo));
                files.Add(new PlacementPlanner.FileToPlace($"g{g}f{i}", g, i == 0 ? FileKind.Installer : FileKind.Extra, size));
                total += size;
            }
        }
        long primaryFree = 14_440L << 20;   // 14.44 GB
        var devices = new[] { new DeviceSpace("p", primaryFree, true), new DeviceSpace("s", 16L << 40, true) };
        var plan = PlacementPlanner.Plan(files, devices, new RoutingPolicy { Mode = RoutingMode.Overflow }, "p", RoutingMode.Overflow, ExtrasPlacement.SeparateByGame);
        long onP = 0;
        for (int i = 0; i < files.Count; i++) if (plan.Placements[i].RootId == "p") onP += files[i].Bytes;
        long left = primaryFree - onP;
        Console.WriteLine($"FILL total={total >> 30} GB onPrimary={onP / 1048576.0:F1} MB left={left / 1048576.0:F1} MB games={plan.Placements.Where(p => p.RootId == "p").Select(p => files.First(f => f.FileKey == p.FileKey).GameGogId).Distinct().Count()}");
        // Games are NOT kept whole any more (in-order per-file placement, 09-11): a split game is allowed and
        // visible on the Storage page. What still matters is that greedy in-order fill leaves the device full.
        Assert.True(left >= 0, "never over-committed");
        Assert.True(left < (20L << 20), $"left {left >> 20} MB should be under 20 MB");
    }
}

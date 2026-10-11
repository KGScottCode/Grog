// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using Grog.Core.Download;
using Grog.Core.Tests.Framework;

[NewBatch]
[Trait("throughput")]
public class FileEtaTests
{
    private static DateTime At(double s) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);
    private const long Total = 4_000_000_000;

    [Test] void SilentUntilSettled()
    {
        var e = new FileEta();
        Assert.True(e.Update(At(0), 0, Total) is null, "first sample: nothing yet");
        Assert.True(e.Update(At(1), 10_000_000, Total) is null, "under the settle time: nothing yet");
        Assert.True(e.Update(At(3.5), 35_000_000, Total) is not null, "settled: speaks");
    }

    [Test] void SteadyRateCountsDownOneSecondPerSecond()
    {
        var e = new FileEta();
        double? prev = null;
        for (int i = 0; i <= 120; i++)
        {
            var s = e.Update(At(i * 0.5), (long)(i * 0.5 * 10_000_000), Total);
            if (i == 120) Assert.True(Math.Abs(s!.Value - (Total - 600_000_000) / 10_000_000.0) < 2, "true time left at a steady 10 MB/s");
            if (prev is { } p && s is { } c && i > 20) Assert.True(Math.Abs(p - c - 0.5) < 0.2, "counts down with the clock");
            prev = s;
        }
    }

    [Test] void ABriefDipDoesNotSwingTheEstimate()
    {
        var e = new FileEta();
        long b = 0; double? before = null, worst = null;
        for (int i = 1; i <= 120; i++)
        {
            bool dip = i is >= 100 and < 102;                  // one second at a tenth of the speed
            b += dip ? 500_000 : 5_000_000;                    // 10 MB/s otherwise, sampled every 0.5 s
            var s = e.Update(At(i * 0.5), b, Total);
            if (i == 99) before = s;
            if (i >= 100 && s is { } v) worst = Math.Max(worst ?? 0, v);
        }
        // The old readout (left / half-second speed) jumped tenfold here: ~340 s to ~3400 s.
        Assert.True(worst!.Value - before!.Value < 30, $"a one-second dip moves it by seconds, not minutes ({before:0} -> {worst:0})");
    }

    [Test] void RestartedTransferStartsOver()
    {
        var e = new FileEta();
        e.Update(At(0), 0, Total);
        e.Update(At(5), 50_000_000, Total);
        Assert.True(e.Update(At(6), 1_000, Total) is null, "bytes went backwards (a restart): settle again");
    }
}

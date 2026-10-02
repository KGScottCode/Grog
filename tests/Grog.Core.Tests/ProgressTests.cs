namespace Grog.Core.Tests;

using System;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;
using Grog.Core.Format;

public class ProgressTests
{
    [Test] void Snapshot_PercentAndRemaining()
    {
        var s = new ProgressSnapshot(300, 400);
        Assert.True(s.Percent > 74.9 && s.Percent < 75.1, "75%");
        Assert.Equal(100L, s.RemainingBytes, "remaining");
        Assert.False(s.IsComplete, "not complete");
        Assert.True(new ProgressSnapshot(400, 400).IsComplete, "complete at total");
        Assert.Equal(0.0, new ProgressSnapshot(0, 0).Percent, "empty is 0% not divide-by-zero");
    }

    [Test] void ByteFormat_Units()
    {
        Assert.Equal("512 B", ByteFormat.Size(512), "raw bytes stay whole");
        Assert.Equal("8.00 KB", ByteFormat.Size(8192), "kb 2dp");
        Assert.Equal("1.50 GB", ByteFormat.Size(1610612736), "gb 2dp");
        Assert.Equal("718.50 GB", ByteFormat.Size(771483500544), "gb keeps 2dp above 100 (was 1dp: inconsistent)");
        Assert.Equal("2.000 TB", ByteFormat.Size(2199023255552), "tb 3dp (owner call 08-29)");
        Assert.Equal("--", ByteFormat.Rate(0), "no rate");
    }

    [Test] void ByteFormat_Duration()
    {
        Assert.Equal("45s", ByteFormat.Duration(TimeSpan.FromSeconds(45)), "seconds");
        Assert.Equal("10m 20s", ByteFormat.Duration(TimeSpan.FromSeconds(620)), "minutes");
        Assert.Equal("1h 15m", ByteFormat.Duration(TimeSpan.FromMinutes(75)), "hours");
    }


}

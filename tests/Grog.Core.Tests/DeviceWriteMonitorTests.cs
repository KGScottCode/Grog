// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Grog.Core.Download;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

public sealed class DeviceWriteMonitorTests
{
    [Test]
    public void SlowOnLongFlushes_FastBelowLowWater()
    {
        var m = new DeviceWriteMonitor();
        Assert.False(m.IsSlow("usb"), "unknown device is not slow");
        m.Record("usb", 0.2);
        Assert.False(m.IsSlow("usb"), "one quick flush");
        m.Record("usb", 3.0); m.Record("usb", 3.0); m.Record("usb", 3.0);
        Assert.True(m.IsSlow("usb"), "three 3 s flushes: slow");
        m.Record("usb", 1.5); m.Record("usb", 1.5);
        Assert.True(m.IsSlow("usb"), "1.5 s is under the slow line but above the fast line: stays slow (hysteresis)");
        for (int i = 0; i < 6; i++) m.Record("usb", 0.2);
        Assert.False(m.IsSlow("usb"), "back under 1 s: fast again");
        Assert.False(m.IsSlow("hdd"), "another device is judged on its own");
    }

    [Test]
    public void PrimaryKey_IsEmpty_NullMapsToIt()
    {
        var m = new DeviceWriteMonitor();
        m.Record(null, 5); m.Record(null, 5);
        Assert.True(m.IsSlow(""), "null and empty are the same device (the primary)");
    }

    [Test]
    public void One_very_long_flush_is_slow_at_once()
    {
        var m = new DeviceWriteMonitor();
        m.Record("usb", 0.2);
        m.Record("usb", 6.0);   // ema = 0.2*0.6 + 6*0.4 = 2.52 anyway, but the single-flush rule is the point
        Assert.True(m.IsSlow("usb"));
        var m2 = new DeviceWriteMonitor();
        for (int i = 0; i < 10; i++) m2.Record("usb", 0.1);
        m2.Record("usb", 5.5);   // ema stays well under 2 s; the 5.5 s flush alone flips it
        Assert.True(m2.IsSlow("usb"), "a single 5 s+ flush is verdict enough");
    }
}

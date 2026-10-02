// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Collections.Generic;
using System.Linq;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;
using D = Grog.Core.Volumes.DeviceSpace;

[NewBatch]
[Trait("fit")]
public class CapacityTests
{
    static List<D> Devices(params (string id, long free, bool online)[] xs)
        => xs.Select(x => new D(x.id, x.free, x.online)).ToList();

    [Test] void FitsOnDefault()
    {
        var r = Capacity.Evaluate(300, "default", Devices(("default", 500, true), ("secondary", 500, true)));
        Assert.Equal(FitVerdict.FitsOnSelected, r.Verdict, "fits on default alone");
        Assert.Equal(0L, r.ShortfallBytes, "no shortfall");
    }

    [Test] void NeedsSecondary()
    {
        var r = Capacity.Evaluate(700, "default", Devices(("default", 500, true), ("secondary", 500, true)));
        Assert.Equal(FitVerdict.NeedsAnotherDrive, r.Verdict, "needs the secondary to fit");
    }

    [Test] void WontFit_ExceedsCombined()
    {
        var r = Capacity.Evaluate(1200, "default", Devices(("default", 500, true), ("secondary", 500, true)));
        Assert.Equal(FitVerdict.WontFit, r.Verdict, "exceeds combined free space");
        Assert.Equal(200L, r.ShortfallBytes, "1200 - 1000 combined");
    }

    [Test] void OfflineSecondary_NotCounted()
    {
        var r = Capacity.Evaluate(700, "default", Devices(("default", 500, true), ("secondary", 500, false)));
        Assert.Equal(FitVerdict.WontFit, r.Verdict, "offline secondary can't be relied on");
    }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.IO;
using Grog.App.Services;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

public class GtkBackendGuardTests
{
    private static string Marker() => Path.Combine(Path.GetTempPath(), "grog-gtk-" + Path.GetRandomFileName());

    [Test]
    public void Default_works_nothing_applied_or_remembered()
    {
        var m = Marker(); var applied = new List<string>();
        Assert.True(GtkBackendGuard.Decide(null, m, b => b is null, applied.Add), "ready");
        Assert.Empty(applied, "no backend forced");
        Assert.False(File.Exists(m), "nothing remembered");
    }

    [Test]
    public void Default_fails_x11_works_applied_and_remembered()
    {
        var m = Marker(); var applied = new List<string>();
        Assert.True(GtkBackendGuard.Decide(null, m, b => b == "x11", applied.Add), "ready");
        Assert.Equal("x11", applied[0], "x11 applied");
        Assert.Equal("x11", File.ReadAllText(m).Trim(), "remembered");
        File.Delete(m);
    }

    [Test]
    public void Remembered_x11_skips_probing()
    {
        var m = Marker(); File.WriteAllText(m, "x11\n");
        var applied = new List<string>(); var probes = 0;
        Assert.True(GtkBackendGuard.Decide(null, m, _ => { probes++; return false; }, applied.Add), "ready");
        Assert.Equal(0, probes, "no probe");
        Assert.Equal("x11", applied[0], "x11 applied");
        File.Delete(m);
    }

    [Test]
    public void Both_fail_reports_not_ready()
    {
        var m = Marker(); var applied = new List<string>();
        Assert.False(GtkBackendGuard.Decide(null, m, _ => false, applied.Add), "not ready");
        Assert.Empty(applied, "nothing forced");
    }

    [Test]
    public void User_setting_wins()
    {
        var m = Marker(); var probes = 0;
        Assert.True(GtkBackendGuard.Decide("wayland", m, _ => { probes++; return false; }, _ => { }), "ready");
        Assert.Equal(0, probes, "never probed");
    }
}

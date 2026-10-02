// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Linq;
using Grog.Core.Cli;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;

[NewBatch]
[Trait("backupconfig")]
public class BackupConfigResolverTests
{
    static string Val(BackupConfigResolver.BackupPlan p, string name) => p.Settings.First(s => s.Name == name).Value;
    static string Src(BackupConfigResolver.BackupPlan p, string name) => p.Settings.First(s => s.Name == name).Source;

    [Test] void NoLocation_IsNotConfigured()
    {
        var p = BackupConfigResolver.Resolve(null, null, null, ExtrasPlacement.WithGame, null, null);
        Assert.True(!p.Configured, "no location -> not configured");
        Assert.True(p.NotConfiguredReason!.Contains("--primary"), "tells the user the fix");
    }

    [Test] void FlagBeatsConfig_ForLocation()
    {
        var p = BackupConfigResolver.Resolve("/flag/path", "/saved/path", null, ExtrasPlacement.WithGame, null, null);
        Assert.Equal("/flag/path", Val(p, "folder"), "flag wins");
        Assert.Equal("flag", Src(p, "folder"), "source is flag");
        Assert.True(p.Configured, "configured once a location resolves");
    }

    [Test] void SavedLocation_UsedWhenNoFlag()
    {
        var p = BackupConfigResolver.Resolve(null, "/saved/path", null, ExtrasPlacement.WithGame, null, null);
        Assert.Equal("/saved/path", Val(p, "folder"), "falls back to saved");
        Assert.Equal("config", Src(p, "folder"), "source is config");
    }

    [Test] void Defaults_AreDeclared_NotSilent()
    {
        var p = BackupConfigResolver.Resolve("/x", null, null, ExtrasPlacement.WithGame, null, null);
        Assert.Equal("games + extras", Val(p, "scope"), "default scope");
        Assert.Equal("default", Src(p, "scope"), "scope marked default");
        Assert.Equal("3", Val(p, "concurrency"), "default concurrency");
        Assert.Equal("on", Val(p, "verify-after-download"), "default verify on");
    }

    [Test] void Flags_OverrideDefaults()
    {
        var p = BackupConfigResolver.Resolve("/x", null, includeExtrasFlag: false, configuredExtrasLayout: ExtrasPlacement.SeparateByGame,
            concurrencyFlag: 8, verifyFlag: false);
        Assert.Equal("games only", Val(p, "scope"), "scope overridden");
        Assert.Equal("flag", Src(p, "scope"), "scope source flag");
        Assert.Equal("8", Val(p, "concurrency"), "concurrency overridden");
        Assert.Equal("off", Val(p, "verify-after-download"), "verify overridden");
    }
}

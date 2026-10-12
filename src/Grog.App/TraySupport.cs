// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Linq;

namespace Grog.App;

public static class TraySupport
{
    public static bool IsLikelySupported()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return true;
        if (!OperatingSystem.IsLinux()) return false;

        var hint = ((Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "") + " " +
                    (Environment.GetEnvironmentVariable("DESKTOP_SESSION") ?? "")).ToLowerInvariant();
        string[] trayDesktops = { "kde", "plasma", "cinnamon", "xfce", "mate", "budgie", "lxqt", "unity", "pantheon" };
        return trayDesktops.Any(hint.Contains);
    }
}

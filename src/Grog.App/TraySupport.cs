// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Linq;

namespace Grog.App;

/// <summary>Best-effort check for a system tray Grog can minimize to; picks the first-run default for the
/// Minimize-to-tray setting.</summary>
/// <remarks>Heuristic on XDG_CURRENT_DESKTOP: bare GNOME lacks StatusNotifierItem, so it counts as unsupported
/// to avoid defaulting anyone into a hidden-and-unreachable window. Tray creation still has its own fallback.</remarks>
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

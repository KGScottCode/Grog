// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Threading.Tasks;
using DesktopNotifications;
using DesktopNotifications.FreeDesktop;
using DesktopNotifications.Windows;

namespace Grog.App;

/// <summary>Desktop notifications behind one app-facing API: WinRT toast on Windows, freedesktop D-Bus on Linux;
/// no macOS backend, so <see cref="Available"/> is false there and callers fall back to the in-app toast.</summary>
/// <remarks>Best-effort throughout: init failures and unsupported platforms never throw. Windows toasts need a
/// Start-menu shortcut carrying the AppUserModelID, so callers keep the in-app toast as a fallback.</remarks>
public static class Notifier
{
    private static INotificationManager? _manager;

    /// <summary>Gated by the Settings toggle. When false, <see cref="Notify"/> is a no-op.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>True once an OS backend initialized -- i.e. real desktop notifications are possible here.</summary>
    public static bool Available => _manager is not null;

    public static async Task InitializeAsync()
    {
        try
        {
            INotificationManager? m =
                OperatingSystem.IsWindows() ? new WindowsNotificationManager(WindowsApplicationContext.FromCurrentProcess("Grog"))
              : OperatingSystem.IsLinux()   ? new FreeDesktopNotificationManager(FreeDesktopApplicationContext.FromCurrentProcess("Grog"))
              : null;   // macOS / other: no backend -> in-app fallback
            if (m is not null)
            {
                await m.Initialize();
                _manager = m;
            }
        }
        catch { _manager = null; }
    }

    /// <summary>Fire-and-forget an OS notification. No-ops (returns false) when disabled or unavailable, so the
    /// caller can fall back to the in-app toast.</summary>
    public static bool Notify(string title, string body)
    {
        if (!Enabled) return false;
        var m = _manager;
        if (m is null) return false;
        _ = SafeShow(m, title, body);
        return true;
    }

    private static async Task SafeShow(INotificationManager m, string title, string body)
    {
        try { await m.ShowNotification(new Notification { Title = title, Body = body }); }
        catch { /* best-effort */ }
    }
}

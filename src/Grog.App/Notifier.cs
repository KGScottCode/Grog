// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Threading.Tasks;
using DesktopNotifications;
using DesktopNotifications.FreeDesktop;
using DesktopNotifications.Windows;

namespace Grog.App;

public static class Notifier
{
    private static INotificationManager? _manager;

    public static bool Enabled { get; set; } = true;

    public static bool Available => _manager is not null;

    public static async Task InitializeAsync()
    {
        try
        {
            INotificationManager? m =
                OperatingSystem.IsWindows() ? new WindowsNotificationManager(WindowsApplicationContext.FromCurrentProcess("Grog"))
              : OperatingSystem.IsLinux()   ? new FreeDesktopNotificationManager(FreeDesktopApplicationContext.FromCurrentProcess("Grog"))
              : null;   
            if (m is not null)
            {
                await m.Initialize();
                _manager = m;
            }
        }
        catch { _manager = null; }
    }

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

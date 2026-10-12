// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Avalonia;
using System;

namespace Grog.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == Services.GtkBackendGuard.ProbeArg)
        {
            Environment.ExitCode = Services.GtkBackendGuard.RunProbe();
            return;
        }
        // One Grog per user
        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.SignalExisting();
            return;
        }
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { SingleInstance.Release(); }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(OperatingSystem.IsMacOS()
                ? new Avalonia.Media.FontManagerOptions
                {
                    FontFallbacks = new[]
                    {
                        new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Apple Symbols") },
                    }
                }
                : new Avalonia.Media.FontManagerOptions())
            .LogToTrace();
}

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
        // One Grog per user: if another instance is already running (likely hidden in the tray), tell it to
        // come to the foreground and exit this launch instead of spawning a duplicate.
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
            // macOS: the bundled Inter lacks the UI glyphs (carets, completeness pips, status marks - the
            // half-circle U+25D0 rendered as stacked bars, MacinCloud 2026-08-29) and Avalonia's implicit
            // per-codepoint fallback does not kick in for a custom default font there the way DirectWrite
            // does on Windows. Explicit fallbacks are consulted for codepoints the current font cannot
            // shape; Apple Symbols ONLY - it carries the geometric/technical glyphs and next to no Latin,
            // so even an eager fallback match can never hijack body text.
            .With(OperatingSystem.IsMacOS()
                ? new Avalonia.Media.FontManagerOptions
                {
                    FontFallbacks = new[]
                    {
                        new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Apple Symbols") },
                    }
                }
                : new Avalonia.Media.FontManagerOptions())
            // XAML hot reload in Debug comes from HotAvalonia's auto-enable weaver (see Grog.App.csproj);
            // an explicit .UseHotReload() here would be a duplicate and Fody warns about it.
            .LogToTrace();
}

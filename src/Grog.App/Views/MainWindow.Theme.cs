// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Grog.App.ViewModels;
using Grog.Core.Layout;

namespace Grog.App.Views;

// Theme follow-through: window icon and About scene swap.
public partial class MainWindow
{
    private void OnThemeChanged(GrogTheme theme) =>
        Dispatcher.UIThread.Post(() => { ApplyThemeIcon(theme); ApplyAboutScene(theme); });

    // (UI-thread sweep 09-06 r2) both PNGs used to be read + decoded on the dispatcher (constructor and every
    // theme change). The asset bytes / bitmap are now produced on a worker, cached per theme, and only the
    // cheap assignment happens on the UI thread. A generation counter makes the LAST requested theme win.
    private readonly System.Collections.Generic.Dictionary<GrogTheme, WindowIcon> _themeIcons = new();
    private readonly System.Collections.Generic.Dictionary<GrogTheme, Avalonia.Media.Imaging.Bitmap> _aboutScenes = new();
    private int _themeIconGen, _aboutSceneGen;

    private void ApplyThemeIcon(GrogTheme theme)
    {
        if (_themeIcons.TryGetValue(theme, out var cached)) { try { Icon = cached; } catch { } return; }
        var gen = ++_themeIconGen;
        var file = theme == GrogTheme.OdeToGog ? "grog-purple.png" : "grog-amber.png";
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            System.IO.MemoryStream? bytes = null;
            try
            {
                using var stream = AssetLoader.Open(new Uri($"avares://Grog/Assets/{file}"));
                bytes = new System.IO.MemoryStream();
                stream.CopyTo(bytes);
                bytes.Position = 0;
            }
            catch { bytes = null; }
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (bytes is null) return;
                    // The platform icon object itself is created here (Win32 handles belong to the UI thread);
                    // the asset read is what used to stall, and that already happened on the worker.
                    var icon = new WindowIcon(bytes);
                    _themeIcons[theme] = icon;
                    if (gen == _themeIconGen) Icon = icon;
                }
                catch { /* the window icon is cosmetic; never fail over a missing/locked asset */ }
                finally { bytes?.Dispose(); }
            });
        });
    }

    /// <summary>About's scene art follows the theme like the window icon: gold, purple-graded, or the deep
    /// Tavern cut. Swapped, not tinted -- no palette value fixes a bitmap.</summary>
    private void ApplyAboutScene(GrogTheme theme)
    {
        if (_aboutScenes.TryGetValue(theme, out var cached))
        {
            try { if (Find<Image>("AboutScene") is { } img0) img0.Source = cached; } catch { }
            return;
        }
        var gen = ++_aboutSceneGen;
        var file = theme switch
        {
            GrogTheme.OdeToGog => "tavern-landscape-purple.png",
            GrogTheme.Tavern => "tavern-landscape-deep.png",
            _ => "tavern-landscape.png",
        };
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            Avalonia.Media.Imaging.Bitmap? bmp = null;
            try
            {
                using var stream = AssetLoader.Open(new Uri($"avares://Grog/Assets/{file}"));
                bmp = new Avalonia.Media.Imaging.Bitmap(stream);   // decoded off the dispatcher
            }
            catch { bmp = null; }
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (bmp is null) return;
                    _aboutScenes[theme] = bmp;   // kept for the life of the window: three small scenes at most
                    if (gen != _aboutSceneGen) return;
                    if (Find<Image>("AboutScene") is not { } img) return;
                    img.Source = bmp;
                }
                catch { /* scene art is cosmetic; never fail over a missing asset */ }
            });
        });
    }
}

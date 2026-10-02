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

// System tray: icon, menu, attention escalation, drop-to-tray and restore.
public partial class MainWindow
{
    // ---- System tray ----
    private TrayIcon? _tray;
    // Highest missed-notification level currently painted on the tray icon (0 = normal amber).
    private int _trayAttention;

    // What the tray paint is ABOUT, replayed as a toast on restore so the user learns why without hunting.
    private readonly System.Collections.Generic.List<(int Level, string Body, Grog.App.ViewModels.ToastFix? FixNav)> _missedNotices = new();

    /// <summary>Paint the tray icon for a notification the user may have missed. Escalates only while the
    /// window is hidden or minimized, only ever upward, and resets on restore.</summary>

    private void EscalateTrayAttention(int level, string body, Grog.App.ViewModels.ToastFix? fixNav = null)
    {
        bool couldSee = IsVisible && WindowState != WindowState.Minimized && IsActive;
        if (couldSee || _tray is null) return;
        _missedNotices.Add((level, body, fixNav));
        if (level <= _trayAttention) return;
        _trayAttention = level;
        SetTrayIcon(level >= MainWindowViewModel.TrayAttentionProblem ? "grog-red.png" : "grog-orange.png");
    }

    private void ResetTrayAttention()
    {
        if (_trayAttention == 0) return;
        _trayAttention = 0;
        SetTrayIcon("grog-amber.png");
        // Answer "why was it orange/red" the moment the user is back, then forget; the Activity log keeps
        // the durable record.
        (DataContext as MainWindowViewModel)?.ShowMissedNotices(_missedNotices.ToArray());
        _missedNotices.Clear();
    }

    private void SetTrayIcon(string asset)
    {
        try
        {
            if (_tray is null) return;
            using var s = AssetLoader.Open(new Uri($"avares://Grog/Assets/{asset}"));
            _tray.Icon = new WindowIcon(s);
        }
        catch { /* icon is cosmetic; a missing asset must not sink the tray */ }
    }
    // Set true once the user confirms "Close & pause" so the Closing handler lets the second close through.
    private bool _forceClose;

    /// <summary>True while the minimize-to-tray handler runs, so its own WindowState writes cannot re-enter
    /// it (X11 reports state changes back).</summary>
    private bool _handlingMinimizeToTray;

    /// <summary>Hide to the tray and reset the state so the next restore is not minimized. Shared by the
    /// minimize gesture and by an X-close when Settings says the window should stay running.</summary>
    // macOS: AppKit's deminiaturize runs AFTER our Hide() and orders the window back to front (1012
    // trace: DropToTray hid, then Minimized->Normal->Maximized re-showed it). For a short window after
    // a deliberate hide, any state transition triggers a re-hide - the animation loses the argument.
    private DateTime _macRehideUntil = DateTime.MinValue;

    private void DropToTray(MainWindowViewModel vm)
    {
        if (OperatingSystem.IsMacOS()) _macRehideUntil = DateTime.UtcNow.AddSeconds(1.5);
        _handlingMinimizeToTray = true;
        try
        {
            Hide();
            WindowState = WindowState.Normal;
            vm.ReleaseArtForTray();
            TrimWorkingSetSoon();
        }
        finally { _handlingMinimizeToTray = false; }
    }

    /// <summary>Create the tray icon + menu (Open / Back Up Now / Quit) via Avalonia's TrayIcon; a desktop
    /// with no tray host degrades to a plain close. Hidden until Minimize-to-tray turns it on.</summary>
    private void SetupTray()
    {
        try
        {
            _tray = new TrayIcon { ToolTipText = "Grog - GOG library backup", IsVisible = false };
            try
            {
                using var s = AssetLoader.Open(new Uri("avares://Grog/Assets/grog-amber.png"));
                _tray.Icon = new WindowIcon(s);
            }
            catch { /* icon is cosmetic; a missing asset must not sink the tray */ }

            var menu = new NativeMenu();
            var open = new NativeMenuItem("Open Grog");
            open.Click += (_, _) => RestoreFromTray();
            // "Back Up Now", not "Sync now": the tray item runs the PRIMARY action, and "sync" would read
            // like a two-way mirror, which Grog is not.
            var sync = new NativeMenuItem("Back Up Now");
            sync.Click += (_, _) => (DataContext as MainWindowViewModel)?.PrimaryActionCommand.Execute(null);
            var quit = new NativeMenuItem("Quit Grog");
            quit.Click += (_, _) => (DataContext as MainWindowViewModel)?.RequestQuit();   // QuitAsync pauses first and brings the window back if it must wait
            menu.Items.Add(open);
            menu.Items.Add(sync);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);
            _tray.Menu = menu;
            _tray.Clicked += (_, _) => RestoreFromTray();   // left-click the icon restores the window

            if (Application.Current is { } app)
                TrayIcon.SetIcons(app, new TrayIcons { _tray });
        }
        catch { _tray = null; /* no tray host: leave close behaving normally */ }
    }

    private void RestoreFromTray()
    {
        _macRehideUntil = DateTime.MinValue;   // a real restore outranks any pending re-hide
        ResetTrayAttention();   // the user is back; the missed-notification paint has done its job
        Show();
        WindowState = WindowState.Normal;
        Activate();
        (DataContext as MainWindowViewModel)?.ReloadArtAfterTray();
    }

    /// <summary>Bring the window to the foreground when a second launch asks the running instance to
    /// surface (single-instance): a tray restore plus a topmost nudge.</summary>
    public void BringToForeground()
    {
        _macRehideUntil = DateTime.MinValue;   // a real restore outranks any pending re-hide
        RestoreFromTray();
        // Say WHICH copy this is: a portable and an installed copy look identical, so surfacing one
        // silently reads as "the app I launched ignored me".
        (DataContext as MainWindowViewModel)?.NotifyAlreadyRunning();
        // Briefly toggle Topmost then drop it back: Activate() alone is often ignored by the OS when
        // another app owns the foreground.
        try { Topmost = true; Topmost = false; } catch { /* cosmetic */ }
    }

    /// <summary>Shrink the resident footprint after a hide: collect, compact the LOH once, and on Windows
    /// hand freed pages back via SetProcessWorkingSetSize(-1,-1). Best-effort, never load-bearing.</summary>
    // (UI-thread sweep 09-06 r2) the blocking full GC + LOH compaction used to run on the dispatcher; a
    // worker does it now (the GC is process-wide either way, and the window is hidden, so nothing waits).
    private void TrimWorkingSetSoon(int delayMs = 750)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await System.Threading.Tasks.Task.Delay(delayMs); } catch { }
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
                if (OperatingSystem.IsWindows())
                    SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
            }
            catch { /* best-effort */ }
        });
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minSize, IntPtr maxSize);
}

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

// System tray
public partial class MainWindow
{
    private TrayIcon? _tray;
    private int _trayAttention;
    private readonly System.Collections.Generic.List<(int Level, string Body, Grog.App.ViewModels.ToastFix? FixNav)> _missedNotices = new();


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
    private bool _forceClose;

    private bool _handlingMinimizeToTray;

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
        _macRehideUntil = DateTime.MinValue;   
        ResetTrayAttention();   
        Show();
        WindowState = WindowState.Normal;
        Activate();
        (DataContext as MainWindowViewModel)?.ReloadArtAfterTray();
    }

    public void BringToForeground()
    {
        _macRehideUntil = DateTime.MinValue;   
        RestoreFromTray();
        (DataContext as MainWindowViewModel)?.NotifyAlreadyRunning();
        try { Topmost = true; Topmost = false; } catch { /* cosmetic */ }
    }

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

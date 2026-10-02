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

// Close-confirmation overlay handlers.
public partial class MainWindow
{
    // ---- Close-confirmation overlay (shown only when an X-close would interrupt active work) ----
    private void OnCloseConfirmCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.ShowCloseConfirm = false;
    }

    private void OnCloseConfirmClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.ShowCloseConfirm = false;
            if (vm.RememberCloseChoice) vm.Settings.CloseAction = CloseWindowAction.Quit;
            vm.PauseAllForShutdown();
            // Quit the APPLICATION, not the window: on macOS Window.Close() from this dialog did
            // nothing (MacinCloud 2026-08-28) while the app-level shutdown - the identical path Dock
            // Quit takes - works everywhere. RequestQuit sets Quitting first, so the Closing
            // interceptor stands aside on every platform.
            vm.RequestQuit();
            return;
        }
        _forceClose = true;
        Close();
    }

    private void OnCloseConfirmMinimize(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        // Belt to the view's IsVisible: with no tray there is no icon to come back through, so never hide.
        if (!vm.Settings.TrayAvailable || _tray is null) return;
        vm.ShowCloseConfirm = false;
        if (vm.RememberCloseChoice) vm.Settings.CloseAction = CloseWindowAction.MinimizeToTray;
        // The user chose the tray for this close, so ensure the tray icon is up even if the setting is
        // off -- otherwise the window would vanish unreachably.
        if (_tray is not null) _tray.IsVisible = true;
        Hide();
        vm.ReleaseArtForTray();
        TrimWorkingSetSoon();
    }
}

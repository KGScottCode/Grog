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

// Close-confirmation overlay handlers
public partial class MainWindow
{
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
            vm.RequestQuit();
            return;
        }
        _forceClose = true;
        Close();
    }

    private void OnCloseConfirmMinimize(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (!vm.Settings.TrayAvailable || _tray is null) return;
        vm.ShowCloseConfirm = false;
        if (vm.RememberCloseChoice) vm.Settings.CloseAction = CloseWindowAction.MinimizeToTray;
        if (_tray is not null) _tray.IsVisible = true;
        Hide();
        vm.ReleaseArtForTray();
        TrimWorkingSetSoon();
    }
}

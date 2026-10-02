// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Grog.App.ViewModels;

namespace Grog.App.Views.Pages;

public partial class OverviewPage : UserControl
{
    private OverviewViewModel? _vm;

    public OverviewPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.ShowWontFitRequested -= OnShowWontFit;
            // The page's DataContext is the shell; the OverviewViewModel is rebased onto the root Grid (S3.3). Casting
            // the page's own context to OverviewViewModel was always null, so this event was never heard (09-17).
            _vm = (DataContext as MainWindowViewModel)?.Overview;
            if (_vm is not null) _vm.ShowWontFitRequested += OnShowWontFit;
        };
    }

    // Scroll the (virtualized) waiting list to a tail index. The list is the ScrollViewer's direct content, so
    // ScrollIntoView realizes the row and brings it to the nearest edge. Show me then centers it: the last rows
    // that fit above the divider, the red rows below. Posted, so a rebuild raised by the same click is laid out first.
    private void OnShowWontFit(int tailIndex, bool center) => Dispatcher.UIThread.Post(() =>
    {
        WaitingList.ScrollIntoView(tailIndex);
        if (!center || WaitingList.Parent is not ScrollViewer sv) return;
        if (WaitingList.ContainerFromIndex(tailIndex) is not Control row) return;
        if (row.TranslatePoint(default, WaitingList) is not { } top) return;
        double y = top.Y + WaitingList.Margin.Top - (sv.Viewport.Height - row.Bounds.Height) / 2;
        sv.Offset = new Vector(sv.Offset.X, Math.Clamp(y, 0, Math.Max(0, sv.Extent.Height - sv.Viewport.Height)));
    }, DispatcherPriority.Loaded);
}

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

// Custom frameless window chrome: title bar drag, caption buttons, client-side edge resize.
public partial class MainWindow
{
    // Client-side edge resize (Linux only): WindowDecorations.None removes the WM's resize borders, so a
    // 6px band inside every edge maps to BeginResizeDrag. Cursors are cached; the handler runs per move.
    private const double ResizeBand = 6;
    private static readonly Cursor CurWE = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor CurNS = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor CurNW = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor CurNE = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor CurSW = new(StandardCursorType.BottomLeftCorner);
    private static readonly Cursor CurSE = new(StandardCursorType.BottomRightCorner);

    private void EnableEdgeResize()
    {
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (WindowState != WindowState.Normal) return;   // a maximized window has no edges to drag
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (EdgeAt(e.GetPosition(this)) is { } edge) { BeginResizeDrag(edge, e); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (_, e) =>
        {
            var edge = WindowState == WindowState.Normal ? EdgeAt(e.GetPosition(this)) : null;
            Cursor = edge switch
            {
                WindowEdge.West or WindowEdge.East   => CurWE,
                WindowEdge.North or WindowEdge.South => CurNS,
                WindowEdge.NorthWest => CurNW,
                WindowEdge.NorthEast => CurNE,
                WindowEdge.SouthWest => CurSW,
                WindowEdge.SouthEast => CurSE,
                _ => Cursor.Default,
            };
        }, RoutingStrategies.Tunnel);
    }

    private WindowEdge? EdgeAt(Point p)
    {
        var b = Bounds;
        bool l = p.X < ResizeBand, r = p.X > b.Width - ResizeBand;
        bool t = p.Y < ResizeBand, bo = p.Y > b.Height - ResizeBand;
        if (t && l) return WindowEdge.NorthWest;
        if (t && r) return WindowEdge.NorthEast;
        if (bo && l) return WindowEdge.SouthWest;
        if (bo && r) return WindowEdge.SouthEast;
        if (l) return WindowEdge.West;
        if (r) return WindowEdge.East;
        if (t) return WindowEdge.North;
        if (bo) return WindowEdge.South;
        return null;
    }

    // ---- Custom frameless title bar (window chrome we draw ourselves) ----
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) { OnMaxRestore(sender, e); return; }   // double-click the bar = maximize/restore
        BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestore(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // Close routes through the normal Closing handler -> "Confirm Close or Minimize?" + pause-in-flight.
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

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

// Drag-to-reorder for the two queues
public partial class MainWindow
{
    private void WireRowReordering<T>(string listName,
                                      Func<MainWindowViewModel, System.Collections.IList> tail,
                                      Action<MainWindowViewModel, T, int> drop,
                                      Func<MainWindowViewModel, T, object, bool>? canDrop = null) where T : class
    {
        var list = Find<ItemsControl>(listName);
        if (list is null) return;

        T? dragging = null;
        Point start = default;
        bool moved = false;
        Control? dimmed = null;
        IDropTargetRow? lit = null;   // the row painting the insertion line

        var scroller = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        double lastY = 0;
        scroller.Tick += (_, _) =>
        {
            if (dragging is null || !moved) { scroller.Stop(); return; }
            if (list.FindAncestorOfType<ScrollViewer>() is not { } sv) { scroller.Stop(); return; }
            double yInSv = lastY - sv.Offset.Y;
            const double Zone = 36, Step = 14;
            double dy = yInSv < Zone ? -Step : yInSv > sv.Viewport.Height - Zone ? Step : 0;
            if (dy == 0) return;
            double before = sv.Offset.Y;
            sv.Offset = new Vector(sv.Offset.X, System.Math.Clamp(before + dy, 0, System.Math.Max(0, sv.Extent.Height - sv.Viewport.Height)));
            if (sv.Offset.Y == before) return;
            lastY += sv.Offset.Y - before;   // the pointer is still; the list moved under it
            int target = IndexAtY(list, lastY);
            if (target >= 0) DrawLine(target, IndexOfRow(list, dragging));
        };

        void EndDrag()
        {
            scroller.Stop();
            if (dimmed is not null) { dimmed.Opacity = 1; dimmed = null; }
            ClearLine();
            list.Cursor = Cursor.Default;
            dragging = null;
            moved = false;
        }

        void ClearLine() { if (lit is not null) { lit.DropLineAbove = false; lit.DropLineBelow = false; lit = null; } }
        void DrawLine(int targetIdx, int fromIdx)
        {
            var rows = DataContext is MainWindowViewModel vm0 ? tail(vm0) : null;
            if (rows is null || targetIdx < 0 || targetIdx >= rows.Count || rows[targetIdx] is not IDropTargetRow target) { ClearLine(); return; }
            if (canDrop is not null && dragging is not null && DataContext is MainWindowViewModel vm1 && !canDrop(vm1, dragging, rows[targetIdx]!)) { ClearLine(); return; }
            bool above = targetIdx <= fromIdx;
            if (!ReferenceEquals(lit, target)) ClearLine();
            lit = target;
            target.DropLineAbove = above; target.DropLineBelow = !above;
        }

        list.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
            if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
            dragging = RowUnder<T>(e.Source);
            start = e.GetPosition(list);
            moved = false;
            if (dragging is not null) e.Pointer.Capture(list);
        }, RoutingStrategies.Tunnel);

        list.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (dragging is null) return;
            double y = e.GetPosition(list).Y;
            if (!moved && System.Math.Abs(y - start.Y) > 8)
            {
                moved = true;
                list.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
                int from = IndexOfRow(list, dragging);
                if (from >= 0 && list.ContainerFromIndex(from) is Control c) { dimmed = c; c.Opacity = 0.45; }
            }
            if (!moved) return;
            lastY = y;
            if (!scroller.IsEnabled) scroller.Start();
            int target = IndexAtY(list, y);
            if (target >= 0) DrawLine(target, IndexOfRow(list, dragging));
        }, RoutingStrategies.Tunnel);

        list.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            var row = dragging;
            bool wasMoved = moved;
            double y = e.GetPosition(list).Y;
            EndDrag();
            if (row is null || !wasMoved || DataContext is not MainWindowViewModel vm) return;

            int targetIdx = IndexAtY(list, y);
            var rows = tail(vm);
            if (targetIdx < 0 || targetIdx >= rows.Count) return;
            if (ReferenceEquals(rows[targetIdx], row)) return;
            if (canDrop is not null && !canDrop(vm, row, rows[targetIdx]!)) return;

            drop(vm, row, targetIdx);
        }, RoutingStrategies.Tunnel);

        list.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Tunnel);

        static T? RowUnder<TRow>(object? source) where TRow : class
        {
            var c = source as Control;
            while (c is not null)
            {
                if (c.DataContext is T r) return r;
                c = c.Parent as Control;
            }
            return null;
        }

        static int IndexOfRow(ItemsControl list, T row)
        {
            for (int i = 0; i < list.ItemCount; i++)
                if (list.ContainerFromIndex(i) is Control c && ReferenceEquals(c.DataContext, row)) return i;
            return -1;
        }
    }

    private static int IndexAtY(ItemsControl list, double y)
    {
        int last = -1;
        for (int i = 0; i < list.ItemCount; i++)
        {
            if (list.ContainerFromIndex(i) is Control c)
            {
                var top = c.TranslatePoint(new Point(0, 0), list)?.Y;
                if (top is null) continue;
                if (y < top.Value + c.Bounds.Height) return i;
                last = i;
            }
        }
        return last;
    }
}

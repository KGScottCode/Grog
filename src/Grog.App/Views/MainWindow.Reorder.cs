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

// Drag-to-reorder for the two waiting queues (downloads, moves).
public partial class MainWindow
{
    /// <summary>Drag-to-reorder for a waiting queue: position IS priority, and a drop into the active slots
    /// bumps the running item back to waiting (its partial survives).</summary>
    /// <remarks>Pointer-based, not the DragDrop API (in-list reorder, no payload). ONE implementation for
    /// BOTH queues -- the download and move queues are the same gesture and must move together. The list may
    /// hold rows of other types (the download tail's won't-fit divider): only a T row starts a drag, any row
    /// is a drop target, and the drop callback maps the visual index onto the persisted order.</remarks>
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

        // (09-16) Auto-scroll while a drag sits near the top or bottom of the visible list, so a row can be carried
        // past the viewport instead of stopping at its edge. The timer runs only during a drag.
        var scroller = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        double lastY = 0;
        scroller.Tick += (_, _) =>
        {
            if (dragging is null || !moved) { scroller.Stop(); return; }
            if (list.FindAncestorOfType<ScrollViewer>() is not { } sv) { scroller.Stop(); return; }
            // Edge zone in the ScrollViewer's own coordinates (the list is its direct content).
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

        // Draws WHERE IT WILL LAND while the pointer moves, so a live drag never looks dead. The line is a flag on
        // the TARGET ROW, painted by its own template (09-16): the adorner it replaced was placed by list-space
        // coordinates and never showed once the list was scrolled (owner-measured, repeatable across restarts).
        void ClearLine() { if (lit is not null) { lit.DropLineAbove = false; lit.DropLineBelow = false; lit = null; } }
        void DrawLine(int targetIdx, int fromIdx)
        {
            var rows = DataContext is MainWindowViewModel vm0 ? tail(vm0) : null;
            if (rows is null || targetIdx < 0 || targetIdx >= rows.Count || rows[targetIdx] is not IDropTargetRow target) { ClearLine(); return; }
            // A target the list refuses (a fitting row over the won't-fit group, 09-16) draws no line: the
            // gesture has no meaning there, and no line is the honest signal before a pointless drop.
            if (canDrop is not null && dragging is not null && DataContext is MainWindowViewModel vm1 && !canDrop(vm1, dragging, rows[targetIdx]!)) { ClearLine(); return; }
            // The line marks the BOUNDARY the row lands on: above the target moving up, below it moving
            // down -- one fixed edge would point at the wrong gap half the time.
            bool above = targetIdx <= fromIdx;
            if (!ReferenceEquals(lit, target)) ClearLine();
            lit = target;
            target.DropLineAbove = above; target.DropLineBelow = !above;
        }

        list.AddHandler(PointerPressedEvent, (_, e) =>
        {
            // NEVER start a drag from a press on a button (the row's cancel X): capturing the pointer to
            // the list robs the button of its press-release cycle, so Click silently never fires -- the X
            // looked pressed and removed nothing (caught on macOS 2026-08-29, but broken everywhere).
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
            // Left button only: a right-click opens the row's menu and must not capture the pointer (09-16).
            if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
            dragging = RowUnder<T>(e.Source);
            start = e.GetPosition(list);
            moved = false;
            // Capture, so a drag straying outside the list keeps reporting instead of freezing at the edge.
            if (dragging is not null) e.Pointer.Capture(list);
        }, RoutingStrategies.Tunnel);

        list.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (dragging is null) return;
            double y = e.GetPosition(list).Y;
            // Slop so a click never becomes an accidental reorder. 8px, not 4 (owner 09-02: a touchpad
            // click drifted into a drag, which also reset the sticky sort to Queue order).
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

            // Resolve the drop target by vertical position against the realized rows, so a drop that lands in
            // the gap between rows (or below the last one) still reorders instead of being a silent no-op.
            int targetIdx = IndexAtY(list, y);
            var rows = tail(vm);
            if (targetIdx < 0 || targetIdx >= rows.Count) return;
            if (ReferenceEquals(rows[targetIdx], row)) return;
            if (canDrop is not null && !canDrop(vm, row, rows[targetIdx]!)) return;
            // A drop onto a row of another kind (the won't-fit divider) is still a valid target: the drop
            // callback maps it onto the persisted order.

            drop(vm, row, targetIdx);
        }, RoutingStrategies.Tunnel);

        // A lost capture (deactivation, a dialog) must not leave a row dimmed and a line painted forever.
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

    /// <summary>Index of the realized row whose vertical span contains <paramref name="y"/>; the last row
    /// when below everything, -1 when empty. Lets a drop in the padding between rows still resolve.</summary>
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

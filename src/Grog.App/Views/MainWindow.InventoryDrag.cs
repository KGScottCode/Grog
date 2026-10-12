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

// Storage page device grids
public partial class MainWindow
{
    private void WireInventoryDrag()
    {
        WireGrid("PrimaryList", "SecondaryList", primary: true);
        WireGrid("SecondaryList", "PrimaryList", primary: false);
    }

    private void WireGrid(string listName, string otherName, bool primary)
    {
        var list = Find<ItemsControl>(listName);
        if (list is null) return;
        var otherCard = Find<Border>(primary ? "GuideSecondaryFolder" : "GuidePrimaryFolder");

        bool dragging = false;
        bool armed = false;
        Point start = default;

        list.AddHandler(PointerPressedEvent, (_, e) =>
        {
            start = e.GetPosition(this);
            dragging = false;
            var row = InventoryRowUnder(e.Source);
            var grp = row is null && !IsCaretHit(e.Source) ? GroupUnder(e.Source) : null;
            armed = DataContext is MainWindowViewModel vm0
                && (row is not null ? SideOf(vm0, primary).Selected.Contains(row)
                  : grp is not null && grp.Files.Count > 0 && grp.Files.All(f => SideOf(vm0, primary).Selected.Contains(f)));
        }, RoutingStrategies.Tunnel);

        list.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (!armed || DataContext is not MainWindowViewModel vm || SideOf(vm, primary).Selected.Count == 0) return;
            var d = e.GetPosition(this) - start;
            if (!dragging && (System.Math.Abs(d.X) > 8 || System.Math.Abs(d.Y) > 8))
            {
                dragging = true;
                Cursor = new Cursor(StandardCursorType.DragMove);
            }
            if (dragging && otherCard is not null)
            {
                var other = Find<ItemsControl>(otherName);
                bool over = other is not null && PointIsInside(other, e.GetPosition(this));
                if (over != otherCard.Classes.Contains("droptarget"))
                {
                    if (over) otherCard.Classes.Add("droptarget");
                    else otherCard.Classes.Remove("droptarget");
                }
            }
        }, RoutingStrategies.Tunnel);

        list.AddHandler(PointerReleasedEvent, async (_, e) =>
        {
            bool wasDragging = dragging;
            dragging = false;
            armed = false;
            Cursor = Cursor.Default;
            otherCard?.Classes.Remove("droptarget");
            if (DataContext is not MainWindowViewModel vm) return;

            if (wasDragging)
            {
                var other = Find<ItemsControl>(otherName);
                if (other is not null && PointIsInside(other, e.GetPosition(this)))
                {
                    var src = SideOf(vm, primary);
                    var targetRoot = primary ? vm.Storage.SecondaryGrid.RootId : vm.Storage.PrimaryGrid.RootId;
                    await vm.Storage.MoveInventoryTo(src, targetRoot);
                }
                return;
            }

            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (IsCaretHit(e.Source) && GroupUnder(e.Source) is { } cg) { vm.Storage.ToggleGroupExpanded(cg); return; }
            if (GroupUnder(e.Source) is { } g) { vm.Storage.ClickGroup(g, ctrl, shift); return; }
            if (InventoryRowUnder(e.Source) is { } f) { vm.Storage.ClickFile(f, ctrl, shift); }
        }, RoutingStrategies.Tunnel);
    }

    private static DeviceGridViewModel SideOf(MainWindowViewModel vm, bool primary) => primary ? vm.Storage.PrimaryGrid : vm.Storage.SecondaryGrid;

    private bool PointIsInside(Control c, Point p)
    {
        var tl = c.TranslatePoint(new Point(0, 0), this);
        return tl is not null && new Rect(tl.Value, c.Bounds.Size).Contains(p);
    }

    private static InventoryRow? InventoryRowUnder(object? source)
    {
        var c = source as Control;
        while (c is not null)
        {
            if (c.DataContext is InventoryRow r) return r;
            c = c.Parent as Control;
        }
        return null;
    }

    private static InventoryGroupRow? GroupUnder(object? source)
    {
        var c = source as Control;
        while (c is not null)
        {
            if (c.DataContext is InventoryGroupRow g) return g;
            if (c.DataContext is InventoryRow or InventoryElsewhereRow) return null;   // inside a file row, not the group header
            c = c.Parent as Control;
        }
        return null;
    }

    private static bool IsCaretHit(object? source)
    {
        var c = source as Control;
        while (c is not null)
        {
            if (c is TextBlock { Tag: "caret" }) return true;
            if (c.DataContext is InventoryGroupRow or InventoryRow or InventoryElsewhereRow) return false;
            c = c.Parent as Control;
        }
        return false;
    }
}

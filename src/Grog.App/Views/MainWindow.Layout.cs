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

// Window and grid layout persistence: size/position restore, column widths, capture on close.
public partial class MainWindow
{
    private void RestoreWindowSize()
    {
        if (FixedStartupWindow)
        {
            Width = 1920; Height = 1080; WindowState = WindowState.Normal;
            // Pin to the primary screen's top-left so 1920x1080 is not center-placed off a 1080p display;
            // WindowStartupLocation must be Manual for Position to hold.
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(0, 0);
            return;
        }
        if (_settings.WindowWidth is { } w)
            Width = WindowLayoutMath.ClampDimension(w, WindowLayoutMath.MinWindowWidth, Width);
        if (_settings.WindowHeight is { } h)
            Height = WindowLayoutMath.ClampDimension(h, WindowLayoutMath.MinWindowHeight, Height);
        // Only adopt a saved position that is still on a connected screen; coordinates from a
        // since-unplugged monitor would open the window off-desktop.
        if (_settings.WindowPosX is { } px && _settings.WindowPosY is { } py)
        {
            var p = new PixelPoint(px, py);
            if (Screens.All.Any(sc => sc.Bounds.Contains(p)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = p;
            }
        }
        if (_settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    /// <summary>Mirrors LibraryViewModel.ShowOwnerColumn onto the Library grid's Owner column.</summary>
    private void SyncOwnerColumn(MainWindowViewModel vm)
    {
        if (Find<DataGrid>("LibraryGrid") is not { } libraryGrid) return;
        foreach (var c in libraryGrid.Columns)
            if (c.SortMemberPath == "OwnerSortKey")
            {
                // Each cell's ItemsControl reserves the full-stack MinWidth (GameRowViewModel.OwnerCellMinWidth)
                // so Auto never undershoots; only the visibility flag is code-behind (no IsVisible binding).
                c.IsVisible = vm.Library.ShowOwnerColumn;
                break;
            }
    }

    /// <summary>Bump when the Library grid's columns change shape or a width changes kind; without the bump,
    /// restore-on-Opened re-applies stale saved widths over the new layout every launch.</summary>
    private const int GridLayoutVersion = 5;

    private void RestoreColumnWidths(DataGrid? grid)
    {
        if (grid is null) return;
        if (_settings.GridColumnLayoutVersion != GridLayoutVersion) { _settings.GridColumnWidths = null; return; }
        var saved = _settings.GridColumnWidths;
        if (!WindowLayoutMath.ColumnWidthsApply(saved, grid.Columns.Count)) return;
        for (var i = 0; i < grid.Columns.Count; i++)
            if (WindowLayoutMath.ShouldApplyColumnWidth(saved![i]))
                grid.Columns[i].Width = new DataGridLength(saved[i]);
    }


    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // (UI-thread sweep 09-06 r2) the Closing handlers (raised by base.OnClosing) cancel most X presses --
        // confirm dialog, drop-to-tray, quit re-raise -- and each one used to pay a settings Flush+Load+Save
        // on the dispatcher. Persist the layout only for a close that actually goes through; the quit path
        // re-raises Closing un-cancelled, so the final write still happens exactly as before.
        base.OnClosing(e);
        if (!e.Cancel) CaptureLayout();
    }

    private void CaptureLayout()
    {
        // Read-modify-write against the current file: the ViewModel owns a separate AppSettings instance
        // and saves prefs mid-session, so touch only the layout fields here.
        AppSettings.FlushPendingSave();   // (UI-thread sweep 09-06) a debounced save is newer than the file we are about to read
        var s = AppSettings.Load();

        // While the fixed-size harness is on, never persist the window layout or the forced size would
        // overwrite the user's real saved layout; column widths still persist normally.
        if (!FixedStartupWindow)
        {
            if (WindowState == WindowState.Maximized)
            {
                // Keep the last known normal size/position as the restore layout; just remember maximized.
                s.WindowMaximized = true;
            }
            else
            {
                s.WindowMaximized = false;
                s.WindowWidth = Bounds.Width;
                s.WindowHeight = Bounds.Height;
                s.WindowPosX = Position.X;
                s.WindowPosY = Position.Y;
            }
        }

        var grid = Find<DataGrid>("LibraryGrid");
        if (grid is not null)
        {
            s.GridColumnWidths = grid.Columns
                .Select(c => c.Width.IsStar ? WindowLayoutMath.StarColumn : c.ActualWidth)
                .ToArray();
            s.GridColumnLayoutVersion = GridLayoutVersion;
        }

        s.Save();
    }
}

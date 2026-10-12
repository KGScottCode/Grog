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

// Window and grid layout persistence
public partial class MainWindow
{
    private void RestoreWindowSize()
    {
        if (FixedStartupWindow)
        {
            Width = 1920; Height = 1080; WindowState = WindowState.Normal;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(0, 0);
            return;
        }
        if (_settings.WindowWidth is { } w)
            Width = WindowLayoutMath.ClampDimension(w, WindowLayoutMath.MinWindowWidth, Width);
        if (_settings.WindowHeight is { } h)
            Height = WindowLayoutMath.ClampDimension(h, WindowLayoutMath.MinWindowHeight, Height);
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

    private void SyncOwnerColumn(MainWindowViewModel vm)
    {
        if (Find<DataGrid>("LibraryGrid") is not { } libraryGrid) return;
        foreach (var c in libraryGrid.Columns)
            if (c.SortMemberPath == "OwnerSortKey")
            {
                c.IsVisible = vm.Library.ShowOwnerColumn;
                break;
            }
    }

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
        base.OnClosing(e);
        if (!e.Cancel) CaptureLayout();
    }

    private void CaptureLayout()
    {
        AppSettings.FlushPendingSave();   // a debounced save is newer than the file we are about to read
        var s = AppSettings.Load();

        if (!FixedStartupWindow)
        {
            if (WindowState == WindowState.Maximized)
            {
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

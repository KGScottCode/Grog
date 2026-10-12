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

// Library filter pills
public partial class MainWindow
{
    private ScrollViewer? _pillScroll;
    private Control? _pillBar;
    private readonly double[] _pillNat = { double.NaN, double.NaN, double.NaN };
    private double _pillNatAt = double.NaN;
    private double _pillActedNat = double.NaN;

    private void WireChromeWidth()
    {
        UpdateChromeWidth();
        PropertyChanged += (_, e) => { if (e.Property == Visual.BoundsProperty) UpdateChromeWidth(); };
    }

    private void UpdateChromeWidth()
    {
        if (DataContext is MainWindowViewModel vm && Bounds.Width > 1)
            vm.NarrowChrome = Bounds.Width < MainWindowViewModel.NarrowChromeWidth;
    }

    private void WirePillResponsiveness()
    {
        if (!TryBindPillControls()) return;
    }

    private bool TryBindPillControls()
    {
        if (_pillBound) return true;
        _pillScroll = Find<ScrollViewer>("PillScroll");
        _pillBar = Find<Control>("PillBar");
        if (_pillScroll is null || _pillBar is null) return false;
        _pillScroll.PropertyChanged += OnPillMetricChanged;
        _pillBar.PropertyChanged += OnPillMetricChanged;
        _pillBound = true;
        return true;
    }
    private bool _pillBound;

    private void OnPillMetricChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty) RecomputePillMode();
    }

    private void RecomputePillMode()
    {
        if (DataContext is not MainWindowViewModel vm || _pillScroll is null || _pillBar is null) return;
        double avail = _pillScroll.Viewport.Width;
        if (avail <= 1) return;   // pre-layout; wait for a real size

        if (double.IsNaN(_pillNatAt) || Math.Abs(_pillNatAt - avail) > 1)
        {
            _pillNat[0] = _pillNat[1] = _pillNat[2] = double.NaN;
            _pillNatAt = avail;
        }

        int cur = (int)vm.Library.PillMode;
        double nat = _pillBar.Bounds.Width;
        if (!double.IsNaN(_pillActedNat) && Math.Abs(nat - _pillActedNat) < 0.5) return;
        _pillActedNat = double.NaN;
        if (nat > 1) _pillNat[cur] = nat;   // record this mode's natural width, at THIS viewport

        const double eps = 0.5;
        if (nat > avail + eps)
        {
            if (cur < 2) { _pillActedNat = nat; vm.Library.PillMode = (LibraryViewModel.PillDisplayMode)(cur + 1); }
        }
        else if (cur > 0)
        {
            double widerW = _pillNat[cur - 1];
            if (double.IsNaN(widerW) || widerW <= avail + eps)
            { _pillActedNat = nat; vm.Library.PillMode = (LibraryViewModel.PillDisplayMode)(cur - 1); }
        }
    }
}

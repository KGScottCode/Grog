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

// Library filter pills: responsive collapse to icons when the bar cannot fit the labels.
public partial class MainWindow
{
    // ---- Responsive filter pills ----
    private ScrollViewer? _pillScroll;
    private Control? _pillBar;
    // Learned natural width of the pill row in each mode (Full/NameOnly/IconOnly), indexed by PillDisplayMode.
    // NaN = not yet measured. Widths are constant per mode, so once learned the choice is stable (no flicker).
    private readonly double[] _pillNat = { double.NaN, double.NaN, double.NaN };
    /// <summary>The viewport the cached natural widths were measured at; a width recorded at a different
    /// window size is not evidence about this one, so a viewport change invalidates the cache.</summary>
    private double _pillNatAt = double.NaN;
    /// <summary>The width last ACTED on: right after a mode change the next bounds event still reports the
    /// OLD mode's width, so readings identical to it are ignored rather than filed under the new mode.</summary>
    private double _pillActedNat = double.NaN;

    /// <summary>Queue sort pills follow the WINDOW width, not a measured row: there are four of them and
    /// only two long labels, so a natural-width probe like the Library bar's would be machinery for nothing.</summary>
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

    /// <summary>Drive the three-stage responsive pill layout: the row's NATURAL width (it lives in a
    /// ScrollViewer) versus the viewport decides fit; stepping ONE mode at a time converges, no oscillation.</summary>
    private void WirePillResponsiveness()
    {
        // LAZY by necessity: the Library visual tree does not exist at window construction (Overview is
        // home), so retry until the controls exist, then subscribe once.
        if (!TryBindPillControls()) return;
    }

    /// <summary>Resolve the pill controls and subscribe, once they exist. Returns false while the Library
    /// page has never been shown.</summary>
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

        // A new viewport invalidates every cached measurement -- they describe a different layout.
        if (double.IsNaN(_pillNatAt) || Math.Abs(_pillNatAt - avail) > 1)
        {
            _pillNat[0] = _pillNat[1] = _pillNat[2] = double.NaN;
            _pillNatAt = avail;
        }

        int cur = (int)vm.Library.PillMode;
        double nat = _pillBar.Bounds.Width;
        // Stale reading from before the last mode flip: wait for real layout rather than record a lie.
        if (!double.IsNaN(_pillActedNat) && Math.Abs(nat - _pillActedNat) < 0.5) return;
        _pillActedNat = double.NaN;
        if (Environment.GetEnvironmentVariable("GROG_PILL_TRACE") == "1")
            Console.WriteLine($"PILL cur={cur} nat={nat:F0} avail={avail:F0} cache=[{_pillNat[0]:F0},{_pillNat[1]:F0},{_pillNat[2]:F0}] at={_pillNatAt:F0}");
        if (nat > 1) _pillNat[cur] = nat;   // record this mode's natural width, at THIS viewport

        const double eps = 0.5;
        if (nat > avail + eps)
        {
            // Overflowing: step one mode narrower (Full -> NameOnly -> IconOnly).
            if (cur < 2) { _pillActedNat = nat; vm.Library.PillMode = (LibraryViewModel.PillDisplayMode)(cur + 1); }
        }
        else if (cur > 0)
        {
            // Fits: try one mode wider, unless that wider mode is already known not to fit at this width.
            double widerW = _pillNat[cur - 1];
            if (double.IsNaN(widerW) || widerW <= avail + eps)
            { _pillActedNat = nat; vm.Library.PillMode = (LibraryViewModel.PillDisplayMode)(cur - 1); }
        }
    }
}

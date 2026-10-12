// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using Avalonia.Threading;

namespace Grog.App.Services;

public sealed class UiCoalescer
{
    private readonly TimeSpan _interval;
    private readonly Action _run;
    private readonly bool _trailingOnly;
    private DateTime _lastRun = DateTime.MinValue;
    private bool _scheduled;

    /// <param name="trailingOnly">Never run on the leading edge: the first request of a burst waits for the
    /// window like the rest. For work whose single cost is the stall the window exists to spread out.</param>
    public UiCoalescer(TimeSpan interval, Action run, bool trailingOnly = false)
    {
        _interval = interval;
        _run = run;
        _trailingOnly = trailingOnly;
    }

    public void Request()
    {
        if (_scheduled) return;
        var since = DateTime.UtcNow - _lastRun;
        if (!_trailingOnly && since >= _interval) { _lastRun = DateTime.UtcNow; _run(); return; }
        _scheduled = true;
        var wait = _trailingOnly ? _interval : _interval - since;
        DispatcherTimer.RunOnce(() =>
        {
            _scheduled = false;
            _lastRun = DateTime.UtcNow;
            _run();
        }, wait);
    }
}

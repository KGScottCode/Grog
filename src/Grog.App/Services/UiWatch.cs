// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Threading;

namespace Grog.App.Services;

public static class UiWatch
{
    public const int SlowMs = 50;
    public const int StallMs = 700;

    private static readonly List<string> _slow = new();
    private static readonly Stopwatch _beat = new();
    private static DispatcherTimer? _timer;
    private static Action<string>? _log;

    public static void Start(Action<string> log)
    {
        _log = log;
        _beat.Restart();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Send, (_, _) =>
        {
            long gap = _beat.ElapsedMilliseconds;
            _beat.Restart();
            if (gap < StallMs) { _slow.Clear(); return; }
            var seen = _slow.Count == 0 ? "no timed pass - the block is outside the wrapped code" : string.Join(", ", _slow);
            _slow.Clear();
            _log?.Invoke($"UI stalled {gap} ms; slow passes since last beat: {seen}");
        });
        _timer.Start();
    }

    public static Action<string, double>? OnTimed;

    public static void Note(string name, long ms)
    {
        if (ms >= SlowMs && Dispatcher.UIThread.CheckAccess()) _slow.Add($"{name} {ms} ms");
    }

    public static void Time(string name, Action body)
    {
        var sw = Stopwatch.StartNew();
        try { body(); }
        finally
        {
            sw.Stop();
            OnTimed?.Invoke(name, sw.Elapsed.TotalMilliseconds);
            if (sw.ElapsedMilliseconds >= SlowMs) _slow.Add($"{name} {sw.ElapsedMilliseconds} ms");
        }
    }
}

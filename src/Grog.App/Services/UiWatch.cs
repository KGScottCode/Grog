// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Threading;

namespace Grog.App.Services;

/// <summary>
/// Measures the UI thread instead of guessing about it (owner, 09-04: "the graph freezes every 3-5 s").
/// <see cref="Time"/> wraps the named UI passes and records any that run longer than <see cref="SlowMs"/>;
/// a 200 ms heartbeat notices when the dispatcher itself was blocked and logs the stall with the slow
/// passes seen since the previous beat - or "no timed pass", which means the block came from somewhere
/// not yet wrapped. Diagnostic-only: it decides nothing.
/// </summary>
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

    /// <summary>Every timed pass, slow or not: the headless stress harness records the distribution per name.</summary>
    public static Action<string, double>? OnTimed;

    /// <summary>Record a named cost measured elsewhere (the manifest gate wait), when it happened on the UI thread.</summary>
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

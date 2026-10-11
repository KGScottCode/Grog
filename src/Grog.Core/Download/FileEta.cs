// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Download;

using System;

/// <summary>
/// Time left for ONE file in flight. The engine reports a half-second speed for the graph; dividing what is left
/// by that figure swings by minutes on a big file every time the link dips or bursts. Here the rate is a
/// time-weighted moving average (about <see cref="RateTauSeconds"/> of memory), nothing is said until the file has
/// moved for <see cref="SettleSeconds"/>, and the FINISH TIME is smoothed so the readout counts down instead of
/// jumping. Same approach as <see cref="ThroughputMeter"/> for the whole run.
/// <para>Pure and clock-free: the caller supplies timestamps, so tests drive time directly.</para>
/// </summary>
public sealed class FileEta
{
    public const double RateTauSeconds = 20.0;
    public const double SettleSeconds = 3.0;
    private const double FinishAlpha = 0.2;

    private DateTime? _start;
    private DateTime _last;
    private long _lastBytes;
    private double _rate;            // smoothed bytes/sec
    private DateTime? _finish;       // smoothed finish time

    /// <summary>Feed one progress sample; returns the smoothed seconds left, or null while still settling.</summary>
    public double? Update(DateTime now, long bytesReceived, long? bytesTotal)
    {
        if (_start is null || bytesReceived < _lastBytes)
        {
            _start = now; _last = now; _lastBytes = bytesReceived; _rate = 0; _finish = null;
            return null;
        }
        double dt = (now - _last).TotalSeconds;
        if (dt <= 0) return Current(now);
        double inst = (bytesReceived - _lastBytes) / dt;
        double w = 1 - Math.Exp(-dt / RateTauSeconds);
        _rate = _rate <= 0 ? inst : _rate + w * (inst - _rate);
        _last = now; _lastBytes = bytesReceived;

        if ((now - _start.Value).TotalSeconds < SettleSeconds || _rate <= 0 || bytesTotal is not { } total) return null;
        long left = Math.Max(0, total - bytesReceived);
        var raw = now + TimeSpan.FromSeconds(left / _rate);
        _finish = _finish is { } prev ? prev + TimeSpan.FromTicks((long)((raw - prev).Ticks * FinishAlpha)) : raw;
        return Current(now);
    }

    private double? Current(DateTime now) => _finish is { } f ? Math.Max(0, (f - now).TotalSeconds) : null;
}

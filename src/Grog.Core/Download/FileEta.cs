// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Download;

using System;

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

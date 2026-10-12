// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Concurrent;

namespace Grog.Core.Download;

public sealed class DeviceWriteMonitor
{
    public const double SlowSeconds = 2.0;
    public const double FastSeconds = 1.0;
    public const double ImmediateSlowSeconds = 5.0;

    private sealed class State { public double Ema; public bool Slow; public int Samples; }
    private readonly ConcurrentDictionary<string, State> _roots = new(StringComparer.Ordinal);

    public void Record(string? rootKey, double flushSeconds)
    {
        var s = _roots.GetOrAdd(rootKey ?? "", _ => new State());
        lock (s)
        {
            s.Samples++;
            s.Ema = s.Samples == 1 ? flushSeconds : s.Ema * 0.6 + flushSeconds * 0.4;   // recent flushes weigh most
            if (!s.Slow && (s.Ema >= SlowSeconds || flushSeconds >= ImmediateSlowSeconds)) s.Slow = true;
            else if (s.Slow && s.Ema <= FastSeconds) s.Slow = false;
        }
    }

    public bool IsSlow(string? rootKey) => _roots.TryGetValue(rootKey ?? "", out var s) && s.Slow;

    public double? FlushSeconds(string? rootKey) => _roots.TryGetValue(rootKey ?? "", out var s) && s.Samples > 0 ? s.Ema : null;
}

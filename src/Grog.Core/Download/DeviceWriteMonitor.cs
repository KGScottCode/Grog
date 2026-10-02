// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Concurrent;

namespace Grog.Core.Download;

/// <summary>
/// How long each device takes to accept a finished file (the flush after the last byte). A USB stick with
/// write-through can take seconds per file and stalls anything else touching it; a USB3 hard drive does not.
/// The DRIVE TYPE cannot tell them apart, the flush time can (owner, 09-04). Per root, with hysteresis: slow
/// above <see cref="SlowSeconds"/>, fast again only once back under <see cref="FastSeconds"/>, so a single
/// bad flush does not flap the worker pool. Pure bookkeeping; the engine reads <see cref="IsSlow"/>.
/// </summary>
public sealed class DeviceWriteMonitor
{
    public const double SlowSeconds = 2.0;
    public const double FastSeconds = 1.0;
    /// <summary>One flush this long is verdict enough: do not wait for the average to catch up.</summary>
    public const double ImmediateSlowSeconds = 5.0;

    private sealed class State { public double Ema; public bool Slow; public int Samples; }
    private readonly ConcurrentDictionary<string, State> _roots = new(StringComparer.Ordinal);

    /// <summary>Record one file's device time (flush + hash read-back). <paramref name="rootKey"/> is the target root id ("" = primary).</summary>
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

    /// <summary>True while this device should take one file at a time.</summary>
    public bool IsSlow(string? rootKey) => _roots.TryGetValue(rootKey ?? "", out var s) && s.Slow;

    /// <summary>The smoothed flush time, for readouts and logs; null before the first sample.</summary>
    public double? FlushSeconds(string? rootKey) => _roots.TryGetValue(rootKey ?? "", out var s) && s.Samples > 0 ? s.Ema : null;
}

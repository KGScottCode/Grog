// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;

namespace Grog.Core.Download;

/// <summary>
/// "Auto" simultaneous downloads (owner call 09-01). A file costs roughly a fixed handshake (the resolve
/// + GET round-trips, ~1-2s) plus bytes/bandwidth, so extra workers only pay while the handshake share
/// is large; once big files saturate the pipe they just split it and invite 429s. The pick therefore
/// reads WHAT IS ABOUT TO RUN -- the median size of the queue's head -- rather than probing throughput
/// (which oscillates on a CDN whose per-file speed varies more than concurrency effects do). Pure; the
/// host re-evaluates it at run start and after every completion, and the engine takes it live.
/// </summary>
public static class ConcurrencyAdvisor
{
    public const int HeadWindow = 10;          // files inspected at the head of the queue
    public const int Max = 5;                  // the manual knob's ceiling, shared
    public const long SmallBytes = 10L << 20;  // under 10 MB: handshake-dominated
    public const long LargeBytes = 500L << 20; // over 500 MB: bandwidth-dominated

    /// <summary>Workers for the files at the head of the queue. <paramref name="headSizes"/> are the
    /// next files' known sizes (0/unknown counts as small -- GOG's unsized entries are tiny extras);
    /// <paramref name="remainingFiles"/> caps the answer so idle workers are never spun up.</summary>
    public static int Pick(IReadOnlyList<long> headSizes, int remainingFiles)
    {
        if (remainingFiles <= 1) return 1;
        int pick;
        if (headSizes.Count == 0) pick = 2;
        else
        {
            var sorted = headSizes.OrderBy(x => x).ToList();
            long median = sorted[sorted.Count / 2];
            pick = median < SmallBytes ? 4 : median < LargeBytes ? 3 : 2;
        }
        return Math.Clamp(Math.Min(pick, remainingFiles), 1, Max);
    }
}

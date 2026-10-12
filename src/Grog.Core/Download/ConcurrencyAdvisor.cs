// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;

namespace Grog.Core.Download;

public static class ConcurrencyAdvisor
{
    public const int HeadWindow = 10;          
    public const int Max = 5;                  
    public const long SmallBytes = 10L << 20;  // under 10 MB: handshake-dominated
    public const long LargeBytes = 500L << 20; // over 500 MB: bandwidth-dominated

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

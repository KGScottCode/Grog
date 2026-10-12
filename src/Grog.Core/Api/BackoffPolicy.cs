// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;

namespace Grog.Core.Api;

public static class BackoffPolicy
{
    /// <param name="attempt">1 for the first retry.</param>
    public static TimeSpan Delay(int attempt, TimeSpan baseDelay, TimeSpan max, TimeSpan? serverHint = null)
    {
        var exp = Math.Clamp(attempt - 1, 0, 8); 
        var delay = serverHint ?? TimeSpan.FromTicks(Math.Min(max.Ticks, baseDelay.Ticks << exp));
        if (delay < baseDelay) delay = baseDelay;
        if (delay > max) delay = max;
        return delay;
    }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;

namespace Grog.Core.Api;

/// <summary>
/// THE retry-delay arithmetic: a server hint (Retry-After) when there is one, else exponential from a base,
/// always clamped to [base, max] so a zero hint cannot hot-loop and a huge one cannot park a worker for an
/// hour. The API client's shared throttle gate and the download engine's per-task retry both use it; before
/// 09-02 each had its own curve.
/// </summary>
public static class BackoffPolicy
{
    /// <param name="attempt">1 for the first retry.</param>
    public static TimeSpan Delay(int attempt, TimeSpan baseDelay, TimeSpan max, TimeSpan? serverHint = null)
    {
        var exp = Math.Clamp(attempt - 1, 0, 8);   // 2^8 keeps the shift finite; the clamp below does the rest
        var delay = serverHint ?? TimeSpan.FromTicks(Math.Min(max.Ticks, baseDelay.Ticks << exp));
        if (delay < baseDelay) delay = baseDelay;
        if (delay > max) delay = max;
        return delay;
    }
}

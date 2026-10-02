// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Download;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

/// <summary>The bandwidth limiter's contract: 0 skips throttling entirely (disabled = uncapped, never a
/// stale cap), the rate is read live so a settings change reaches an in-flight transfer, and a rate
/// change resets the accounting window so old-rate arrears cannot stall the new rate.</summary>
public static class RateLimiterTests
{
    /// <summary>A limiter whose sleeps are recorded instead of slept: the assertions read the ledger.</summary>
    private static (RateLimiter Limiter, List<TimeSpan> Sleeps) Recorded(Func<long> rate)
    {
        var sleeps = new List<TimeSpan>();
        var limiter = new RateLimiter(rate, (d, _) => { sleeps.Add(d); return Task.CompletedTask; });
        return (limiter, sleeps);
    }

    [Test]
    public static async Task ZeroRateNeverDelays()
    {
        var (limiter, sleeps) = Recorded(() => 0);
        // Enormous byte counts must pass straight through when the limit is off.
        for (int i = 0; i < 5; i++)
            await limiter.ThrottleAsync(int.MaxValue, CancellationToken.None);
        Assert.Empty(sleeps, "uncapped path must not sleep");
    }

    [Test]
    public static async Task CapActuallyDelays()
    {
        // 100 KB at 250 KB/s should take roughly 0.4s; assert a generous lower bound only (the real clock).
        var limiter = new RateLimiter(250_000);
        var sw = Stopwatch.StartNew();
        await limiter.ThrottleAsync(100_000, CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds >= 200, $"cap must slow the transfer (took {sw.ElapsedMilliseconds}ms)");
    }

    [Test]
    public static async Task DisablingMidTransferUncapsImmediately()
    {
        // Accrue arrears at a slow rate, then disable: the next chunk must pass with no delay.
        long rate = 50_000;
        var (limiter, sleeps) = Recorded(() => Interlocked.Read(ref rate));
        await limiter.ThrottleAsync(10_000, CancellationToken.None);   // some arrears at 50 KB/s
        Assert.Equal(1, sleeps.Count, "the capped chunk asked for a sleep");
        Interlocked.Exchange(ref rate, 0);                             // user unchecks the box
        await limiter.ThrottleAsync(int.MaxValue, CancellationToken.None);
        Assert.Equal(1, sleeps.Count, "disabling must uncap the in-flight file at once");
    }

    [Test]
    public static async Task RateChangeResetsTheWindow()
    {
        // Arrears accrued at a tiny rate must not carry into a raised rate: after the raise, a small
        // chunk against a huge budget must not inherit the old debt.
        long rate = 1_000;                                             // 1 KB/s: 5 KB = 5s owed
        var (limiter, sleeps) = Recorded(() => Interlocked.Read(ref rate));
        await limiter.ThrottleAsync(5_000, CancellationToken.None);    // owes ~5 s at the old rate
        Assert.True(sleeps.Count == 1 && sleeps[0] > TimeSpan.FromSeconds(4), "the old rate owed seconds");
        Interlocked.Exchange(ref rate, 100_000_000);                   // user raises the cap
        await limiter.ThrottleAsync(5_000, CancellationToken.None);
        // 5 KB against 100 MB/s owes 50 us at most: anything longer is the old debt leaking through.
        Assert.True(sleeps.Skip(1).All(d => d < TimeSpan.FromMilliseconds(10)), "old-rate arrears must not stall the new rate");
    }
}

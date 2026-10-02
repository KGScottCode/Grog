// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using Grog.Core.Download;
using Grog.Core.Tests.Framework;

[NewBatch]
[Trait("throughput")]
public class ThroughputMeterTests
{
    private static DateTime T0 => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime At(double s) => T0.AddSeconds(s);

    [Test] void BaselineExcludesPreRunBytes()
    {
        var m = new ThroughputMeter(settleSeconds: 1);
        // Start with 1 GB already backed up; only NEW bytes should count toward the run.
        m.Sample(At(0), 1_000_000_000, active: true);
        m.Sample(At(2), 1_000_000_000 + 20_000_000, active: true);  // +20 MB over 2s
        Assert.True(Math.Abs(m.AvgBytesPerSec - 10_000_000) < 1_000, "avg = 10 MB/s over active time, baseline removed");
    }

    [Test] void AvgIsCumulativeOverActiveTime()
    {
        var m = new ThroughputMeter(settleSeconds: 1);
        m.Sample(At(0), 0, true);
        m.Sample(At(10), 100_000_000, true);   // 100 MB in 10 active seconds
        Assert.True(Math.Abs(m.AvgBytesPerSec - 10_000_000) < 1_000, "10 MB/s sustained");
        Assert.True(Math.Abs(m.ActiveElapsed.TotalSeconds - 10) < 0.001, "elapsed = 10s active");
    }

    [Test] void PauseDoesNotDragAvgOrElapsed()
    {
        var m = new ThroughputMeter(settleSeconds: 1);
        m.Sample(At(0), 0, true);
        m.Sample(At(10), 100_000_000, true);            // 100 MB / 10s = 10 MB/s
        // Paused for a long time; no bytes move and time should NOT accrue.
        m.Sample(At(3610), 100_000_000, active: false); // one hour paused
        Assert.True(Math.Abs(m.ActiveElapsed.TotalSeconds - 10) < 0.001, "paused hour not counted in elapsed");
        Assert.True(Math.Abs(m.AvgBytesPerSec - 10_000_000) < 1_000, "avg unchanged by the pause");
        Assert.Equal(0.0, m.CurrentBytesPerSec, "current speed reads 0 while idle");
    }

    [Test] void CurrentTracksRecentWindowNotWholeRun()
    {
        var m = new ThroughputMeter(windowSeconds: 5, settleSeconds: 1);
        m.Sample(At(0), 0, true);
        // 10s slow (1 MB/s), then a burst.
        for (int s = 1; s <= 10; s++) m.Sample(At(s), s * 1_000_000L, true);
        // Now push 50 MB in the next 5s (10 MB/s) -- current should climb toward the recent rate,
        // while avg (whole run) stays lower.
        long b = 10_000_000;
        for (int s = 11; s <= 15; s++) { b += 10_000_000; m.Sample(At(s), b, true); }
        Assert.True(m.CurrentBytesPerSec > m.AvgBytesPerSec, "current (recent) outruns cumulative avg during a burst");
        Assert.True(m.CurrentBytesPerSec > 8_000_000, "current reflects the ~10 MB/s recent window");
    }

    [Test] void RateWithheldUntilSettled()
    {
        var m = new ThroughputMeter(settleSeconds: 3);
        m.Sample(At(0), 0, true);
        m.Sample(At(1), 50_000_000, true);       // only 1s of active time
        Assert.Equal(0.0, m.AvgBytesPerSec, "avg withheld before settle window");
        Assert.True(!m.HasStableRate, "no stable rate yet");
        Assert.Equal((double?)null, m.EtaSeconds(1_000_000_000), "no ETA before settle");
        m.Sample(At(4), 200_000_000, true);      // now 4s active
        Assert.True(m.HasStableRate, "stable after settle window");
        Assert.True(m.EtaSeconds(1_000_000_000) > 0, "ETA available once settled");
    }

    [Test] void EtaDerivesFromAvg()
    {
        var m = new ThroughputMeter(settleSeconds: 1);
        m.Sample(At(0), 0, true);
        m.Sample(At(10), 100_000_000, true);     // 10 MB/s sustained
        var eta = m.EtaSeconds(50_000_000);      // 50 MB left -> 5s
        Assert.True(eta is > 4.9 and < 5.1, "eta = bytes-left / avg");
    }

    [Test] void ResetClearsRun()
    {
        var m = new ThroughputMeter(settleSeconds: 1);
        m.Sample(At(0), 0, true);
        m.Sample(At(10), 100_000_000, true);
        m.Reset();
        Assert.Equal(0.0, m.AvgBytesPerSec, "avg cleared");
        Assert.Equal(0.0, m.ActiveElapsed.TotalSeconds, "elapsed cleared");
        // New run re-baselines from the next sample.
        m.Sample(At(20), 500_000_000, true);
        m.Sample(At(30), 500_000_000 + 100_000_000, true);
        Assert.True(Math.Abs(m.AvgBytesPerSec - 10_000_000) < 1_000, "new run measured fresh");
    }

    [Test] void OutOfOrderSamplesIgnored()
    {
        var m = new ThroughputMeter(settleSeconds: 1);
        m.Sample(At(0), 0, true);
        m.Sample(At(10), 100_000_000, true);
        var avg = m.AvgBytesPerSec;
        m.Sample(At(5), 999_999_999, true);      // stale timestamp
        Assert.True(Math.Abs(m.AvgBytesPerSec - avg) < 1, "stale sample changes nothing");
    }

    // ---- the two-term ETA model (09-02): per-file overhead + per-byte cost, smoothed finish time ----

    /// <summary>Drive one file through the meter: start at t, bytes arrive linearly, done at t+secs.</summary>
    private static void RunFile(ThroughputMeter m, ref double t, ref long cum, long bytes, double secs)
    {
        var id = Guid.NewGuid();
        m.Sample(At(t), cum, true); m.FileStarted(id);
        t += secs; cum += bytes;
        m.Sample(At(t), cum, true); m.FileCompleted(id, bytes);
    }

    [Test] void SmallFilesCostTimeThatIsNotBytes_AndTheModelSeesIt()
    {
        var m = new ThroughputMeter();
        double t = 0; long cum = 0;
        m.Sample(At(t), cum, true);
        // Every file costs 1 s of overhead plus 1 s per MB: 1 MB -> 2 s, 10 MB -> 11 s, 4 MB -> 5 s.
        RunFile(m, ref t, ref cum, 1_000_000, 2);
        RunFile(m, ref t, ref cum, 10_000_000, 11);
        RunFile(m, ref t, ref cum, 4_000_000, 5);
        var model = m.CostModel;
        Assert.NotNull(model, "three files fit the model");
        Assert.True(Math.Abs(model!.Value.PerFile - 1.0) < 0.01, $"per-file overhead ~1 s, got {model.Value.PerFile}");
        Assert.True(Math.Abs(model.Value.PerByte * 1_000_000 - 1.0) < 0.01, $"per-MB ~1 s, got {model.Value.PerByte * 1e6}");
        // 20 small files of 100 KB: a byte-rate ETA says ~2.4 s (2 MB at 15 MB/18 s); the model knows each
        // costs a second of overhead first.
        var eta = m.EtaSeconds(2_000_000, 20)!.Value;
        Assert.True(eta > 20 && eta < 23, $"20 files x (1 s + 0.1 s) ~= 22 s, got {eta:F1}");
    }

    [Test] void ParallelismDividesTheSummedPerFileCost()
    {
        var m = new ThroughputMeter();
        double t = 0; long cum = 0;
        m.Sample(At(t), cum, true);
        // Two workers: pairs of files overlap completely, each 4 s wall, 1 MB. Active time is half the summed durations.
        for (int i = 0; i < 3; i++)
        {
            var a = Guid.NewGuid(); var b = Guid.NewGuid();
            m.FileStarted(a); m.FileStarted(b);
            t += 4; cum += 2_000_000;
            m.Sample(At(t), cum, true);
            m.FileCompleted(a, 1_000_000); m.FileCompleted(b, 1_000_000);
        }
        Assert.True(Math.Abs(m.Parallelism - 2.0) < 0.05, $"two in flight, got {m.Parallelism:F2}");
        // 10 more such files: 40 s of per-file cost over 2 workers = 20 s.
        var eta = m.EtaSeconds(10_000_000, 10)!.Value;
        Assert.True(Math.Abs(eta - 20) < 1.5, $"~20 s, got {eta:F1}");
    }

    [Test] void TheFinishTimeDriftsInsteadOfJumping()
    {
        var m = new ThroughputMeter();
        double t = 0; long cum = 0;
        m.Sample(At(t), cum, true);
        RunFile(m, ref t, ref cum, 1_000_000, 2); RunFile(m, ref t, ref cum, 1_000_000, 2); RunFile(m, ref t, ref cum, 1_000_000, 2);
        var first = m.EtaSeconds(10_000_000, 10)!.Value;   // ~20 s
        // Same tick, asked again (three readouts share one sample): identical, no extra smoothing step.
        Assert.True(Math.Abs(m.EtaSeconds(10_000_000, 10)!.Value - first) < 0.001, "one smoothing step per sample");
        // Next second the remaining work doubles (a learned size): the estimate moves a fifth of the way, not all of it.
        t += 1; m.Sample(At(t), cum, true);
        var next = m.EtaSeconds(20_000_000, 20)!.Value;
        Assert.True(next > first - 1 && next < first + 6, $"drifted toward ~40 s without jumping: {first:F1} -> {next:F1}");
    }

    [Test] void Big_files_behind_small_ones_are_priced_at_the_link_rate()
    {
        // Walk 09-25: smallest first, three 1 MB files at 1 MB/s landed, then 540 GB remains in 300 files. The per-file
        // fit has no byte slope; the estimate must still charge the bytes beyond the seen size at the link's rate.
        var m = new ThroughputMeter();
        double t = 0; long cum = 0;
        m.Sample(At(t), cum, true);
        for (int i = 0; i < 80; i++) RunFile(m, ref t, ref cum, 1_000_000, 1);   // 80 MB at 1 MB/s: past the minimum
        var eta = m.EtaSeconds(540_000_000_000, 300);
        Assert.NotNull(eta, "enough has moved to estimate");
        Assert.True(eta!.Value > 400_000, $"~540,000 s at 1 MB/s, not minutes: got {eta.Value:F0}");
    }

    [Test] void Nothing_is_promised_before_the_run_has_moved_enough()
    {
        var m = new ThroughputMeter();
        double t = 0; long cum = 0;
        m.Sample(At(t), cum, true);
        for (int i = 0; i < 3; i++) RunFile(m, ref t, ref cum, 1_000_000, 1);
        Assert.Equal((double?)null, m.EtaSeconds(540_000_000_000, 300), "3 MB seen says nothing about 540 GB: Estimating");
    }

    [Test] void TheByteRateIsCappedByWhatTheLinkActuallyDelivered()
    {
        // Two workers, each file's own transfer ran at 10 MB/s, but the two shared a 10 MB/s link: 20 MB moved
        // in 2 s per pair, not 1 s. Small files first, then a first big one, so the fit has a byte slope.
        var m = new ThroughputMeter();
        double t = 0; long cum = 0;
        m.Sample(At(t), cum, true);
        void Pair(long bytesEach, double secs)
        {
            var a = Guid.NewGuid(); var b = Guid.NewGuid();
            m.FileStarted(a); m.FileStarted(b);
            t += secs; cum += 2 * bytesEach;
            m.Sample(At(t), cum, true);
            m.FileCompleted(a, bytesEach); m.FileCompleted(b, bytesEach);
        }
        Pair(1_000_000, 1.2); Pair(1_000_000, 1.2); Pair(50_000_000, 11);   // overhead ~1 s + bytes at the shared 10 MB/s
        var link = m.TransferBytesPerSec!.Value;
        Assert.True(link > 7_000_000 && link < 9_000_000, $"aggregate while moving ~7.8 MB/s, got {link / 1e6:F1}");
        // 1 GB left in a few files: at the link rate that is ~128 s; the uncapped model (per-file slope / 2
        // workers) would have promised roughly half that.
        var eta = m.EtaSeconds(1_000_000_000, 4)!.Value;
        Assert.True(eta > 110 && eta < 150, $"capped by the link: ~128 s, got {eta:F0}");
    }
}

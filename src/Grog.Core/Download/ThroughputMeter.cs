// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Download;

using System;
using System.Collections.Generic;

/// <summary>
/// Derives a run's throughput from monotonic (timestamp, cumulative-bytes, active) samples fed per tick.
/// Current = short rolling window; Avg = session bytes over ACTIVE time only (pauses never drag it down).
///
/// <para>THE ETA is a two-term model, not bytes over speed. A run of small files spends most of its time
/// on per-file work (resolve round-trips, the checksum fetch, the rename) during which no bytes move, so a
/// byte rate collapses and an ETA built on it swings by minutes as each file lands (owner 09-02). Each
/// completed file contributes (bytes, wall seconds); a least-squares fit gives seconds = a + b*bytes, and
/// what remains costs a*files + b*bytes, divided by the run's measured parallelism. Until three files have
/// finished the byte rate stands in. The FINISH TIME is then smoothed exponentially, so the clock the user
/// reads drifts rather than jumps.</para>
///
/// <para>Pure and clock-free: the caller supplies timestamps, so tests drive time directly.</para>
/// </summary>
public sealed class ThroughputMeter
{
    private readonly double _windowSeconds;
    private readonly double _settleSeconds;
    private readonly Queue<(double T, long Bytes)> _window = new();

    private bool _started;
    private DateTime _origin;
    private double _now;          // seconds since first sample
    private long _baseline;       // cumulative bytes at run start (pre-run backed-up bytes excluded)
    private long _lastRel;        // last relative (session) byte count
    private double _activeSeconds;
    private long _sessionBytes;   // bytes moved while active since run start
    private double _transferSeconds;   // ticks during which a file was mid-transfer (gaps between files excluded)
    private long _transferBytes;

    /// <param name="windowSeconds">Rolling window for the live "current" speed.</param>
    /// <param name="settleSeconds">Active time that must elapse before Avg/ETA report, so early
    /// numbers don't swing wildly.</param>
    public ThroughputMeter(double windowSeconds = 5.0, double settleSeconds = 3.0)
    {
        _windowSeconds = windowSeconds;
        _settleSeconds = settleSeconds;
    }

    // Per-file cost model (see the class summary).
    private readonly Dictionary<Guid, double> _fileStart = new();
    private readonly List<(double Bytes, double Seconds)> _files = new();
    private double _fileSecondsSum;      // sum of per-file wall durations: with N workers this exceeds active time
    private double? _smoothFinish;       // seconds-since-origin of the smoothed finish estimate
    private double _smoothAt = -1;       // the sample time the last smoothing step ran at
    private const int MinFilesForModel = 3;
    /// <summary>Bytes the run must have moved before an estimate that is mostly bytes may speak (09-25).</summary>
    private const long MinBytesForEstimate = 64L * 1024 * 1024;
    private const double SmoothAlpha = 0.2;

    public double CurrentBytesPerSec { get; private set; }
    /// <summary>Bytes moved during active download time since the run's baseline (this run's "data done").</summary>
    public long SessionBytes => _sessionBytes;
    public double AvgBytesPerSec => _activeSeconds >= _settleSeconds && _sessionBytes > 0
        ? _sessionBytes / _activeSeconds : 0;
    public TimeSpan ActiveElapsed => TimeSpan.FromSeconds(_activeSeconds);
    public bool HasStableRate => AvgBytesPerSec > 0;

    /// <summary>Seconds to move <paramref name="bytesRemaining"/> at the sustained average, or null
    /// until the rate has settled. The raw byte-rate estimate; <see cref="EtaSeconds(long, int)"/> is the model.</summary>
    public double? EtaSeconds(long bytesRemaining)
    {
        var avg = AvgBytesPerSec;
        if (avg <= 0 || bytesRemaining <= 0) return null;
        return bytesRemaining / avg;
    }

    /// <summary>A file began transferring. Its wall duration starts now (the meter's own clock).</summary>
    public void FileStarted(Guid id) => _fileStart[id] = _now;

    /// <summary>A file that was still pending at the previous sample and is already finished at this one:
    /// it started and ended inside one tick (progress events are coalesced). Its duration is at most the tick,
    /// and those are exactly the small files the model exists for, so they must not be dropped.</summary>
    public void FileStartedSincePreviousSample(Guid id) => _fileStart[id] = _prevNow;
    private double _prevNow;

    /// <summary>A file finished: one (bytes, seconds) point for the cost model. Files that started before
    /// the meter was reset (a resume) have no start and are not counted.</summary>
    public void FileCompleted(Guid id, long bytes)
    {
        if (!_fileStart.Remove(id, out var t0)) return;
        var secs = Math.Max(0.05, _now - t0);
        _files.Add((bytes, secs));
        _fileSecondsSum += secs;
    }

    /// <summary>Files finished this run that the model has a duration for.</summary>
    public int ModelledFiles => _files.Count;

    /// <summary>The fitted per-file overhead (seconds) and per-byte cost (seconds/byte), or null before
    /// <see cref="MinFilesForModel"/> files have finished. Least squares, clamped to non-negative.</summary>
    public (double PerFile, double PerByte)? CostModel
    {
        get
        {
            int n = _files.Count;
            if (n < MinFilesForModel) return null;
            double mb = 0, ms = 0;
            foreach (var (b, sec) in _files) { mb += b; ms += sec; }
            mb /= n; ms /= n;
            double cov = 0, var = 0;
            foreach (var (b, sec) in _files) { cov += (b - mb) * (sec - ms); var += (b - mb) * (b - mb); }
            double perByte = var > 0 ? Math.Max(0, cov / var) : 0;
            double perFile = Math.Max(0, ms - perByte * mb);
            if (perByte == 0 && perFile == 0) return null;
            return (perFile, perByte);
        }
    }

    /// <summary>Wall seconds per file overlap under concurrency: this is how many files, on average, were in
    /// flight at once while the run was active. Divides the summed per-file cost of what remains.</summary>
    public double Parallelism => _activeSeconds > 0 && _fileSecondsSum > 0 ? Math.Max(1, _fileSecondsSum / _activeSeconds) : 1;

    /// <summary>Aggregate bytes per second over the seconds in which a transfer was actually moving: the
    /// link as this run has seen it, gaps between files excluded. Null until it has settled.</summary>
    public double? TransferBytesPerSec => _transferSeconds >= _settleSeconds && _transferBytes > 0
        ? _transferBytes / _transferSeconds : null;

    /// <summary>The byte term's rate. The per-file fit measures ONE file's transfer and the parallelism
    /// division assumes N files each get that much, which a shared link does not allow (owner, 09-04: three
    /// big installers will not each run at a small file's burst). So the modelled rate is capped by the
    /// aggregate the link has actually delivered while moving. Early in a run of small files that observed
    /// rate is dragged down by ramp-up, so the estimate starts conservative and comes IN as big files sustain,
    /// never out.</summary>
    private double ModelBytesPerSec(double perByte)
    {
        // A fit that put every second on the per-file term (equal-sized files) has no byte rate to cap:
        // capping there would charge the bytes twice.
        if (perByte <= 0) return double.PositiveInfinity;
        double modelled = Parallelism / perByte;
        return TransferBytesPerSec is { } link ? Math.Min(modelled, link) : modelled;
    }

    /// <summary>The modelled, smoothed ETA for <paramref name="filesRemaining"/> files totalling
    /// <paramref name="bytesRemaining"/>; null until either the model or the byte rate can speak.</summary>
    public double? EtaSeconds(long bytesRemaining, int filesRemaining)
    {
        if (filesRemaining <= 0 && bytesRemaining <= 0) { _smoothFinish = null; return null; }
        // (09-25, owner walk) Not enough seen yet to speak for what is left: a smallest-first run had moved a few MB of
        // tiny files and promised 2-3 minutes for 540 GB. Until the run has moved a meaningful share, say nothing
        // ("Estimating..."): an honest blank beats a confident number that is off by a factor of 200.
        if (bytesRemaining > 0 && _transferBytes < MinBytesForEstimate && bytesRemaining > 16 * Math.Max(1, _transferBytes))
        { _smoothFinish = null; return null; }
        double? raw;
        if (CostModel is { } m)
        {
            double rate = ModelBytesPerSec(m.PerByte);
            long priced = Math.Max(0, bytesRemaining);
            if (double.IsInfinity(rate))
            {
                // A fit that put every second on the per-file term (all files so far about one size) has no byte rate:
                // the per-file cost already covers files of the size seen. It used to drop the REST of the bytes too,
                // so 540 GB behind a few MB of tiny files read "2-3 minutes" (walk 09-25). Price only the bytes beyond
                // the seen size, at what the link has actually delivered.
                double meanSeen = 0; foreach (var (b, _) in _files) meanSeen += b; meanSeen /= Math.Max(1, _files.Count);
                priced = Math.Max(0, bytesRemaining - (long)(meanSeen * Math.Max(0, filesRemaining)));
                rate = priced > 0 ? TransferBytesPerSec ?? double.PositiveInfinity : double.PositiveInfinity;
            }
            raw = m.PerFile * Math.Max(0, filesRemaining) / Parallelism
                + (double.IsInfinity(rate) ? 0 : priced / rate);
            if (double.IsInfinity(rate) && priced > 0) raw = null;   // bytes to move and no rate for them yet
        }
        else raw = EtaSeconds(bytesRemaining);
        if (raw is not { } eta) return null;
        // Smooth the FINISH TIME, not the remaining seconds: a steady estimate should count down one second
        // per second, which a smoothed duration would not do.
        var finish = _now + eta;
        // One smoothing step per SAMPLE, however many readouts ask within the same tick (three do).
        if (_smoothFinish is not { } prev) _smoothFinish = finish;
        else if (_smoothAt != _now) _smoothFinish = prev + SmoothAlpha * (finish - prev);
        _smoothAt = _now;
        return Math.Max(0, _smoothFinish.Value - _now);
    }

    /// <summary>Begin a fresh run. All accumulators clear; the next sample re-seeds the baseline.</summary>
    /// <summary>The run went idle (pause, error): drop the live rate without touching the cumulative
    /// baseline, so the next active sample does not re-credit the in-flight partial as new bytes.</summary>
    public void MarkIdle()
    {
        _window.Clear();
        CurrentBytesPerSec = 0;
    }

    public void Reset()
    {
        _window.Clear();
        _started = false;
        _now = 0; _baseline = 0; _lastRel = 0;
        _activeSeconds = 0; _sessionBytes = 0; _transferSeconds = 0; _transferBytes = 0;
        CurrentBytesPerSec = 0;
        _fileStart.Clear(); _files.Clear(); _fileSecondsSum = 0; _smoothFinish = null; _smoothAt = -1; _prevNow = 0;
    }

    /// <param name="nowUtc">Wall clock of this sample.</param>
    /// <param name="cumulativeBytes">Monotonic total bytes done + in-flight this run. May include
    /// pre-run backed-up bytes; the first sample's value is subtracted as a baseline.</param>
    /// <param name="active">True if THE RUN is moving this tick (a file mid-flight, or one about to start).</param>
    /// <param name="transferring">True if a file is actually mid-transfer this tick; null means "same as
    /// <paramref name="active"/>". Feeds <see cref="TransferBytesPerSec"/>, the link-rate cap.</param>
    public void Sample(DateTime nowUtc, long cumulativeBytes, bool active, bool? transferring = null)
    {
        if (!_started)
        {
            _started = true;
            _origin = nowUtc;
            _baseline = cumulativeBytes;
            _lastRel = 0;
            _now = 0;
            return;
        }

        var t = (nowUtc - _origin).TotalSeconds;
        var dt = t - _now;
        if (dt <= 0) return;              // out-of-order or duplicate tick: ignore
        _prevNow = _now;
        _now = t;

        var rel = cumulativeBytes - _baseline;
        if (rel < _lastRel)               // cumulative went backwards (reset/resync): rebase, no negative delta
        {
            _baseline = cumulativeBytes;
            rel = 0;
            _lastRel = 0;
            _window.Clear();
        }
        var dBytes = rel - _lastRel;
        _lastRel = rel;

        if (active)
        {
            _activeSeconds += dt;
            _sessionBytes += dBytes;
            if (transferring ?? true) { _transferSeconds += dt; _transferBytes += dBytes; }

            _window.Enqueue((t, rel));
            while (_window.Count > 1 && t - _window.Peek().T > _windowSeconds) _window.Dequeue();
            var head = _window.Peek();
            var span = t - head.T;
            if (span > 0.25) CurrentBytesPerSec = Math.Max(0, (rel - head.Bytes) / span);
        }
        else
        {
            _window.Clear();
            CurrentBytesPerSec = 0;
        }
    }
}

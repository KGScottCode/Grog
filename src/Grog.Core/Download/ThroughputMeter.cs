// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Download;

using System;
using System.Collections.Generic;

public sealed class ThroughputMeter
{
    private readonly double _windowSeconds;
    private readonly double _settleSeconds;
    private readonly Queue<(double T, long Bytes)> _window = new();

    private bool _started;
    private DateTime _origin;
    private double _now;         
    private long _baseline;      
    private long _lastRel;       
    private double _activeSeconds;
    private long _sessionBytes;  
    private double _transferSeconds; 
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
    private double _fileSecondsSum;     
    private double? _smoothFinish;      
    private double _smoothAt = -1;      
    private const int MinFilesForModel = 3;
    private const long MinBytesForEstimate = 64L * 1024 * 1024;
    private const double SmoothAlpha = 0.2;

    public double CurrentBytesPerSec { get; private set; }
    public long SessionBytes => _sessionBytes;
    public double AvgBytesPerSec => _activeSeconds >= _settleSeconds && _sessionBytes > 0
        ? _sessionBytes / _activeSeconds : 0;
    public TimeSpan ActiveElapsed => TimeSpan.FromSeconds(_activeSeconds);
    public bool HasStableRate => AvgBytesPerSec > 0;

    public double? EtaSeconds(long bytesRemaining)
    {
        var avg = AvgBytesPerSec;
        if (avg <= 0 || bytesRemaining <= 0) return null;
        return bytesRemaining / avg;
    }

    public void FileStarted(Guid id) => _fileStart[id] = _now;

    public void FileStartedSincePreviousSample(Guid id) => _fileStart[id] = _prevNow;
    private double _prevNow;

    public void FileCompleted(Guid id, long bytes)
    {
        if (!_fileStart.Remove(id, out var t0)) return;
        var secs = Math.Max(0.05, _now - t0);
        _files.Add((bytes, secs));
        _fileSecondsSum += secs;
    }

    public int ModelledFiles => _files.Count;

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

    public double Parallelism => _activeSeconds > 0 && _fileSecondsSum > 0 ? Math.Max(1, _fileSecondsSum / _activeSeconds) : 1;

    public double? TransferBytesPerSec => _transferSeconds >= _settleSeconds && _transferBytes > 0
        ? _transferBytes / _transferSeconds : null;

    private double ModelBytesPerSec(double perByte)
    {
        if (perByte <= 0) return double.PositiveInfinity;
        double modelled = Parallelism / perByte;
        return TransferBytesPerSec is { } link ? Math.Min(modelled, link) : modelled;
    }

    public double? EtaSeconds(long bytesRemaining, int filesRemaining)
    {
        if (filesRemaining <= 0 && bytesRemaining <= 0) { _smoothFinish = null; return null; }
        if (bytesRemaining > 0 && _transferBytes < MinBytesForEstimate && bytesRemaining > 16 * Math.Max(1, _transferBytes))
        { _smoothFinish = null; return null; }
        double? raw;
        if (CostModel is { } m)
        {
            double rate = ModelBytesPerSec(m.PerByte);
            long priced = Math.Max(0, bytesRemaining);
            if (double.IsInfinity(rate))
            {
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
        var finish = _now + eta;
        if (_smoothFinish is not { } prev) _smoothFinish = finish;
        else if (_smoothAt != _now) _smoothFinish = prev + SmoothAlpha * (finish - prev);
        _smoothAt = _now;
        return Math.Max(0, _smoothFinish.Value - _now);
    }

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

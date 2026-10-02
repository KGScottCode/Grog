// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Download;
using Grog.Core.Format;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.App.Runtime;

namespace Grog.App.ViewModels;

// Overview (S3.3): the run readouts -- throughput and ETA, the speed graph, the coverage table and byte rollup,
// the extras segments, the run-stats pill and its footer, the free-after-run line and the current-file bar.
public sealed partial class OverviewViewModel
{
    public string AvgSpeedText => _meter.AvgBytesPerSec > 0 ? ByteFormat.Size((long)_meter.AvgBytesPerSec) + "/s" : "--";
    public string ElapsedText => _meter.ActiveElapsed.TotalSeconds >= 1 ? FormatDuration(_meter.ActiveElapsed) : "--";
    /// <summary>Sub-caption for ELAPSED: the clock does not run while paused, so say so once the run has been paused.</summary>
    public string ElapsedInlineText => _meter.ActiveElapsed.TotalSeconds < 1 ? "" : _root.DownloadPaused ? "paused; pauses not counted" : "downloading time";

    public string CurrentSpeedText => _meter.CurrentBytesPerSec > 0 ? ByteFormat.Size((long)_meter.CurrentBytesPerSec) + "/s" : "--";

    // ---- Live speed graph: muted area chart behind the Downloading text ----
    // Sampled at ~1 Hz (not per byte-tick): one geometry rebuild per second. Auto-scales to the window's peak.
    private readonly List<double> _speedSamples = new();
    private const int SpeedSampleCap = 60;   // a rolling 60 s minute
    private DateTime _lastSpeedSampleUtc = DateTime.MinValue;
    public Geometry? SpeedGraphGeometry { get; private set; }
    public bool ShowSpeedGraph => RunActive && _speedSamples.Count >= 2;

    // Y-axis readouts: the fill normalizes to the window PEAK (the ceiling); the average is a reference line.
    private double _speedPeak, _speedAvg;
    public string SpeedPeakText => _speedPeak > 0 ? ByteFormat.Size((long)_speedPeak) + "/s" : "";
    public string SpeedAvgTag => _speedAvg > 0 ? "avg " + ByteFormat.Size((long)_speedAvg) + "/s" : "";
    private double SpeedAvgRatio => _speedPeak > 0 ? Math.Clamp(_speedAvg / _speedPeak, 0, 1) : 0;
    // The average line splits the plot: the row ABOVE it is peak-minus-average, the row BELOW is the average.
    public Avalonia.Controls.GridLength SpeedAboveAvgRow => new(Math.Max(0.0001, 1 - SpeedAvgRatio), Avalonia.Controls.GridUnitType.Star);
    public Avalonia.Controls.GridLength SpeedBelowAvgRow => new(Math.Max(0.0001, SpeedAvgRatio), Avalonia.Controls.GridUnitType.Star);

    internal void SampleSpeedGraph(double bytesPerSec)
    {
        var now = DateTime.UtcNow;
        if (now - _lastSpeedSampleUtc < TimeSpan.FromSeconds(1)) return;
        _lastSpeedSampleUtc = now;
        _speedSamples.Add(Math.Max(0, bytesPerSec));
        while (_speedSamples.Count > SpeedSampleCap) _speedSamples.RemoveAt(0);
        _speedPeak = _speedSamples.Count > 0 ? _speedSamples.Max() : 0;
        _speedAvg = _speedSamples.Count > 0 ? _speedSamples.Average() : 0;
        SpeedGraphGeometry = BuildSpeedGeometry();
        RaiseSpeedGraph();
    }

    /// <summary>The speed-over-time card's readouts as ONE set (sample push + reset both raise it).</summary>
    private void RaiseSpeedGraph()
    {
        OnPropertyChanged(nameof(SpeedGraphGeometry));
        OnPropertyChanged(nameof(ShowSpeedGraph));
        OnPropertyChanged(nameof(SpeedPeakText));
        OnPropertyChanged(nameof(SpeedAvgTag));
        OnPropertyChanged(nameof(SpeedAboveAvgRow));
        OnPropertyChanged(nameof(SpeedBelowAvgRow));
    }

    internal void ResetSpeedGraph()
    {
        _speedSamples.Clear();
        _lastSpeedSampleUtc = DateTime.MinValue;
        _speedPeak = _speedAvg = 0;
        SpeedGraphGeometry = null;
        RaiseSpeedGraph();
    }

    /// <summary>Filled area under the speed curve in a fixed 1000x100 box. Fixed per-second step, so adding a
    /// sample never re-spaces existing ones; a Viewbox scales it, so the VM needs no pixel sizes.</summary>
    private Geometry? BuildSpeedGeometry()
    {
        int n = _speedSamples.Count;
        if (n < 2) return null;
        const double w = 1000, h = 100;
        double max = Math.Max(1, _speedSamples.Max());
        double step = w / (SpeedSampleCap - 1);   // fixed: 1 s = one slot of the rolling minute
        // Right-anchored: the newest sample sits at the right edge; the left stays empty until the minute fills.
        double xStart = w - (n - 1) * step;   // oldest sample's x
        var g = new Avalonia.Media.StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Avalonia.Point(xStart, h), true);   // bottom of the oldest sample
            for (int i = 0; i < n; i++)
            {
                double x = xStart + i * step;   // newest (i == n-1) lands exactly at w
                double y = h - Math.Clamp(_speedSamples[i] / max, 0, 1) * h;
                ctx.LineTo(new Avalonia.Point(x, y));
            }
            ctx.LineTo(new Avalonia.Point(w, h));   // straight down at the newest (right edge)
            ctx.EndFigure(true);
        }
        return g;
    }
    private static string FormatDuration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h"
      : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
      : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s"
      : $"{Math.Max(0, (int)t.TotalSeconds)}s";

    public string RemainingText => ByteFormat.Size(Math.Max(0, _stats.AllSizeBytes - _stats.AllDoneBytes));
    /// <summary>Bytes on disk, on its own -- the storage card pairs it with RemainingText.</summary>
    public string DoneSizeText => ByteFormat.Size(_stats.AllDoneBytes);

    // Hero progress bar: scope-aware totals, so deselecting Games or Extras re-totals the headline --
    // unlike the per-category cards, which keep full-library magnitudes so out-of-scope cost stays visible.
    private double LibraryDoneRatio => _stats.TotalBytes <= 0 ? 0 : (double)_stats.DoneBytes / _stats.TotalBytes;
    public Avalonia.Controls.GridLength LibraryDoneCol => new(Math.Max(0, 100.0 * LibraryDoneRatio), Avalonia.Controls.GridUnitType.Star);
    public Avalonia.Controls.GridLength LibraryRemainCol => new(Math.Max(0.0001, 100.0 * (1 - LibraryDoneRatio)), Avalonia.Controls.GridUnitType.Star);

    // LIBRARY hero card headline numbers, aggregated over what's IN SCOPE (kind + language).
    public string LibraryDoneText => ByteFormat.Size(_stats.DoneBytes);
    public string LibraryRemainText => ByteFormat.Size(Math.Max(0, _stats.TotalBytes - _stats.DoneBytes));
    private int ScopedDoneFiles => (_includeGames ? _stats.GameDoneFileCount : 0) + (_includeExtras ? _stats.ExtraDoneFileCount : 0);
    private int ScopedTotalFiles => (_includeGames ? _stats.GameFileCount : 0) + (_includeExtras ? _stats.ExtraFileCount : 0);
    public string LibraryFilesText => $"{ScopedDoneFiles} / {ScopedTotalFiles}";

    /// <summary>Bytes still to move in the CURRENT run (active + waiting rows), not the whole library.</summary>
    internal long QueuedRemainingBytes =>
        ActiveRows.Concat(WaitingRows).Sum(r => r.RemainingBytes);

    /// <summary>The rows this run can REACH: a won't-fit row is queued but has nowhere to land, so pricing it
    /// into Files left / ETA / "finishes about" promised a week-long run over files that will not download
    /// (owner-hit 09-18: 748 left, 7d 12h, with every fitting file done). Same rule as DownloadStatsText.</summary>
    /// (QA 09-30 A11) Nor a row whose transfer this run has given up on (its engine task failed or was turned away,
    /// and a run does not re-admit it) or that waits for a disconnected drive: "1 left" with an ETA over nothing moving.
    private IEnumerable<QueueRow> ReachableRows => ActiveRows.Concat(WaitingRows).Where(r => !r.WontFit && !UnreachableThisRun(r));
    private bool UnreachableThisRun(QueueRow r)
    {
        if (!r.IsLiveTask) return false;
        var t = r.Task;
        if (t.State is Grog.Core.Download.DownloadTaskState.Failed or Grog.Core.Download.DownloadTaskState.Canceled) return true;
        return t.State == Grog.Core.Download.DownloadTaskState.Pending && Grog.Core.Volumes.DriveWaits.Contains(t.TargetRootId);
    }
    internal int ReachableCount => ReachableRows.Count();
    internal long ReachableRemainingBytes =>
        ReachableRows.Sum(r => r.RemainingBytes);

    public string EtaText
    {
        get
        {
            // Only while bytes move: MarkIdle zeroes the live rate but the average survives a Pause, so the ETA
            // and "finishes about" kept counting from Now on every raise while nothing moved (QA 09-18).
            if (ReachableCount == 0 || !_root.DownloadRunning || _root.DownloadPaused) return "--";
            var secs = _meter.EtaSeconds(ReachableRemainingBytes, ReachableCount);
            return secs is null ? "Estimating…" : FormatDuration(TimeSpan.FromSeconds(secs.Value));
        }
    }

    // Run-stats pill: Files + Data groups for this run's queue; all read "--" until there's a run.
    /// <summary>Must stay PUBLIC: a compiled binding cannot reach a non-public member.</summary>
    public bool RunActive => _root.DownloadRunning || _root.DownloadPaused || HasQueuedWork;
    public string FilesDoneText => RunActive ? _runFilesDone.ToString() : "--";
    public string FilesLeftText => RunActive ? ReachableCount.ToString() : "--";
    public string RunDataDoneText => _meter.SessionBytes > 0 ? ByteFormat.Size(_meter.SessionBytes) : "--";
    public string RunDataLeftText => RunActive && ReachableRemainingBytes > 0 ? ByteFormat.Size(ReachableRemainingBytes) : "--";

    /// <summary>Downloading pane footer, worded exactly like the Moving pane's: how far through the run
    /// this is, in files AND bytes. Without it the pane says what is next but never how much is left.</summary>
    public bool ShowDownloadStats => RunActive
        && (_runFilesDone + ActiveRows.Count + WaitingRows.Count) > 0;   // never "0 of 0 files · 0 B of 0 B"
    /// <summary>The white half has something to say: files done or files that can land. With every queued row
    /// red it read "0 of 0 files · 0 B of 0 B" beside the red segment (QA 09-18); only the red half shows then.</summary>
    public bool ShowReachStats => _runFilesDone + ReachableCount > 0;
    /// <summary>A queue row's total for FOOTER arithmetic: the learned length when known, never below
    /// what has actually arrived - so a file GOG under-sized (or served without a length) cannot zero
    /// its remaining while bytes still flow. That mismatch (session meter climbing, remaining clamped)
    /// is what made the footer's denominator creep at download speed (owner-caught 08-31).</summary>
    private static long EffectiveRowTotal(Grog.Core.Download.DownloadTask t)
        => Math.Max(Math.Max(t.BytesTotal ?? 0, t.File.ExpectedSizeBytes ?? 0), t.BytesReceived);
    private static long StillNeedsOf(Grog.Core.Download.DownloadTask t) => Grog.Core.Volumes.PlacementPlanner.StillNeeds(t.File);
    /// <summary>Bytes of files COMPLETED this run, at their learned sizes (companion to _runFilesDone).</summary>
    /// <summary>Tasks whose completion the settle callback has already folded into <see cref="_runBytesSettled"/>.</summary>
    private readonly HashSet<Guid> _settledTaskIds = new();
    public string DownloadStatsText
    {
        get
        {
            // Only the rows this run can REACH (09-13): a won't-fit row is queued but has nowhere to land, so
            // counting it in "of N files / of X GB" promised a total the run could never hit. Those rows get
            // their own red segment (WontFitStatsText) beside this one.
            var rows = ActiveRows.Concat(WaitingRows).Where(r => !r.WontFit).ToList();
            int total = _runFilesDone + rows.Count;
            // Per-row, not meter-based: done and total come from the SAME rows, so retries, resumed
            // partials and size corrections move both sides together instead of leaking into a creep.
            long doneBytes = _runBytesSettled + rows.Sum(r => Math.Min(r.Task.BytesReceived, EffectiveRowTotal(r.Task)));
            long totalBytes = _runBytesSettled + rows.Sum(r => EffectiveRowTotal(r.Task));
            return $"{_runFilesDone} of {total} file{(total == 1 ? "" : "s")} · "
                 + $"{ByteFormat.Size(doneBytes)} of {ByteFormat.Size(totalBytes)}";
        }
    }

    /// <summary>The red half of the footer: rows flagged won't-fit, count and bytes. Empty when there are none.</summary>
    public string WontFitStatsText
    {
        get
        {
            var red = ActiveRows.Concat(WaitingRows).Where(r => r.WontFit).ToList();
            if (red.Count == 0) return "";
            long bytes = red.Sum(r => StillNeedsOf(r.Task));   // what is still to come, as the banner and the dialogs count it (QA 09-30 A8)
            return $"{red.Count} won't fit · {ByteFormat.Size(bytes)}";
        }
    }
    public bool HasWontFitStats => WontFitStatsText.Length > 0;

    // ---- Status page: readouts that answer a question the duration/count pair cannot ----

    /// <summary>Failures this run. Deliberately NOT paired with a "verified" tally: HTTPS downloads do not
    /// silently corrupt, so a verified counter reads 0/0 forever and trains the eye to ignore it.</summary>
    public string RunFailedText => _runFailed.ToString();

    /// <summary>Status' own primary action: counts the library-wide gap, ignoring Library's filter chips and
    /// row selection. <c>SyncBackupLabel</c> stays Library-scoped, where the narrowing is the point.</summary>
    public string StatusBackupLabel
    {
        get
        {
            if (!_root.IsConnected) return "Connect account";
            if (_library.TotalCount == 0) return "Scan GOG Library";
            int gap = Math.Max(0, _stats.InScopeFiles - _stats.InScopePresentFiles);
            return gap > 0 ? $"Back up · {gap:N0} file{(gap == 1 ? "" : "s")}" : "Backed up ✓";
        }
    }

    /// <summary>Sub-lines for the telemetry cards. Kept as VM strings so the view holds no formatting.</summary>
    // Sub-lines carry a SECOND number the big value lacks; when there is none they are empty, not a placeholder.
    public string AvgSpeedInlineText   => _meter.AvgBytesPerSec > 0 ? $"avg {AvgSpeedText}" : "";
    public string FilesDoneInlineText  => RunActive && _runFilesDone > 0 ? $"{FilesDoneText} done this run" : "";

    /// <summary>The row the free-space readouts speak for: the primary when it is online, else any online
    /// device (three copies of this fallback before 09-02).</summary>
    private BackupLocationRow? PrimaryOnlineRow
        => _root.Storage.BackupLocations.FirstOrDefault(r => r.IsPrimary && r.IsOnline)
        ?? _root.Storage.BackupLocations.FirstOrDefault(r => r.IsOnline);

    public string FreeAfterRunShort
    {
        get
        {
            var row = PrimaryOnlineRow;
            if (row is null) return "--";
            var after = RunActive && ReachableRemainingBytes > 0 ? row.FreeBytes - ReachableRemainingBytes : row.FreeBytes;
            return after < 0 ? "short" : ByteFormat.Size(after);
        }
    }

    /// <summary>Any files given up on this run.</summary>
    public bool RunHasFailures => _runFailed > 0;

    /// <summary>"finishes about 5:28 AM" -- a clock time, not a duration. Empty when there is no usable
    /// estimate, so the caller can hide the line rather than print "--".</summary>
    public string FinishClockText
    {
        get
        {
            if (!RunActive || ReachableCount == 0 || !_root.DownloadRunning || _root.DownloadPaused) return "";
            var secs = _meter.EtaSeconds(ReachableRemainingBytes, ReachableCount);
            if (secs is null || secs.Value <= 0 || double.IsInfinity(secs.Value)) return "";
            var done = DateTimeOffset.Now.AddSeconds(secs.Value).ToLocalTime();
            var sameDay = done.Date == DateTimeOffset.Now.ToLocalTime().Date;
            return sameDay ? $"finishes about {done:h:mm tt}"
                           : $"finishes about {done:h:mm tt}, {done:ddd MMM d}";
        }
    }

    /// <summary>Where the primary folder lands once this run completes: makes a mid-run out-of-space
    /// failure visible BEFORE it happens.</summary>
    public string FreeAfterRunText
    {
        get
        {
            var row = PrimaryOnlineRow;
            if (row is null) return "";
            if (!RunActive || ReachableRemainingBytes <= 0)
                return $"{row.Label}: {ByteFormat.Size(row.FreeBytes)} free of {ByteFormat.Size(row.TotalBytes)}";
            var after = row.FreeBytes - ReachableRemainingBytes;
            return after < 0
                ? $"after this run {row.Label} is short by {ByteFormat.Size(-after)}"
                : $"after this run {row.Label} has {ByteFormat.Size(after)} free";
        }
    }

    /// <summary>True when the queued run does NOT fit on the destination. Drives the warning color.</summary>
    public bool RunWontFit
    {
        get
        {
            var row = PrimaryOnlineRow;
            return row is not null && RunActive && ReachableRemainingBytes > row.FreeBytes;
        }
    }

    // ---- Bottom activity bar = CURRENT OPERATION, not the standing library total ----
    // The ONE operation bar for whatever is live (scan OR download); leads with "Task: <desc>", dims to
    // "Idle", and owns the library-scan progress -- there is no separate scan bar.

    /// <summary>The single "what's happening right now" label for the bottom bar's "Task:" lead. Kept short --
    /// the breakdown lives on the cards up top and the specific file on the Current File line.</summary>
    public string CurrentTaskText
    {
        get
        {
            if (_library.CatalogBusy) return "Scanning GOG library";
            // (09-19) A run that ended with only won't-fit rows left is not downloading: nothing can move.
            if (RunActive && !_root.DownloadRunning && !_root.DownloadPaused && ReachableCount == 0) return "Waiting";
            if (RunActive) return _root.DownloadPaused ? "Paused" : "Downloading";
            return "Idle";
        }
    }

    // Something is running (scan or download); feeds the Overview readouts' run maths.
    public bool BottomActive => _library.CatalogBusy || RunActive;
    // Right-side readout: scan count while scanning, run bytes while downloading, blank when idle.
    public string RunLeadDoneText => ByteFormat.Size(_meter.SessionBytes);
    // (09-19) THIS RUN's total is what the run can REACH: done + what still fits. It used the whole queue, so
    // with won't-fit rows the bar read "1.76 GB / 542 GB - 0%" beside a finish time and a files-left count that
    // were both "what fits": a bar that could never fill. Equal to the queue total when everything fits.
    public string RunTotalText => ByteFormat.Size(_meter.SessionBytes + ReachableRemainingBytes);

    private double BottomRatio => _library.CatalogBusy
        ? Math.Clamp(_library.CatalogProgress / 100.0, 0, 1)
        : (_meter.SessionBytes + ReachableRemainingBytes is long t && t > 0 ? (double)_meter.SessionBytes / t : 0);
    public Avalonia.Controls.GridLength RunDoneCol => new(Math.Max(0, 100.0 * BottomRatio), Avalonia.Controls.GridUnitType.Star);
    public Avalonia.Controls.GridLength RunRemainCol => new(Math.Max(0.0001, 100.0 * (1 - BottomRatio)), Avalonia.Controls.GridUnitType.Star);

    // TASK bar trailing value: overall bytes + % (download) or % (scan).
    public string TaskBarValueText => _library.CatalogBusy ? (BottomRatio > 0 ? $"{BottomRatio * 100:F0}%" : "")
        : RunActive ? $"{RunLeadDoneText} / {RunTotalText} · {BottomRatio * 100:F0}%" : "";

    // ---- Current File bar: the file downloading right now; collapses when none ----
    private QueueRow? ActiveFile => HasActive ? ActiveRows[0] : null;
    public bool ShowFileBar => RunActive && ActiveFile is not null;
    public string FileBarName => ActiveFile?.Name ?? "";
    // Zero-filled placeholder when no file is active (e.g. paused), so the fixed value cell reads as a readout.
    public string FileBarValueText => ActiveFile?.ProgressText ?? "0.0 MB / 0.0 MB · 0%";
    // File bar tinted by the current file's type (same source as the dots/legend): Game=accent, else the hue.
    public IBrush FileBarBrush => ActiveFile is { } r ? ExtraTypeSegment.ColorFor(r.TypeLabel) : Palette.AccentAmber;
    private double FileRatio => Math.Clamp((ActiveFile?.Percent ?? 0) / 100.0, 0, 1);
    public Avalonia.Controls.GridLength FileDoneCol => new(Math.Max(0, 100.0 * FileRatio), Avalonia.Controls.GridUnitType.Star);
    public Avalonia.Controls.GridLength FileRemainCol => new(Math.Max(0.0001, 100.0 * (1 - FileRatio)), Avalonia.Controls.GridUnitType.Star);
    internal void RaiseRunBar()
    {
        OnPropertyChanged(nameof(CurrentTaskText));
        OnPropertyChanged(nameof(BottomActive));
        OnPropertyChanged(nameof(RunLeadDoneText)); OnPropertyChanged(nameof(RunTotalText));
        OnPropertyChanged(nameof(RunDoneCol)); OnPropertyChanged(nameof(RunRemainCol));
        OnPropertyChanged(nameof(TaskBarValueText));
        OnPropertyChanged(nameof(ShowFileBar)); OnPropertyChanged(nameof(FileBarName));
        OnPropertyChanged(nameof(FileBarValueText)); OnPropertyChanged(nameof(FileBarBrush));
        OnPropertyChanged(nameof(FileDoneCol)); OnPropertyChanged(nameof(FileRemainCol));
    }

    internal void RaiseRunStats()
    {
        OnPropertyChanged(nameof(CurrentSpeedText)); OnPropertyChanged(nameof(AvgSpeedText));
        OnPropertyChanged(nameof(ElapsedText)); OnPropertyChanged(nameof(ElapsedInlineText)); OnPropertyChanged(nameof(EtaText));
        OnPropertyChanged(nameof(FilesDoneText)); OnPropertyChanged(nameof(FilesLeftText));
        _root.RaiseRailActivity();
        RaiseRunReadouts();
        OnPropertyChanged(nameof(RunDataDoneText)); OnPropertyChanged(nameof(RunDataLeftText));
        OnPropertyChanged(nameof(DownloadStatsText)); OnPropertyChanged(nameof(ShowDownloadStats)); OnPropertyChanged(nameof(ShowReachStats));
        OnPropertyChanged(nameof(WontFitStatsText)); OnPropertyChanged(nameof(HasWontFitStats));
        OnPropertyChanged(nameof(ShowSpeedGraph));
        RaiseRunBar();
    }
    /// <summary>Extras type segments, biggest first: legend entries plus the bar's proportional columns.
    /// A fixed star-sized Grid gives exact proportions; unused slots collapse to zero width.</summary>
    public ObservableCollection<ExtraTypeSegment> ExtraSegments { get; } = new();
    public bool HasExtraSegments => ExtraSegments.Count > 0;

    private readonly Avalonia.Controls.GridLength[] _extraCols = new Avalonia.Controls.GridLength[8];   // 7 buckets + remainder
    private readonly IBrush?[] _extraBrushes = new IBrush?[7];
    public Avalonia.Controls.GridLength ExtraCol0 => _extraCols[0];
    public Avalonia.Controls.GridLength ExtraCol1 => _extraCols[1];
    public Avalonia.Controls.GridLength ExtraCol2 => _extraCols[2];
    public Avalonia.Controls.GridLength ExtraCol3 => _extraCols[3];
    public Avalonia.Controls.GridLength ExtraCol4 => _extraCols[4];
    public Avalonia.Controls.GridLength ExtraCol5 => _extraCols[5];
    public Avalonia.Controls.GridLength ExtraCol6 => _extraCols[6];
    public Avalonia.Controls.GridLength ExtraColRest => _extraCols[7];
    public IBrush? ExtraBrush0 => _extraBrushes[0];
    public IBrush? ExtraBrush1 => _extraBrushes[1];
    public IBrush? ExtraBrush2 => _extraBrushes[2];
    public IBrush? ExtraBrush3 => _extraBrushes[3];
    public IBrush? ExtraBrush4 => _extraBrushes[4];
    public IBrush? ExtraBrush5 => _extraBrushes[5];
    public IBrush? ExtraBrush6 => _extraBrushes[6];

    internal void RebuildExtraSegments()
    {
        // Runs on every 1 Hz dashboard pass (the legend's sizes follow landed bytes), so the bound list is DIFFED:
        // reset only when the set of legend entries changed, otherwise swap just the entries whose bytes moved
        // (records compare by value). A Clear + re-add per tick re-created the legend for nothing (UI-thread sweep 09-06).
        var fresh = new List<ExtraTypeSegment>();
        for (int i = 0; i < _extraCols.Length; i++) _extraCols[i] = new Avalonia.Controls.GridLength(0, Avalonia.Controls.GridUnitType.Star);
        for (int i = 0; i < _extraBrushes.Length; i++) _extraBrushes[i] = null;

        long total = _stats.ExtraSizeBytes;
        if (total > 0)
        {
            int i = 0;
            foreach (var t in _stats.ExtraTypes.Take(7))
            {
                // Segment width = how much of this type is BACKED UP as a share of all extras; the empty
                // tail of the track is what's still to fetch.
                _extraCols[i] = new Avalonia.Controls.GridLength(100.0 * t.DoneBytes / total, Avalonia.Controls.GridUnitType.Star);
                _extraBrushes[i] = ExtraTypeSegment.ColorFor(t.Name);
                // A type with nothing backed up draws a zero-width segment, so it gets no legend entry:
                // the legend decodes the bar, it is not an inventory of types owned.
                if (t.DoneBytes > 0)
                    fresh.Add(new ExtraTypeSegment(t.Name, _extraBrushes[i]!) { DoneBytes = t.DoneBytes });
                i++;
            }
            var doneShare = 100.0 * _stats.ExtraDoneBytes / total;
            _extraCols[7] = new Avalonia.Controls.GridLength(Math.Max(0.0001, 100 - doneShare), Avalonia.Controls.GridUnitType.Star);
        }
        bool sameKeys = fresh.Count == ExtraSegments.Count;
        if (sameKeys)
            for (int i = 0; i < fresh.Count; i++)
                if (!string.Equals(fresh[i].Name, ExtraSegments[i].Name, StringComparison.Ordinal)) { sameKeys = false; break; }
        if (!sameKeys) { ExtraSegments.Clear(); foreach (var seg in fresh) ExtraSegments.Add(seg); }
        else for (int i = 0; i < fresh.Count; i++) if (ExtraSegments[i] != fresh[i]) ExtraSegments[i] = fresh[i];

        OnPropertyChanged(nameof(HasExtraSegments));
        _library.RaiseLegend();
        OnPropertyChanged(nameof(ExtraCol0)); OnPropertyChanged(nameof(ExtraCol1)); OnPropertyChanged(nameof(ExtraCol2));
        OnPropertyChanged(nameof(ExtraCol3)); OnPropertyChanged(nameof(ExtraCol4)); OnPropertyChanged(nameof(ExtraCol5));
        OnPropertyChanged(nameof(ExtraCol6)); OnPropertyChanged(nameof(ExtraColRest));
        OnPropertyChanged(nameof(ExtraBrush0)); OnPropertyChanged(nameof(ExtraBrush1)); OnPropertyChanged(nameof(ExtraBrush2));
        OnPropertyChanged(nameof(ExtraBrush3)); OnPropertyChanged(nameof(ExtraBrush4));
        OnPropertyChanged(nameof(ExtraBrush5)); OnPropertyChanged(nameof(ExtraBrush6));
    }

    // Percents + whole-library bar read the Core rollup: byte-weighted AND in-flight-aware, so bars climb
    // smoothly during a download. File-count and size text stay on _stats (whole-file counts).
    public double GamesPercent => _rollup.Games.Percent;
    public double ExtrasPercent => _rollup.Extras.Percent;
    public double TotalPercent => _rollup.Whole.Percent;
    public string TotalPercentText => Pct(_rollup.Whole.Percent);
    public string GamesPercentText => Pct(_rollup.Games.Percent);
    public string ExtrasPercentText => Pct(_rollup.Extras.Percent);

    /// <summary>One decimal, but never "0.0%" for something that IS held: 0.13 GB of 472 GB is 0.03%, the bar
    /// shows its sliver, and a text that rounds it to nothing contradicts the bar beside it (owner-hit 09-13).
    /// Same at the top: 99.97% is not "100.0%" until the last byte lands.</summary>
    private static string Pct(double p)
        => p <= 0 ? "0.0%"
         : p < 0.05 ? "<0.1%"
         : p >= 100 ? "100%"
         : p > 99.95 ? ">99.9%"
         : $"{p:F1}%";
    public string TotalSummaryText => $"{TotalFilesText} files  \u00b7  {TotalSizeStripText}";
    /// <summary>Green once everything in scope is backed up; amber while there's still work.</summary>
    public IBrush TotalBarBrush => _rollup.Whole.Percent >= 99.95 ? Palette.SuccessGreen : Palette.AccentAmber;

    // ---- Library coverage table ----
    // Full-library magnitudes; out-of-scope categories dim, never hide. One unit for the whole DATA column,
    // carried by the header ("DATA (GB)"); bars and percents are byte-weighted from these same fields.
    private long CoverageUnitScale => Math.Max(_stats.GameSizeBytes, _stats.ExtraSizeBytes) >= 1L << 30 ? 1L << 30
        : Math.Max(_stats.GameSizeBytes, _stats.ExtraSizeBytes) >= 1L << 20 ? 1L << 20 : 1L << 10;
    public string CoverageDataHeader => $"DATA ({(CoverageUnitScale == 1L << 30 ? "GB" : CoverageUnitScale == 1L << 20 ? "MB" : "KB")})";
    // Split halves: each cell renders num | " / " | num sub-columns so the slashes stack on one x.
    public string GamesFilesDoneText => $"{_stats.GameDoneFileCount}";
    public string GamesFilesTotalText => $"{_stats.GameFileCount}";
    public string ExtrasFilesDoneText => $"{_stats.ExtraDoneFileCount}";
    public string ExtrasFilesTotalText => $"{_stats.ExtraFileCount}";
    public string GamesDataDoneText => $"{(double)GameShownBytes / CoverageUnitScale:0.00}";
    public string GamesDataTotalText => $"{(double)_stats.GameSizeBytes / CoverageUnitScale:0.00}";
    public string ExtrasDataDoneText => $"{(double)ExtraShownBytes / CoverageUnitScale:0.00}";
    public string ExtrasDataTotalText => $"{(double)_stats.ExtraSizeBytes / CoverageUnitScale:0.00}";
    // Totals row under the table (owner 08-31): games + extras summed, same unit, same split-halves
    // cells so the slashes stack; done sides include in-flight partials, so they tick live.
    public string TotalCovFilesDoneText  => $"{_stats.GameDoneFileCount + _stats.ExtraDoneFileCount}";
    public string TotalCovFilesTotalText => $"{_stats.GameFileCount + _stats.ExtraFileCount}";
    public string TotalCovDataDoneText   => $"{(double)(GameShownBytes + ExtraShownBytes) / CoverageUnitScale:0.00}";
    public string TotalCovDataTotalText  => $"{(double)(_stats.GameSizeBytes + _stats.ExtraSizeBytes) / CoverageUnitScale:0.00}";
    public double GamesBytePercent => _stats.GameSizeBytes > 0 ? 100.0 * GameShownBytes / _stats.GameSizeBytes : 0;
    public double ExtrasBytePercent => _stats.ExtraSizeBytes > 0 ? 100.0 * ExtraShownBytes / _stats.ExtraSizeBytes : 0;
    public string GamesBytePercentText => Pct(GamesBytePercent);
    public string ExtrasBytePercentText => Pct(ExtrasBytePercent);
    public string GamesFilesText => $"{_stats.GameDoneFileCount} / {_stats.GameFileCount}";
    public string GamesSizeText => $"{ByteFormat.Size(_stats.GameDoneBytes)} of {ByteFormat.Size(_stats.GameSizeBytes)}";
    public string ExtrasFilesText => $"{_stats.ExtraDoneFileCount} / {_stats.ExtraFileCount}";
    public string ExtrasSizeText => $"{ByteFormat.Size(_stats.ExtraDoneBytes)} of {ByteFormat.Size(_stats.ExtraSizeBytes)}";
    public string TotalFilesText => $"{_stats.AllDoneFileCount} / {_stats.AllFileCount}";
    public string TotalSizeStripText => $"{ByteFormat.Size(_stats.AllDoneBytes)} of {ByteFormat.Size(_stats.AllSizeBytes)}";

    /// <summary>The byte-rollup readouts (bars, percents, coverage) as ONE named set, raised by both the full
    /// dashboard refresh and the in-flight update -- never duplicate this list at a call site.</summary>
    internal void RaiseByteRollup()
    {
        OnPropertyChanged(nameof(GamesPercent)); OnPropertyChanged(nameof(ExtrasPercent)); OnPropertyChanged(nameof(TotalPercent));
        OnPropertyChanged(nameof(GamesPercentText)); OnPropertyChanged(nameof(ExtrasPercentText)); OnPropertyChanged(nameof(TotalPercentText));
        OnPropertyChanged(nameof(TotalBarBrush));
    }

    internal void RaiseRunReadouts()
    {
        OnPropertyChanged(nameof(RunFailedText));
        OnPropertyChanged(nameof(RunHasFailures));
        OnPropertyChanged(nameof(FinishClockText));
        OnPropertyChanged(nameof(RunLedeIsClock)); OnPropertyChanged(nameof(RunLedeIsScan)); OnPropertyChanged(nameof(RunLedeValue));
        OnPropertyChanged(nameof(RunLedeHeadline)); OnPropertyChanged(nameof(RunLedeDetail));
        OnPropertyChanged(nameof(RunLedeBrush));
        OnPropertyChanged(nameof(RunLedePausedReason)); OnPropertyChanged(nameof(RunActive));   // (UX 09-08 #2)
        OnPropertyChanged(nameof(FreeAfterRunText)); OnPropertyChanged(nameof(FreeAfterRunShort));
        OnPropertyChanged(nameof(AvgSpeedInlineText)); OnPropertyChanged(nameof(FilesDoneInlineText));
        OnPropertyChanged(nameof(StatusBackupLabel));
        OnPropertyChanged(nameof(RunWontFit));
    }
}

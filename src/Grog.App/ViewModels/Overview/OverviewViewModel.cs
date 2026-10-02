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

/// <summary>
/// The Overview page's view model (S3.3, 09-08): the health signal and its tally, the lede and the deck, the
/// issues modal with its fix flows, the run readouts (throughput, ETA, speed graph, coverage table, extras
/// segments), the download queue pane with its sort and pill, the activity log and the toast. Split into
/// partials by those topics. It PRESENTS the run: the download machinery (DownloadFiltered, the engine host,
/// the progress feed, the meter and the run counters, Pause / Clear / Resume) stays on the shell as session
/// orchestration and is read through <c>_root</c>; the library stats it grades come from <c>_root.Library</c>.
/// </summary>
public sealed partial class OverviewViewModel : ObservableObject
{
    private readonly MainWindowViewModel _root;
    private readonly GrogSession _session;

    public OverviewViewModel(MainWindowViewModel root, GrogSession session)
    {
        _root = root;
        _session = session;
    }

    // ---- Session / sibling shorthands (same names as the root's, so the moved code reads unchanged) ----
    private LibraryViewModel _library => _root.Library;
    private JsonManifestStore _manifest => _session.Manifest;
    private DownloadEngine? _liveEngine => _session.LiveEngine;
    private string _backupRoot => _session.BackupRoot;
    private Grog.Core.Sync.LibraryStats _stats { get => _library.Stats; set => _library.Stats = value; }
    private Grog.Core.Sync.LibraryRollup _rollup => _library.Rollup;
    private bool _includeGames => _library.IncludeGames;
    private bool _includeExtras => _library.IncludeExtras;
    private Grog.Core.Download.ThroughputMeter _meter => _root.Meter;
    private int _runFilesDone => _root._runFilesDone;
    private int _runFailed => _root._runFailed;
    private long _runBytesSettled => _root._runBytesSettled;
    private void Log(string message, bool isError = false, LogCategory category = LogCategory.General) => _root.Log(message, isError, category);

    // Health signal: derived from durable state (corrupt, gone, offline fixed drives), not log chatter.
    // Counted ONCE per tally in RecomputeHealthTally (one pass over Items x Files) and read back from fields:
    // these getters are hit ~20 times per RaiseHealth, and each used to be a full library walk (UI-thread sweep 09-06).
    private int _healthCorruptCount, _healthGoneCount, _healthUnavailableCount;
    public int HealthCorruptCount => _healthCorruptCount;
    public int HealthGoneCount    => _healthGoneCount;
    public int HealthDeviceProblemCount => _root.Storage.BackupLocations.Count(d => d.IsProblem);
    /// <summary>Files GOG refuses to serve. Not in HealthIssueCount: not actionable, so it never reddens
    /// the rail badge or page verdict; it gets its own calm row.</summary>
    public int HealthUnavailableCount => _healthUnavailableCount;
    // A failed run counts as ONE issue however many files it hit: one event, one cause.
    public int HealthIssueCount => HealthCorruptCount + HealthGoneCount + HealthDeviceProblemCount
                                 + (HasRunFailure ? 1 : 0);
    public bool HealthOk => HealthIssueCount == 0;
    public bool ShowHealthBadge => HealthIssueCount > 0;
    /// <summary>The rail badge says what it counts when there is room ("10 issues"); collapsed to the icon
    /// rail it is the bare number. Issues outrank the queue count beside it, so they must never read as
    /// "10 what?" (owner 09-02).</summary>
    public string HealthBadgeText => HealthIssueCount == 0 ? ""
        : !_root.RailExpanded ? HealthIssueCount.ToString()
        : HealthIssueCount == 1 ? "1 issue" : $"{HealthIssueCount} issues";
    public string HealthBadgeTip => HealthIssueCount == 1
        ? "1 issue needs attention: see Local Library Health on the Overview"
        : $"{HealthIssueCount} issues need attention: see Local Library Health on the Overview";

    // Rail row state: the rail is the summary, pages are the detail. Two readouts only.
    /// <summary>(09-19) THE rail's library fraction, one reader for its three texts. Counts every game the
    /// library HOLDS that is in scope and not hidden (HiddenPolicy: the mature toggle and the per-game picks), so
    /// the rail agrees with a grid that hides them. It read the FILTERED grid (Library.Games): right for hidden
    /// games by accident, and wrong the moment a search or a filter narrowed the grid.</summary>
    private (int Done, int All) RailLibraryCounts()
    {
        var hidden = _library.Hidden;
        var rows = _library.AllGames.Where(g => !g.NotInScope && (_library.ShowLegacyMovies || g.Type != ProductType.Movie)
                                                && (g.Item is null || hidden.IsVisible(g.Item))).ToList();
        return (rows.Count(g => g.StatusText == "Backed Up"), rows.Count);
    }
    /// <summary>Tooltip for the rail's bare fraction; it owns the units -- GAMES in scope, not files.</summary>
    public string RailLibraryTip
    {
        get
        {
            var (done, all) = RailLibraryCounts();
            if (all == 0) return "Library";
            return $"Library - {done} of {all} games in scope fully backed up";
        }
    }

    public string RailLibraryCount
    {
        get
        {
            if (_railLibraryOverride is { } forced) return forced;   // render harness only (see .Shots.cs)
            // Scoped games only: an excluded game can never become "Backed Up", so the fraction must
            // exclude it or it can never reach full. A count uses scope.
            var (done, all) = RailLibraryCounts();
            if (all == 0) return "";
            return $"{done}/{all} Games";
        }
    }
    /// <summary>(UX 09-08 #6) Library rail second line, the verb spelled out: "{done} of {total} items backed
    /// up". Same scoped counts as RailLibraryCount ("Items": games and movies both live in the grid).</summary>
    public string RailLibrarySub
    {
        get
        {
            var (done, all) = RailLibraryCounts();
            // (Rail exceptions 09-09) "N new" used to ride INSIDE this line to avoid a third rail row. It was
            // being truncated to "..." at the real rail width, so the count it existed to show was invisible.
            // It lives on its own line now; this one states the standing fact only, in the shape every rail
            // line uses ("N of M backed up").
            if (all == 0) return "nothing to back up yet";
            return $"{done} of {all} backed up";
        }
    }
    /// <summary>(Rail exceptions 09-09) Library's outstanding items, worst first. The rail is the one
    /// surface you see without opening a page, so it earns a line per exception rather than cramming them
    /// into one that then truncates. Ordered by the SAME grading Core uses (Fault above Attention): data you
    /// had and lost outranks data you have not fetched yet, so "missing" can never be hidden behind "new".
    /// Empty in the healthy case -- green is assumed and never reported.</summary>
    public IReadOnlyList<RailException> RailLibraryExceptions
    {
        get
        {
            var list = new List<RailException>();
            if (HealthGoneCount > 0) list.Add(new($"{HealthGoneCount} missing", Grog.Core.Sync.Severity.Fault));
            if (HealthCorruptCount > 0) list.Add(new($"{HealthCorruptCount} corrupt", Grog.Core.Sync.Severity.Fault));
            int fresh = _library.NewItemCount;
            if (fresh > 0) list.Add(new($"{fresh} new", Grog.Core.Sync.Severity.Attention));
            return list;
        }
    }

    /// <summary>(UX 09-08 #6) Overview rail second line: what the queue holds, in Files.</summary>
    public string RailOverviewSub
    {
        get
        {
            // (09-22, owner) The rail line says what is HAPPENING, not only what is queued: downloading, moving, both.
            int n = DownloadQueueCount;
            int moving = _root.Storage.IsReorgRunning ? _root.Storage.MovesLeft : 0;
            if (_railSubOverride is { } forced) return forced;   // render harness only (see .Shots.cs)
            string dl = _root.DownloadRunning && !_root.DownloadPaused ? $"downloading {Plural.Of(n, "file")}"
                      : _root.DownloadPaused && n > 0 ? $"paused, {Plural.Of(n, "file")} queued"
                      : n > 0 ? $"{Plural.Of(n, "file")} queued" : "";
            string mv = moving > 0 ? $"moving {Plural.Of(moving, "file")}" : "";
            if (dl.Length > 0 && mv.Length > 0) return dl + "\n" + mv;   // one verb per line: the rail is narrow (owner 09-22)
            if (dl.Length > 0) return dl;
            if (mv.Length > 0) return mv;
            return "nothing queued";
        }
    }

    /// <summary>(09-22, owner) The rail line is amber while something is HAPPENING (downloading or moving), dim otherwise.</summary>
    public IBrush RailOverviewSubBrush =>
        (_root.DownloadRunning && !_root.DownloadPaused) || (_root.Storage.IsReorgRunning && _root.Storage.MovesLeft > 0)
            ? Palette.AccentAmber : Palette.InkDim;

    internal void RaiseRailOverviewSub() { OnPropertyChanged(nameof(RailOverviewSub)); OnPropertyChanged(nameof(RailOverviewSubBrush)); }   // (UX 09-08 #6)

    /// <summary>Primary's drive for the Folders row: "F:" on Windows, the last path segment elsewhere.</summary>
    public string RailPrimaryDrive
    {
        get
        {
            var p = _root.Storage.PrimaryLocation?.Path;
            if (string.IsNullOrWhiteSpace(p)) return "";
            var root = System.IO.Path.GetPathRoot(p);
            if (!string.IsNullOrWhiteSpace(root) && root.Contains(':')) return root.TrimEnd('\\', '/');
            var name = p.TrimEnd('\\', '/');
            int i = name.LastIndexOfAny(new[] { '\\', '/' });
            return i >= 0 && i < name.Length - 1 ? name[(i + 1)..] : name;
        }
    }

    // No count badge on the Folders rail item: it carries a red dot bound to HasDeviceProblem instead.

    /// <summary>Rail Health icon color: red = issues, amber = incomplete, gray = nothing yet, green = full.
    /// Mirrors the page verdict so the rail never reads green while the backup is incomplete.</summary>
    public IBrush HealthDotBrush =>
        !HealthOk            ? Palette.ErrorRed
      : !HealthHasAnyBackup  ? Palette.InkDim
      : !HealthFullyBackedUp ? Palette.AccentAmber
      : Palette.SuccessGreen;
    // One scoped tally feeds the verdict and every vitals tile, so Coverage and Verified cannot disagree.
    // The math lives in Core (LibraryStats.From); this method only refreshes the cached copy.
    private void RecomputeHealthTally()
    {
        _stats = _manifest is null ? Grog.Core.Sync.LibraryStats.Empty
            : Grog.Core.Sync.LibraryStats.From(_manifest.Current, _library.EffectiveScope, _library.ShowLegacyMovies);

        // PARTIAL bytes ride beside the tally for the coverage DATA figures only (owner call 2026-08-29:
        // one big installer must make the GB creep, not sit frozen for an hour). File COUNTS stay
        // settled-only - a half file is not a file you have - and the verdict/queue logic stays on the
        // two present-rules; this is display arithmetic, never a third "present" rule.
        _partialGameBytes = 0; _partialExtraBytes = 0;
        // The fault counts and the drill-down rows come out of this same pass (UI-thread sweep 09-06): they are
        // UNSCOPED (every file the manifest holds), so they are taken before the scope test below.
        _healthCorruptCount = 0; _healthGoneCount = 0; _healthUnavailableCount = 0;
        var corruptRows = new List<HealthFileRow>(); var goneRows = new List<HealthFileRow>();
        if (_manifest is not null)
        {
            var scope = _library.EffectiveScope;
            foreach (var item in _manifest.Current.Items)
                foreach (var f in item.Files)
                {
                    switch (f.State)
                    {
                        case FileState.Corrupt: _healthCorruptCount++; corruptRows.Add(HealthFileRow.From(item, f)); break;
                        case FileState.Missing: _healthGoneCount++; goneRows.Add(HealthFileRow.From(item, f)); break;
                        case FileState.Unavailable: _healthUnavailableCount++; break;
                    }
                    if (!scope.Includes(f) || !f.HasPartial || (f.PartialBytes ?? 0) <= 0) continue;
                    if (Grog.Core.Sync.BackupScope.IsPresent(f)) continue;   // finished files already count
                    if (f.Kind == Grog.Core.Models.FileKind.Extra) _partialExtraBytes += f.PartialBytes!.Value;
                    else _partialGameBytes += f.PartialBytes!.Value;
                }
        }
        _healthCorruptRows = corruptRows; _healthGoneRows = goneRows;
    }
    private List<HealthFileRow> _healthCorruptRows = new(), _healthGoneRows = new();

    private long _partialGameBytes, _partialExtraBytes;
    /// <summary>Done-plus-in-flight bytes for the coverage card's DATA readouts.</summary>
    internal long GameShownBytes => _stats.GameDoneBytes + _partialGameBytes;
    internal long ExtraShownBytes => _stats.ExtraDoneBytes + _partialExtraBytes;

    /// <summary>How stale a catalog check may get before its derived counts stop being asserted. Tied to
    /// the user's own schedule interval; a week is the fallback with no schedule.</summary>
    private TimeSpan CatalogStaleAfter
    {
        get
        {
            var sc = _root.Sched;
            if (sc is null || !sc.Enabled) return TimeSpan.FromDays(7);
            var next = Grog.Core.Scheduling.SchedulePlanner.NextFire(sc, DateTimeOffset.Now);
            var span = next is { } n && sc.LastRun is { } lr && n > lr ? n - lr : TimeSpan.FromDays(1);
            return span < TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : span;
        }
    }

    /// <summary>The Status headline, chosen by severity. Ranking lives in Core (StatusLede), tested there.</summary>
    private Grog.Core.Sync.StatusLede.Lede Lede => Grog.Core.Sync.StatusLede.Pick(new(
        OfflineFolders: HealthDeviceProblemCount,
        MissingFiles: HealthGoneCount,
        // Minus in-flight partials, so the "X GB to back up" headline creeps DOWN through a big file at
        // the same 1 Hz the coverage GB creeps up - the two figures must not tell different stories.
        // Display arithmetic only; the Kind/verdict still grades on the settled tally.
        UnprotectedBytes: Math.Max(0, _stats.GapBytes - _partialGameBytes - _partialExtraBytes),
        UnprotectedFiles: Math.Max(0, _stats.InScopeFiles - _stats.InScopePresentFiles),
        TotalFiles: _stats.InScopeFiles,
        ProtectedFiles: _stats.InScopePresentFiles,
        ProtectedBytes: _stats.PresentBytes,
        OutdatedGames: _library.UpdatesCount,
        SinceCatalogCheck: _stats.LastApiRefresh is { } t ? DateTimeOffset.Now - t : null,
        StaleAfter: CatalogStaleAfter,
        FailedLastRun: _lastRunFailure?.Failed ?? 0,
        FailedReason: _lastRunFailure?.Reason));

    // Last run's failures, read from the run journal: a run can fail without touching the manifest
    // (no space, drive pulled, network gone), so the journal is the durable record of the attempt.
    private Grog.Core.Runs.RunOutcome? _lastRunFailure;
    /// <summary>The whole last-run summary (any outcome), for the Schedule card's "last run result" line.</summary>
    internal Grog.Core.Runs.RunSummary? _lastRunSummary;
    internal Grog.Core.Runs.RunSummary? LastRunSummary => _lastRunSummary;

    /// <summary>Re-reads the last run's outcome. Cheap (one small file), so it runs with every health
    /// refresh; the journal only reports a run that ended badly.</summary>
    private long _lastRunReadAt = long.MinValue / 2;
    private bool _lastRunDirty = true;

    /// <summary>Something that changes the journal happened (a settle, a run end, a Fresh Library); the next
    /// RaiseHealth re-reads it. Between such events the record cannot change, so it is not re-read.</summary>
    internal void MarkLastRunDirty() => _lastRunDirty = true;

    private void RefreshLastRunFailure()
    {
        if (string.IsNullOrEmpty(_backupRoot)) { _lastRunFailure = null; _lastRunFailureRaw = null; _lastRunSummary = null; return; }
        // Measured 09-05 (grog.log): this ran every 1 Hz tick and cost 0.9 s - GrogPaths.Resolve(root) probes
        // the BACKUP ROOT on disk (a legacy-state check on the USB stick) and then the journal is read again.
        // The config dir never moves during a session and the journal only changes on a settle: resolve once,
        // re-read on change or at most every 5 s.
        long now = Environment.TickCount64;
        if (!_lastRunDirty && now - _lastRunReadAt < 5000) { ApplyUnaddressed(); return; }   // no re-read; the queue may have changed
        if (_lastRunReading) return;   // one read in flight; the dirty flag (still set) re-arms the next pass
        _lastRunDirty = false; _lastRunReadAt = now;
        // The two journal reads are file IO on the profile dir: on the pool, result posted back; the bound
        // properties raise once the record actually changed (UI-thread sweep 09-06 r2). Same throttle as before.
        _lastRunReading = true;
        int gen = _lastRunGen;   // a Dismiss while this read is in flight must win over its result (QA 09-30 C12)
        bool runLive = _root.DownloadRunning;   // a live run's journal is not the last run (QA 09-30 C2); a crashed run's is
        _ = Task.Run(() =>
        {
            try
            {
                var cfg = Grog.Core.Storage.GrogPaths.ResolveConfigDir();   // profile folder only; never touches the backup root
                return (Failure: Grog.Core.Runs.RunJournalReader.ReadLastFailure(cfg, readUnfinished: !runLive),
                        Summary: Grog.Core.Runs.RunJournalReader.ReadLast(cfg));
            }
            catch { return (Failure: (Grog.Core.Runs.RunOutcome?)null, Summary: (Grog.Core.Runs.RunSummary?)null); }
        }).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _lastRunReading = false;
                if (!t.IsCompletedSuccessfully || gen != _lastRunGen) return;
                var (failure, summary) = t.Result;
                if (string.IsNullOrEmpty(_backupRoot)) return;   // unbound meanwhile: the sync path above already nulled the fields
                if (summary is null && _lastRunSummary is not null) summary = _lastRunSummary;   // Dismiss removed the file; the Schedule card keeps its line (C12)
                _lastRunFailureRaw = failure;
                failure = Unaddressed(failure);
                // RunOutcome is a record struct (value equality); the summary compares by the fields the readouts use.
                bool changed = !Nullable.Equals(_lastRunFailure, failure) || !SameSummary(_lastRunSummary, summary);
                _lastRunFailure = failure; _lastRunSummary = summary;
                if (changed)
                {
                    OnPropertyChanged(nameof(HasRunFailure)); OnPropertyChanged(nameof(RunFailureIssueText));
                    RaiseIssueCountSet();   // (09-26) the rail icon + badge read HasRunFailure too: the card cleared, the icon stayed red
                    RebuildHealthChecks(retally: false);   // the "Last run" row reads _lastRunFailure
                    RaiseLede();   // the headline counts the same failures: it read "1 file" beside "3 files failed" (walk 09-19)
                    _root.Schedule.RaiseReadouts();              // the SCHEDULE card's last-run lines read the summary
                }
            });
        }, TaskScheduler.Default);
    }
    private bool _lastRunReading;
    private int _lastRunGen;
    private static bool SameSummary(Grog.Core.Runs.RunSummary? a, Grog.Core.Runs.RunSummary? b)
        => a is null ? b is null
         : b is not null && a.RunId == b.RunId && a.State == b.State && a.UpdatedAt == b.UpdatedAt
           && a.Total == b.Total && a.Completed == b.Completed && a.Failed == b.Failed && a.CurrentFile == b.CurrentFile;

    public bool HasRunFailure => _lastRunFailure is { Failed: > 0 };

    /// <summary>(1280) A failed file that is back in the queue is addressed: the next Resume retries it, so the card
    /// has nothing to say about it; only files that failed and are NOT queued remain a failure. A journal from before
    /// the keys were written names no file, so its count stands as is.</summary>
    private Grog.Core.Runs.RunOutcome? _lastRunFailureRaw;
    private void ApplyUnaddressed()
    {
        var now = Unaddressed(_lastRunFailureRaw);
        if (Nullable.Equals(_lastRunFailure, now)) return;
        _lastRunFailure = now;
        OnPropertyChanged(nameof(HasRunFailure)); OnPropertyChanged(nameof(RunFailureIssueText));
        RaiseIssueCountSet(); RebuildHealthChecks(retally: false); RaiseLede();
    }
    private Grog.Core.Runs.RunOutcome? Unaddressed(Grog.Core.Runs.RunOutcome? failure)
    {
        if (failure is not { } f || f.FailedKeys.Length == 0 || _session.Manifest is not { } store) return failure;
        // Distinct files (a file re-added mid-run can fail twice: two journal lines, one file; QA 09-30 C8). A condemned
        // (Corrupt) file is counted and fixed under Corrupt already; counting it here too made one file two issues (C10).
        int left = store.Read(m => f.FailedFiles().Distinct().Count(k => !m.Downloads.Contains(k.GogId, k.FileKey)
            && m.ItemById(k.GogId)?.Files.FirstOrDefault(x => x.FileKey == k.FileKey)?.State != FileState.Corrupt));
        return left == 0 ? null : f with { Failed = left };
    }

    /// <summary>(09-26) Every readout of <see cref="HealthIssueCount"/> moves together: the Health card, the Overview
    /// rail icon and the rail badge. Any change to one of its inputs (here: the last-run failure) raises all of them.</summary>
    private void RaiseIssueCountSet()
    {
        OnPropertyChanged(nameof(HealthIssueCount)); OnPropertyChanged(nameof(HealthOk)); OnPropertyChanged(nameof(ShowHealthBadge));
        OnPropertyChanged(nameof(HealthBadgeText)); OnPropertyChanged(nameof(HealthBadgeTip)); OnPropertyChanged(nameof(HealthDotBrush));
        _root.RaiseHealthBadgeInRail();
    }

    /// <summary>Forget the last run's failure record (Fresh Library reset, or a later run that ended clean):
    /// the journal's current.json is what ReadLastFailure reads on every refresh, so the file goes too.</summary>
    internal void ClearRunJournalFailure()
    {
        // The summary stays: Dismiss clears the failure, not the Schedule card's "N of M files" line (QA 09-30 C12).
        _lastRunFailure = null; _lastRunFailureRaw = null; _lastRunGen++; MarkLastRunDirty();
        OnPropertyChanged(nameof(HasRunFailure)); OnPropertyChanged(nameof(RunFailureIssueText));
        RaiseIssueCountSet();
        if (string.IsNullOrEmpty(_backupRoot)) return;
        if (_root.DownloadRunning) return;   // the file on disk is the LIVE run's journal: never delete it from under the run (QA 09-30 C2)
        // Synchronous and BEFORE any requeue: a background delete landing after the next run wrote its own
        // current.json would erase the live journal (review 09-06). The profile dir is local; one unlink.
        try
        {
            var cfg = Grog.Core.Storage.GrogPaths.ResolveConfigDir();
            var cur = System.IO.Path.Combine(cfg, "runs", "current.json");
            if (System.IO.File.Exists(cur)) System.IO.File.Delete(cur);
        }
        catch { /* best-effort: the in-memory flag is already cleared */ }
    }
    public string RunFailureIssueText => _lastRunFailure is { } r
        ? $"{Grog.Core.Format.Plural.Of(r.Failed, "file")} failed in the last backup run."
          + (r.ConnectionLost ? $" {char.ToUpperInvariant(ConnectionLostShort[0])}{ConnectionLostShort[1..]}; Resume picks up where they stopped."   // (09-22) the symptom, said plainly
             : string.IsNullOrWhiteSpace(r.Reason) ? "" : $" {r.Reason!.TrimEnd('.')}.")
        : "";
    /// <summary>The one phrase every surface uses for a run that failed because the connection went away.</summary>
    internal const string ConnectionLostShort = "connection lost";

    // The run's own lede: a transfer in flight headlines WHEN IT ENDS; when idle the state word leads.

    /// <summary>A finish clock only while a run is MOVING: after "Backup finished" with leftovers queued, the
    /// meter's last rate projected a finish time over a stopped queue (owner-hit 09-17).</summary>
    public bool RunLedeIsClock => _root.DownloadRunning && !_root.DownloadPaused && !_root.FinishingCurrent && FinishClockValue is not null
        && !_root.Storage.HasWaitingDrive;
    /// <summary>A catalog scan IS the current run while it lasts, so it outranks the download state here;
    /// otherwise the lede reads "Paused" over a bar filling with scan progress.</summary>
    public bool RunLedeIsScan => _library.CatalogBusy;

    private DateTimeOffset? FinishClockValue
    {
        get
        {
            if (!RunActive || ReachableCount == 0 || !_root.DownloadRunning || _root.DownloadPaused) return null;
            var secs = _meter.EtaSeconds(ReachableRemainingBytes, ReachableCount);
            if (secs is null || secs.Value <= 0 || double.IsInfinity(secs.Value)) return null;
            return DateTimeOffset.Now.AddSeconds(secs.Value);
        }
    }

    /// <summary>The big value: a clock time while running, otherwise the run state.</summary>
    public string RunLedeValue
    {
        get
        {
            if (RunLedeIsScan) return "Scanning";
            if (FinishClockValue is { } f && RunLedeIsClock)
            {
                var t = f.ToLocalTime();
                return t.Date == DateTimeOffset.Now.ToLocalTime().Date ? $"{t:h:mm tt}" : $"{t:h:mm tt}";
            }
            return CurrentTaskText;
        }
    }

    /// <summary>The sentence the value completes.</summary>
    public string RunLedeHeadline
    {
        get
        {
            if (RunLedeIsScan) return "your GOG library for changes";
            // (09-25) A run held up by a drive that is slow to answer says so, instead of an ETA that stops moving.
            if (_root.DownloadRunning && !_root.DownloadPaused && _root.Storage.HasWaitingDrive)
                return $"waiting for {_root.Storage.WaitingDrives} to respond";
            if (RunLedeIsClock)
            {
                var t = FinishClockValue!.Value.ToLocalTime();
                var tomorrow = t.Date > DateTimeOffset.Now.ToLocalTime().Date;
                return tomorrow ? "tomorrow is when this backup should finish"
                                : "is when this backup should finish";
            }
            // Paused gets no headline: the resume button beside it already says what to do.
            if (_root.DownloadPaused) return "";
            if (_root.DownloadRunning && _root.FinishingCurrent) return _root.FinishingLabel.ToLowerInvariant();   // "finishing 3 files…"
            if (_root.DownloadRunning) return "working out how long this will take";   // running, ETA not settled yet
            if (HasQueuedWork)  return ReachableCount == 0 ? "nothing in the queue fits" : "queued and ready to go";
            return "nothing queued";
        }
    }

    /// <summary>(UX 09-08 #2b) Paused is a state, not an error: the big word keeps its size and this dim
    /// inline reason says why nothing moves. Empty unless the run is paused (and no scan outranks it).</summary>
    public string RunLedePausedReason
    {
        get
        {
            if (RunLedeIsScan || !_root.DownloadPaused || !RunActive) return "";
            if (_root.PausedAfterFinish) return "\u2014 finished the files in progress";
            if (_root.Storage.PausedForDriveLabel is { } drive) return $"\u2014 waiting for {drive} to be reconnected";   // (09-25)
            return _root.QueueNeedsAccount ? "\u2014 waiting for you to connect an account" : "\u2014 paused by you";
        }
    }

    public string RunLedeDetail
    {
        get
        {
            // Only the scan keeps a detail line; the telemetry row and progress bar cover the rest.
            if (RunLedeIsScan) return _library.CatalogDetail;
            return "";
        }
    }

    public IBrush RunLedeBrush => RunLedeIsScan  ? Palette.AccentAmber
                                : RunLedeIsClock ? Palette.SuccessGreen
                                : _root.DownloadPaused ? Palette.AccentAmber
                                : Palette.InkMuted;

    public string LedeValue    => Lede.Value;
    public string LedeHeadline => Lede.Headline;
    public string LedeDetail   => Lede.Detail;
    /// <summary>How much of the gap is already queued, beside the headline (owner 08-31): "to Back Up"
    /// alone reads as if nothing is planned when a queue is sitting right there. GAP LEDES ONLY - on
    /// the longer fault headlines (files gone) the note is off-topic and clipped into the transport
    /// buttons at 1366 (render-measured).</summary>
    public string LedeQueuedNote => DownloadQueueCount > 0
        && Lede.Kind is Grog.Core.Sync.StatusLede.Kind.Gap or Grog.Core.Sync.StatusLede.Kind.Stale
        ? $"({ByteFormat.Size(DownloadQueueBytes)} queued)" : "";
    /// <summary>Graded by Core, colored by the one mapping. AllClear alone is green: this headline is the
    /// one place that states health outright; everywhere else green stays banned (manage by exception).</summary>
    public IBrush LedeBrush => Lede.Kind == Grog.Core.Sync.StatusLede.Kind.AllClear
        ? Palette.SuccessGreen
        : SeverityBrush.For(Grog.Core.Sync.StatusLede.SeverityOf(Lede.Kind));
    internal void RaiseLede()
    {
        OnPropertyChanged(nameof(LedeValue)); OnPropertyChanged(nameof(LedeHeadline));
        OnPropertyChanged(nameof(LedeDetail)); OnPropertyChanged(nameof(LedeBrush));
        OnPropertyChanged(nameof(LedeQueuedNote));
        OnPropertyChanged(nameof(DeckOutdatedText)); OnPropertyChanged(nameof(DeckOutdatedSub));
        OnPropertyChanged(nameof(DeckOutdatedBrush)); OnPropertyChanged(nameof(DeckFoldersText));
        OnPropertyChanged(nameof(DeckFoldersSub)); OnPropertyChanged(nameof(DeckFoldersBrush));
        OnPropertyChanged(nameof(DeckMissingText)); OnPropertyChanged(nameof(DeckMissingSub));
        OnPropertyChanged(nameof(DeckMissingBrush));
        RaiseHealthStatus();
    }

    // The deck: three supporting findings under the lede. Fixed at three so the row keeps its shape.

    public string DeckOutdatedText
    {
        get
        {
            if (Grog.Core.Sync.StatusLede.IsStale(_stats.LastApiRefresh is { } t ? DateTimeOffset.Now - t : null, CatalogStaleAfter))
                return "GOG hasn't been checked recently";
            // Scoped wording: this finding compares held files' versions against GOG; unfetched files
            // are the lede's job, so the cell names only what it actually measured.
            if (_stats.InScopePresentFiles == 0) return "Nothing backed up to compare yet";
            if (_library.UpdatesCount == 0) return "Your copies all match GOG";
            return _library.UpdatesCount == 1 ? "1 game has a newer version on GOG"
                                     : $"{_library.UpdatesCount} games have newer versions on GOG";
        }
    }
    public string DeckOutdatedSub => _stats.LastApiRefresh is { } t
        ? $"checked {Grog.Core.Sync.StatusLede.Describe(DateTimeOffset.Now - t)} ago"
        : "your GOG library hasn't been scanned yet";
    public IBrush DeckOutdatedBrush =>
        Grog.Core.Sync.StatusLede.IsStale(_stats.LastApiRefresh is { } t ? DateTimeOffset.Now - t : null, CatalogStaleAfter)
            ? Palette.AccentAmber
            : _library.UpdatesCount > 0 ? Palette.AccentAmber : Palette.SuccessGreen;

    // Health status line, manage by exception: silent when good, worst active finding when not.
    /// <summary>A folder situation worth reporting: none at all, or one that should be here and isn't.
    /// Deliberately NOT "fewer online than total" -- a safely-unplugged removable is not a fault.</summary>
    private bool HealthFoldersBad => _root.Storage.BackupLocations.Count == 0 || HealthDeviceProblemCount > 0;
    private bool HealthCheckStale => Grog.Core.Sync.StatusLede.IsStale(
        _stats.LastApiRefresh is { } t ? DateTimeOffset.Now - t : null, CatalogStaleAfter);

    /// <summary>True while the launch pass stats every backed-up file; the health line says so instead of a
    /// missing count it cannot yet stand behind.</summary>
    private bool _diskCheckRunning;
    internal void SetDiskCheckRunning(bool on)
    {
        if (_diskCheckRunning == on) return;
        _diskCheckRunning = on;
        RaiseHealthStatus();
    }

    /// <summary>The one-line health readout: worst finding first, "Up to date" only when all of them pass.</summary>
    public string HealthStatusText
    {
        get
        {
            if (_diskCheckRunning)   return "Checking files on disk…";
            if (HealthFoldersBad)    return DeckFoldersText;
            if (HealthGoneCount > 0) return DeckMissingText;
            // The rail badge counts a failed run as an issue; the page must say the same thing in the same
            // place, or the badge is a red number with no story (owner 09-02).
            if (HasRunFailure)       return RunFailureIssueText;
            // Stale knowledge IS an exception: it means the number above may be understated.
            if (HealthCheckStale)    return DeckOutdatedText;
            // Only when there is a catalog to be short against; the empty-library lede already covers it.
            if (_stats.InScopePresentFiles == 0 && _library.TotalCount > 0) return "Nothing backed up yet";
            // Silence is the report: an all-clear wording reads as "done" beside a gap headline.
            return "";
        }
    }
    /// <summary>Renders only when something is wrong; the good state is the line's absence.</summary>
    public bool HasHealthStatus => HealthStatusText.Length > 0;
    /// <summary>Same grading as the headline above, so one card's two lines never disagree on severity.</summary>
    public IBrush HealthStatusBrush => _diskCheckRunning ? Palette.InkDim : SeverityBrush.For(
        HealthFoldersBad || HealthGoneCount > 0 || HasRunFailure
            ? Grog.Core.Sync.Severity.Fault
            : Grog.Core.Sync.Severity.Attention);
    internal void RaiseHealthStatus()
        { OnPropertyChanged(nameof(HealthStatusText)); OnPropertyChanged(nameof(HealthStatusBrush)); OnPropertyChanged(nameof(HasHealthStatus)); }

    public string DeckFoldersText
    {
        get
        {
            if (_root.Storage.BackupLocations.Count == 0) return "No backup storage set";
            if (HealthDeviceProblemCount > 0)
                return HealthDeviceProblemCount == 1 ? "Storage that should be connected isn't"
                                                     : $"{HealthDeviceProblemCount} storage locations that should be connected aren't";
            // The healthy remainder is not reported: the headline and per-folder rail dots cover it.
            return "";
        }
    }
    public string DeckFoldersSub
    {
        get
        {
            var online = _root.Storage.BackupLocations.Where(d => d.IsOnline).ToList();
            if (online.Count == 0) return "";
            long free = online.Sum(d => d.FreeBytes);
            // Free space alone belongs on Folders; surface it here only when it is a problem.
            return _stats.GapBytes > 0 && free < _stats.GapBytes
                ? $"{ByteFormat.Size(free)} free - not enough for the rest" : "";
        }
    }
    public IBrush DeckFoldersBrush => HealthDeviceProblemCount > 0 ? Palette.ErrorRed
        : _root.Storage.BackupLocations.Count == 0 ? Palette.AccentAmber : Palette.SuccessGreen;

    public string DeckMissingText => HealthGoneCount == 0 ? "Nothing has gone missing"
        : HealthGoneCount == 1 ? "1 backed-up file is gone from disk"
                               : $"{HealthGoneCount} backed-up files are gone from disk";
    // No sub-line under the all-clear; the problem case keeps one because it adds to the heading.
    public string DeckMissingSub => HealthGoneCount == 0 ? "" : "still in the manifest, no longer on the drive";
    public IBrush DeckMissingBrush => HealthGoneCount > 0 ? Palette.ErrorRed : Palette.SuccessGreen;

    private bool HealthHasAnyBackup  => _stats.InScopePresentFiles > 0;
    private bool HealthFullyBackedUp => _stats.InScopeFiles > 0 && _stats.InScopePresentFiles >= _stats.InScopeFiles;
    private bool HealthFullyVerified => _stats.InScopePresentFiles > 0 && _stats.VerifiedFiles >= _stats.InScopePresentFiles;

    public string HealthVerdict
    {
        get
        {
            if (!HealthOk)             return $"{HealthIssueCount} issue{(HealthIssueCount == 1 ? "" : "s")} need attention.";
            if (!HealthHasAnyBackup)   return "Nothing's backed up yet.";
            if (!HealthFullyBackedUp)  return "Your library isn't fully backed up yet.";
            if (!HealthFullyVerified)  return "Everything's backed up.";
            return "Everything's backed up and verified.";
        }
    }

    /// <summary>Health-page verdict color: red = issues, amber = incomplete, gray = nothing yet, green =
    /// full. The rail dot stays on <see cref="HealthDotBrush"/> so an in-progress backup never alarms it.</summary>
    public IBrush HealthVerdictBrush =>
        !HealthOk            ? Palette.ErrorRed
      : !HealthHasAnyBackup  ? Palette.InkDim
      : !HealthFullyBackedUp ? Palette.AccentAmber
      : Palette.SuccessGreen;

    /// <summary>The line under the verdict: the specifics behind whatever the verdict just claimed.</summary>
    public string HealthDetail
    {
        get
        {
            if (!HealthOk)
            {
                var parts = new List<string>();
                if (HealthCorruptCount > 0) parts.Add($"{HealthCorruptCount} corrupt");
                if (HealthGoneCount > 0) parts.Add($"{HealthGoneCount} missing from disk");
                if (HealthDeviceProblemCount > 0) parts.Add($"{HealthDeviceProblemCount} drive problem{(HealthDeviceProblemCount == 1 ? "" : "s")}");
                return string.Join("  ·  ", parts);
            }
            // The tiles below carry the numbers; the verdict detail stays qualitative.
            if (!HealthHasAnyBackup)
                return "Nothing's protected yet - back up your library so a delisted game or lost account can't take it with you.";
            if (!HealthFullyBackedUp)
                return "Some of your library isn't backed up yet. Run Back up to protect the rest.";
            if (!HealthFullyVerified)
                return "Everything's backed up. A few files are size-checked rather than MD5-verified.";
            // All good: close the loop from "present" to "recoverable".
            return "Your backups are the original GOG installers in your backup folder - reinstall from them anytime, even without Grog.";
        }
    }

    /// <summary>The rail's health badge and library fraction re-read the rail width and the worst-case flag.</summary>
    internal void RaiseRailBadge()
    {
        OnPropertyChanged(nameof(HealthBadgeText));
        OnPropertyChanged(nameof(RailLibraryCount)); OnPropertyChanged(nameof(RailLibraryTip));
        OnPropertyChanged(nameof(RailLibrarySub));   // (UX 09-08 #6)
        OnPropertyChanged(nameof(RailLibraryExceptions));   // (Rail exceptions 09-09)
    }

    /// <summary>The Overview-facing readouts the 1 Hz dashboard pass raises (coverage table, byte rollup,
    /// hero card texts, extras segments).</summary>
    internal void RaiseDashboardReadouts()
    {
        OnPropertyChanged(nameof(GamesFilesText));
        OnPropertyChanged(nameof(GamesSizeText));
        OnPropertyChanged(nameof(GamesFilesDoneText)); OnPropertyChanged(nameof(GamesFilesTotalText));
        OnPropertyChanged(nameof(ExtrasFilesDoneText)); OnPropertyChanged(nameof(ExtrasFilesTotalText));
        OnPropertyChanged(nameof(GamesDataDoneText)); OnPropertyChanged(nameof(GamesDataTotalText));
        OnPropertyChanged(nameof(ExtrasDataDoneText)); OnPropertyChanged(nameof(ExtrasDataTotalText));
        OnPropertyChanged(nameof(CoverageDataHeader));
        OnPropertyChanged(nameof(TotalCovFilesDoneText)); OnPropertyChanged(nameof(TotalCovFilesTotalText));
        OnPropertyChanged(nameof(TotalCovDataDoneText)); OnPropertyChanged(nameof(TotalCovDataTotalText));
        OnPropertyChanged(nameof(GamesBytePercent)); OnPropertyChanged(nameof(ExtrasBytePercent));
        OnPropertyChanged(nameof(GamesBytePercentText)); OnPropertyChanged(nameof(ExtrasBytePercentText));
        OnPropertyChanged(nameof(ExtrasFilesText));
        OnPropertyChanged(nameof(ExtrasSizeText));
        OnPropertyChanged(nameof(TotalFilesText));
        OnPropertyChanged(nameof(TotalSizeStripText));
        RaiseByteRollup();
        OnPropertyChanged(nameof(TotalSummaryText));
        OnPropertyChanged(nameof(EtaText)); OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(DoneSizeText));
        OnPropertyChanged(nameof(LibraryDoneText)); OnPropertyChanged(nameof(LibraryRemainText));
        OnPropertyChanged(nameof(LibraryFilesText));
        OnPropertyChanged(nameof(LibraryDoneCol)); OnPropertyChanged(nameof(LibraryRemainCol));
        RebuildExtraSegments();   // legend tracks landed bytes at 1 Hz, but only diffs the bound list (UI-thread sweep 09-06)
    }
}

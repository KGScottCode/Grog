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

// Overview (S3.3): the download queue pane (pill, tail, sort, per-row cancel and drag), the queue readouts the
// rail and the Activity tab share, the activity log and the toast.
public sealed partial class OverviewViewModel
{
    public ObservableCollection<QueueRow> ActiveRows { get; } = new();
    public ObservableCollection<QueueRow> WaitingRows { get; } = new();

    // Head-of-queue pill: the first row that FITS (the active file when running, else the next up); null on an
    // empty queue and when nothing queued fits (owner 09-19: a won't-fit file is never the pill; the frame then
    // shows a placeholder and every red row sits under the divider). DownloadTail is everything after it, in queue order.
    [ObservableProperty] private QueueRow? _downloadPill;
    /// <summary>The rendered tail: QueueRows that fit, in queue order, then (while any row is flagged) the one
    /// <see cref="WontFitDividerRow"/>, then the won't-fit QueueRows in queue order when it is expanded. Typed
    /// object because it holds two row kinds; the view picks the template by type (owner 09-16).</summary>
    public Services.BulkObservableCollection<object> DownloadTail { get; } = new();
    public bool HasDownloadTail => DownloadTail.Count > 0;
    /// <summary>The single divider instance, so reconciling the tail never re-realizes it.</summary>
    public WontFitDividerRow WontFitDivider { get; } = new();
    [RelayCommand] private void ToggleWontFitDivider() { WontFitDivider.Expanded = !WontFitDivider.Expanded; RebuildQueue(); RoomLeftChanged(); }
    // Stable QueueRow instances keyed by (GogId, FileKey) so RebuildQueue updates the bound tail incrementally.
    // Never Clear(): a collection Reset forces the list control to re-realize every row on each progress tick.
    private readonly Dictionary<(long, string), QueueRow> _rowCache = new();
    /// <summary>(09-19) The rows are REUSED, so a fact they derive (hidden name, needs sign-in) is re-raised in place.</summary>
    internal void RefreshQueueRowFacts() { foreach (var r in _rowCache.Values.ToList()) r.Refresh(); DownloadPill?.Refresh(); }
    public bool DownloadPillIdle => DownloadPill is null;
    public string DownloadPillIdleText => DownloadQueueCount > 0 ? "Nothing in the queue fits" : "Queue files from your library to download";
    public string DownloadPillState => _root.DownloadPaused ? "PAUSED"
        : HasActive ? (DownloadPill?.Task.FinishingPhase is { } fp ? fp.ToUpperInvariant() : "DOWNLOADING")
        : _root.DownloadRunning && DownloadPill is not null ? "STARTING"   // run is on, first bytes not yet in (resolve round-trips)
        : DownloadPill is not null ? "UP NEXT" : "";
    partial void OnDownloadPillChanged(QueueRow? value)
    {
        OnPropertyChanged(nameof(DownloadPillIdle));
        OnPropertyChanged(nameof(DownloadPillState)); OnPropertyChanged(nameof(DownloadPillIdleText));
    }

    public bool HasActive => ActiveRows.Count > 0;
    public bool HasWaiting => WaitingRows.Count > 0;
    public bool QueueIsEmpty => ActiveRows.Count == 0 && WaitingRows.Count == 0;

    // Activity-section header counts (the little pills). Recomputed in RebuildQueue / SyncReorgRows.
    public int DownloadQueueCount => ActiveRows.Count + WaitingRows.Count;
    /// <summary>Queued files that fit where they are routed: what Resume would start. The persisted queue is the source.</summary>
    public int ResumableCount => _manifest?.Current.Downloads.FittingCount ?? 0;
    internal long DownloadQueueBytes => ActiveRows.Concat(WaitingRows)
        .Sum(r => r.RemainingBytes);
    public string DownloadCountText => $"{DownloadQueueCount} / {ByteFormat.Size(DownloadQueueBytes)}";
    public int MoveQueueCount => (_root.Storage.ReorgActive is null ? 0 : 1) + _root.Storage.ReorgWaiting.Count;
    private long MoveQueueBytes => (_root.Storage.ReorgActive?.SizeBytes ?? 0) + _root.Storage.ReorgWaiting.Sum(r => r.SizeBytes);
    public string MoveCountText => $"{MoveQueueCount} / {ByteFormat.Size(MoveQueueBytes)}";
    public bool ShowMoveCount => MoveQueueCount > 0;

    /// <summary>Queue has anything in it (active or waiting). Section controls key off this, NOT "actively
    /// transferring" -- otherwise Pause would make its own controls vanish, leaving no way to Resume.</summary>
    public bool HasQueuedWork => !QueueIsEmpty;
    /// <summary>Show Pause: there's queued work and it isn't already paused.</summary>
    /// <summary>Pause only while a run is MOVING. A run that finished with files still queued (won't-fit
    /// leftovers) is idle, not paused: it showed "Pause" over a stopped queue (owner-hit 09-17).</summary>
    public bool ShowPauseDownloadQueue => HasQueuedWork && _root.DownloadRunning && !_root.DownloadPaused;
    /// <summary>Show Resume: there's queued work and it's paused - and an account exists to serve it.
    /// With none connected, Resume is a lie; Connect Account takes its slot (owner 08-31).</summary>
    /// (09-19) PAUSED shows Resume AT ONCE. It waited for the run to unwind (DownloadRunning false), and the unwind
    /// flushes partial files: on a slow USB drive the slot sat EMPTY for seconds after the click, no Pause and no
    /// Resume. A Resume clicked during the unwind is safe: ResumeDownloads waits the unwind out before it starts.
    public bool ShowResumeDownloadQueue => HasQueuedWork && (!_root.DownloadRunning || _root.DownloadPaused) && !_root.QueueNeedsAccount;
    public bool ShowQueueConnectAccount => HasQueuedWork && (!_root.DownloadRunning || _root.DownloadPaused) && _root.QueueNeedsAccount;
    /// <summary>With registered accounts merely signed out the fix is a Log In; with none registered
    /// it is a full connect. One button, the honest verb (owner 08-31).</summary>
    /// <summary>The queue's pause/resume/connect button states as ONE set (S3.3).</summary>
    internal void RaiseDownloadControls()
    {
        OnPropertyChanged(nameof(ShowPauseDownloadQueue)); OnPropertyChanged(nameof(ShowResumeDownloadQueue));
        OnPropertyChanged(nameof(ShowQueueConnectAccount)); OnPropertyChanged(nameof(QueueConnectLabel));
    }
    public string QueueConnectLabel => (_manifest?.Current.Accounts.Count ?? 0) > 0 ? "Log In" : "Connect Account";

    /// <summary>What's left, in the terms the user chose to back up.</summary>
    public string QueueStatsText
    {
        get
        {
            var all = ActiveRows.Concat(WaitingRows).ToList();
            if (all.Count == 0) return "0 files queued";
            int games = all.Count(r => r.Task.File.Kind != FileKind.Extra);
            int extras = all.Count - games;
            long bytes = QueuedRemainingBytes;
            var parts = new List<string>();
            if (games > 0) parts.Add($"{games} game file{(games == 1 ? "" : "s")}");
            if (extras > 0) parts.Add($"{extras} extra{(extras == 1 ? "" : "s")}");
            return $"{all.Count} queued · {string.Join(" / ", parts)} · {ByteFormat.Size(bytes)} to go";
        }
    }

    // ---- Sortable download queue (UP NEXT) ----------------------------------------------------------
    public enum QueueSortKey { Order, Name, Game, Kind, Size }
    // Shared scaffold: SortState.
    private readonly SortState<QueueSortKey> _queueSortState = new(QueueSortKey.Order);
    public QueueSortKey QueueSort => _queueSortState.Key;

    // Header arrows (up/down glyph on the active column, blank otherwise).
    public string QueueSortGlyphGame => SortGlyph(QueueSortKey.Game);
    public string QueueSortGlyphKind => SortGlyph(QueueSortKey.Kind);
    public string QueueSortGlyphSize => SortGlyph(QueueSortKey.Size);
    private string SortGlyph(QueueSortKey k) => _queueSortState.Glyph(k);
    // Which pill is the active sort (neutral "selected" emphasis; the amber arrow is separate).
    // Order has NO pill (owner 09-10: unclear what it meant, and it cost a row that wrapped at 1366). It is
    // still the resting key and still what a manual drag resets to -- MoveQueueRow does that -- so the way
    // back to hand-ordered is the drag itself, which is the gesture that earns it.
    public bool QueueSortIsGame => QueueSort == QueueSortKey.Game;
    public bool QueueSortIsKind => QueueSort == QueueSortKey.Kind;
    public bool QueueSortIsSize => QueueSort == QueueSortKey.Size;
    // Kind is two pills: installers-first is the ascending sort, extras-first the descending one.
    public bool QueueSortIsInstallers => QueueSort == QueueSortKey.Kind && _queueSortState.Asc;
    public bool QueueSortIsExtras => QueueSort == QueueSortKey.Kind && !_queueSortState.Asc;
    public string QueueSortGlyphInstallers => QueueSortIsInstallers ? QueueSortGlyphKind : "";
    public string QueueSortGlyphExtras => QueueSortIsExtras ? QueueSortGlyphKind : "";
    internal void RaiseQueueSortGlyphs()
    {
        OnPropertyChanged(nameof(QueueSortGlyphGame));
        OnPropertyChanged(nameof(QueueSortGlyphKind)); OnPropertyChanged(nameof(QueueSortGlyphSize));
        OnPropertyChanged(nameof(QueueSortIsGame));
        OnPropertyChanged(nameof(QueueSortIsKind)); OnPropertyChanged(nameof(QueueSortIsSize));
        OnPropertyChanged(nameof(QueueSortIsInstallers)); OnPropertyChanged(nameof(QueueSortIsExtras));
        OnPropertyChanged(nameof(QueueSortGlyphInstallers)); OnPropertyChanged(nameof(QueueSortGlyphExtras));
    }

    /// <summary>Click a queue column header: set or toggle the sort, then re-sequence the real persisted +
    /// live queue. Sticky until a manual drag reverts to explicit "Queue order".</summary>
    [RelayCommand]
    private void SortQueue(string column)
    {
        var k = column switch
        {
            "Name" => QueueSortKey.Name, "Game" => QueueSortKey.Game,
            "Kind" or "Extras" => QueueSortKey.Kind, "Size" => QueueSortKey.Size, _ => QueueSortKey.Order,
        };
        // "Queue order" is the resting/default state -- it has no meaningful reverse, so it never toggles.
        if (k == QueueSortKey.Order) _queueSortState.Reset(QueueSortKey.Order, asc: true);
        // The two Kind pills PIN a direction instead of toggling: pressing "Extras first" always means extras
        // first, never "the other one this time". That is the whole point of splitting them.
        else if (column == "Kind") _queueSortState.Reset(QueueSortKey.Kind, asc: true);
        else if (column == "Extras") _queueSortState.Reset(QueueSortKey.Kind, asc: false);
        else _queueSortState.Toggle(k);
        RaiseQueueSortGlyphs();
        Services.UiWatch.Time("ApplyQueueSort", () => ApplyQueueSortToModel(replan: true));   // explicit header click re-sequences the real queue AND re-checks fit
        RebuildQueue();
    }


    /// <summary>Apply the active sort to the persisted queue + live engine (pre-empting the active download if
    /// needed). Event-driven: called on header click or enqueue only -- a draining queue stays in order.</summary>
    /// <param name="replan">Re-check what fits for the new order. TRUE only for an explicit header click.
    /// At run start the plan was just made by Place over this very queue, so re-planning there is a full
    /// placement walk that can only produce the answer it already has (owner 09-10: queue membership changing
    /// is not a reason to re-plan; free space changing is).</param>
    internal void ApplyQueueSortToModel(bool replan = false)
    {
        if (_manifest is null || QueueSort == QueueSortKey.Order) return;
        var q = _manifest.Current.Downloads;
        var snap = q.Snapshot();
        if (snap.Count < 2) return;

        var itemById = _manifest.Current.Items.ToDictionary(i => i.GogId);
        GameFile? F(QueuedDownload x) => itemById.TryGetValue(x.GogId, out var it) ? it.Files.FirstOrDefault(f => f.FileKey == x.FileKey) : null;
        LibraryItem? L(QueuedDownload x) => itemById.TryGetValue(x.GogId, out var it) ? it : null;

        IEnumerable<QueuedDownload> ordered = QueueSort switch
        {
            QueueSortKey.Name => snap.OrderBy(x => F(x)?.Name ?? "", StringComparer.OrdinalIgnoreCase),
            QueueSortKey.Game => snap.OrderBy(x => L(x)?.Title ?? "", StringComparer.OrdinalIgnoreCase)
                                     .ThenBy(x => F(x)?.Name ?? "", StringComparer.OrdinalIgnoreCase),
            // Kind: only the BUCKET flips with direction; names stay A-Z inside each bucket. A whole-list
            // Reverse ran the names Z-A under "Extras first" (09-13; the pitfall the old Storage sort named).
            QueueSortKey.Kind => snap.OrderBy(x => (F(x)?.Kind == FileKind.Extra) == _queueSortState.Asc ? 1 : 0)
                                     .ThenBy(x => F(x)?.Name ?? "", StringComparer.OrdinalIgnoreCase),
            QueueSortKey.Size => snap.OrderBy(x => F(x)?.ExpectedSizeBytes ?? 0L),
            _ => snap,
        };
        var list = ordered.ToList();
        if (!_queueSortState.Asc && QueueSort != QueueSortKey.Kind) list.Reverse();
        // Deliberately NO special-casing of in-flight files (owner 09-02): a re-sort takes effect
        // immediately and preempts whatever it demotes - that is the documented contract of the queue.

        // Idempotent guard: already in this order -> nothing to do (no engine churn / preempt).
        bool same = list.Count == snap.Count;
        for (int i = 0; same && i < list.Count; i++)
            if (list[i].GogId != snap[i].GogId || list[i].FileKey != snap[i].FileKey) same = false;
        if (same) return;

        var order = list.Select(x => (x.GogId, x.FileKey)).ToList();
        Services.UiWatch.Time("ApplyQueueSort.SetOrder", () => q.SetOrder(order));
        Services.UiWatch.Time("ApplyQueueSort.EngineReorder", () => _liveEngine?.Reorder(order));
        // Placement is ORDER-DEPENDENT (the planner walks the queue and consumes free space greedily), so the
        // new order changes what fits. Recompute it over the whole queue, or the pane shows the order you
        // asked for on top of the previous order's fit decisions (owner 09-10).
        if (replan) ReplanAfterSort();
        _manifest?.SaveSoon();   // ordering only: debounced, coalesces with the drag that follows
    }

    /// <summary>Re-decide what fits for the order just applied. Whole-queue, so a file that was won't-fit can
    /// come back: the per-file re-plan during a run cannot do that (it only sees what the engine holds). Runs
    /// with or without a live engine (09-13): idle, the records are rewritten so the red rows match the order.</summary>
    private void ReplanAfterSort() => _ = ReplanQueueAsync("the new order");

    /// <summary>Re-check every queued row's target and fit now, run live or idle, and repaint every surface
    /// from the rewritten flags. <paramref name="why"/> names the trigger in the log ("the new order", "the
    /// pinned games"). (09-14) Shared with the Storage page's game pins.</summary>
    /// <returns>True when a plan ran over the newest order; false when it was skipped (logged). A request that
    /// lands while a plan is in flight is coalesced into ONE trailing run, and its caller awaits that run, so
    /// nobody paints a verdict, a glow or a scroll against a list the trailing run is about to regroup
    /// (review 09-16).</returns>
    internal Task<bool> ReplanQueueAsync(string why)
    {
        if (_manifest is null) return Task.FromResult(false);
        if (_replanLoop is not null)
        {
            _replanPending = why;
            _replanWaiters ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _replanWaiters.Task;
        }
        return _replanLoop = ReplanLoopAsync(why);
    }
    private Task<bool>? _replanLoop;

    /// <summary>(09-25) True while the re-plan publishes its result through the Storage refresh: that refresh must
    /// not ask for another re-plan.</summary>
    internal bool PublishingPlan { get; private set; }
    /// <summary>The newest plan's per-device leftover: the one "still free" figure an idle refresh shows.</summary>
    internal IReadOnlyList<Grog.Core.Volumes.DeviceSpace>? LastPlanRoom { get; private set; }
    private Services.UiCoalescer? _idleReplan;
    /// <summary>An idle refresh asks for the flags to be re-decided: off the UI thread, at most once a second.</summary>
    internal void RequestIdleReplan()
        => (_idleReplan ??= new Services.UiCoalescer(TimeSpan.FromSeconds(1), () => _ = ReplanQueueAsync("the drives as they are now"))).Request();
    private string? _replanPending;
    private TaskCompletionSource<bool>? _replanWaiters;

    private async Task<bool> ReplanLoopAsync(string why)
    {
        bool ok = false;
        try
        {
            do
            {
                _replanPending = null;
                var waiters = _replanWaiters; _replanWaiters = null;
                ok = await ReplanOnceAsync(why);
                waiters?.TrySetResult(ok);
                why = _replanPending ?? why;
            } while (_replanPending is not null);   // a failed pass still re-runs a pending newer order
            return ok;
        }
        finally { _replanLoop = null; _replanWaiters?.TrySetResult(ok); _replanWaiters = null; }
    }

    private async Task<bool> ReplanOnceAsync(string why)
    {
        var manifest = _manifest!; var engine = _liveEngine; var rootPath = _root.BackupRootPath;
        try
        {
            // Disk probes OFF the UI thread (a spun-down USB drive stalls DriveInfo for seconds); the mutation
            // back on it, so RecomputeActivity's observable writes stay on the dispatcher.
            // BackupLayout's constructor probes each root folder (Directory.Exists): off the UI thread too (09-25).
            var spaces = await Task.Run(() => new Grog.Core.Volumes.VolumeService(manifest, _session.PendingWrites)
                .DeviceSpaces(new Grog.Core.Volumes.BackupLayout(manifest.Current, rootPath)));
            // NOT under the gate: ReplanWholeQueue takes it itself, and must touch the engine without it
            // (engine events land in RunSettler, which takes the settler lock then the gate -- owner-hit 09-10).
            // OFF the UI thread (walk 09-25: 5 to 24 s here froze the sort click). The run's own mid-run re-plan
            // already runs this on a pool thread: it takes the gate itself and calls the engine without it.
            var result = await Task.Run(() => Grog.Core.Runs.BackupQueueBuilder.ReplanWholeQueueDetailed(manifest, spaces, engine));
            if (result.TotalMs >= 500)
                _root.Log($"Re-plan for {why} took {result.TotalMs} ms: {result.Timing}.", false, LogCategory.Download);
            if (result.Changed > 0)
            {
                _root.Log($"Re-checked what fits for {why}: {result.Changed} file(s) changed.", false, LogCategory.Download);
                Runtime.ManifestSaves.Default.SaveInBackground(manifest, "re-plan");   // the flags are persisted facts (09-17)
            }
            LastPlanRoom = result.FreeAfter;
            Services.UiWatch.Time("SetRoomLeft", () => SetRoomLeft(result.FreeAfter));   // the plan's own leftover: the one "still free" figure
            _root.RecomputeActivity();                 // red rows follow the rewritten flags
            PublishingPlan = true;
            try { _root.Storage.RefreshBackupLocations(); }   // banner and segment read the same flags
            finally { PublishingPlan = false; }
            return true;
        }
        // Decoration on top of a sort that already succeeded: a failed re-check must never lose the reorder.
        catch (Exception ex) { _root.Log($"Fit re-check skipped: {ex.Message}", false, LogCategory.Download); return false; }
    }

    /// <summary>The per-file ceiling that stops a red row, or null when it is simply out of room (09-19).</summary>
    private string? FileLimitOf(QueueRow r) => Grog.Core.Runs.QueueShaper.FileLimitBlocking(r.Task.File, WontFitDivider.RoomByRoot);

    /// <summary>Publish the room left per device to the divider and re-evaluate the row menus. "Fits" is a
    /// per-device test (the planner needs ONE drive with the room), so the divider's number and the menus' gate
    /// use the largest single-device leftover, never the sum across drives (review 09-16).</summary>
    internal void SetRoomLeft(IReadOnlyList<Grog.Core.Volumes.DeviceSpace> freeAfter)
    {
        WontFitDivider.SetRoom(freeAfter);
        foreach (var r in _rowCache.Values) if (r.WontFit) r.FileLimit = FileLimitOf(r);
        RoomLeftChanged();
    }

    /// <summary>Build the Downloading card from the PERSISTED queue, so it is identical on any screen and
    /// after restart. The live engine supplies byte progress; every other file renders from its manifest entry.</summary>
    internal void RebuildQueue() => Services.UiWatch.Time("RebuildQueue", RebuildQueueCore);
    private void RebuildQueueCore()
    {
        // (09-20) Split timings, logged only when the pass is slow: a 550 ms rebuild stalled the window on Pause and
        // the first guess at its cause (rows re-created at the edges of a run) was wrong. Measure, then fix.
        var sw = System.Diagnostics.Stopwatch.StartNew(); long tRows = 0, tGames = 0, tTail = 0, tReconcile = 0;
        // (09-25) The phase that ate the time moved around between identical passes: a garbage collection or a lock is
        // the suspect, so both are measured into the same line.
        var gcPause0 = GC.GetTotalPauseDuration(); int gc2_0 = GC.CollectionCount(2); long lockWaits0 = System.Threading.Monitor.LockContentionCount;
        // ActiveRows/WaitingRows aren't rendered (only counts + pill + tail source), so rebuilding is cheap.
        // The RENDERED list is DownloadTail -- reconciled in place below, never Clear()'d.
        ActiveRows.Clear(); WaitingRows.Clear();
        var seen = new HashSet<(long, string)>();
        if (_manifest is not null)
        {
            var live = new Dictionary<(long, string), DownloadTask>();
            if (_liveEngine is not null)
                foreach (var t in _liveEngine.Snapshot)
                    live[(t.File.GameGogId, t.File.FileKey)] = t;

            // Index items by id once (O(G)) so the queue-row lookup below is O(1) per queued file.
            var itemById = _manifest.Current.Items.ToDictionary(i => i.GogId);
            foreach (var q in _manifest.Current.Downloads.Snapshot())
            {
                itemById.TryGetValue(q.GogId, out var item);
                var file = item?.Files.FirstOrDefault(f => f.FileKey == q.FileKey);
                if (item is null || file is null) continue;
                var key = (q.GogId, q.FileKey);
                seen.Add(key);

                bool isActive = live.TryGetValue(key, out var lt) && lt!.State == DownloadTaskState.Active;
                // Live task when the engine runs (bytes/rate + a stable instance across rebuilds); otherwise
                // a display task derived from the manifest so the row renders with no engine.
                // Only a REAL partial counts as received. LocalSizeBytes survives a removed drive on purpose
                // (re-import matching), so seeding from it made a fresh queue read "0 of 790 files · 53.67 GB
                // of 542.63 GB" after a device delete (owner-hit 09-13).
                long received = file.HasPartial ? file.PartialBytes ?? 0 : 0;
                // Reuse the cached row -> the bound tail sees only real add/remove/move deltas, never a churn of
                // new objects. A live row is kept while it wraps the engine's same instance; a DISPLAY row (no
                // live task) is kept and its task refreshed in place (09-16: a new display task per rebuild made a
                // new row per rebuild, and the whole tail re-realized on every pass).
                _rowCache.TryGetValue(key, out var row);
                // The row is the FILE's; the task under it changes at the edges of a run (09-19: it was replaced
                // there, and the whole tail re-realized on every Pause and Resume).
                if (row is not null && lt is not null && !ReferenceEquals(row.Task, lt)) row.Rebind(lt, live: true);
                else if (row is not null && lt is null && row.IsLiveTask)
                    row.Rebind(new DownloadTask
                    {
                        File = file, GameTitle = item.Title, TargetRootId = q.TargetRootId,
                        State = DownloadTaskState.Pending, BytesTotal = file.PlanSizeBytes, BytesReceived = received,
                    }, live: false);
                if (row is null)
                {
                    DownloadTask task = lt ?? new DownloadTask
                    {
                        File = file, GameTitle = item.Title, TargetRootId = q.TargetRootId,
                        State = DownloadTaskState.Pending, BytesTotal = file.PlanSizeBytes, BytesReceived = received,
                    };
                    _rowCache[key] = row = new QueueRow(task, live: lt is not null);
                }
                else if (!row.IsLiveTask)
                {
                    row.Task.TargetRootId = q.TargetRootId; row.Task.BytesTotal = file.PlanSizeBytes; row.Task.BytesReceived = received;
                    row.Refresh();
                }
                row.WontFit = !q.Fits;   // the persisted entry is the one source: red row, in place (09-11)

                (isActive ? ActiveRows : WaitingRows).Add(row);
            }
        }
        // Drop cached rows no longer in the queue.
        foreach (var k in _rowCache.Keys.Where(k => !seen.Contains(k)).ToList())
            _rowCache.Remove(k);

        tRows = sw.ElapsedMilliseconds;
        // Per-game pass: the OS badge shows only when a game has queued files for MORE than one platform --
        // on a single-platform game it labels a fact with no alternative.
        foreach (var g in ActiveRows.Concat(WaitingRows).Where(r => !r.IsExtra)
                                    .GroupBy(r => r.Task.File.GameGogId))
        {
            bool spans = g.Select(r => r.OsBadge).Where(o => o.Length > 0).Distinct().Count() > 1;
            foreach (var r in g) r.GameSpansPlatforms = spans;
        }

        tGames = sw.ElapsedMilliseconds;
        // Pill = head of the queue; tail = the rest IN QUEUE ORDER, active or not (owner 09-02). Position is
        // priority and the engine preempts anything running outside the top-N window, so the pane shows the
        // one true order and an in-flight row is recognizable by its DOWNLOADING readout, not by being
        // hoisted - hoisting is exactly what read as "my file got reordered".
        var all = new List<QueueRow>();
        if (_manifest is not null)
            foreach (var q in _manifest.Current.Downloads.Snapshot())
                if (_rowCache.TryGetValue((q.GogId, q.FileKey), out var r)) all.Add(r);
        // The pill is "what is happening now": the first row that WILL move, or nothing. A won't-fit row is
        // never the pill, not even when every row is red (owner 09-19); it lives under the divider.
        DownloadPill = all.FirstOrDefault(r => !r.WontFit);
        // The DRAWN tail groups: fitting rows first (queue order), then the divider, then the red rows (queue
        // order) only while the divider is expanded. Grouping is display-only; `all` keeps the persisted order
        // and MoveQueueRow maps a drop back onto it (owner 09-16).
        var tailRows = all.Where(r => !ReferenceEquals(r, DownloadPill)).ToList();
        var desiredTail = new List<object>(tailRows.Count + 1);
        // Count over ALL rows: the pill is never red (09-19), so this equals the red rows under the divider.
        long redBytes = 0; int redCount = 0;
        foreach (var r in all)
            if (r.WontFit) { redCount++; redBytes += r.RemainingBytes; r.FileLimit = FileLimitOf(r); }   // same field as the Storage tally's unplaceable bytes
        foreach (var r in tailRows) if (!r.WontFit) desiredTail.Add(r);
        if (redCount > 0)
        {
            WontFitDivider.Count = redCount; WontFitDivider.Bytes = redBytes;
            desiredTail.Add(WontFitDivider);
            if (WontFitDivider.Expanded) foreach (var r in tailRows) if (r.WontFit) desiredTail.Add(r);
        }
        tTail = sw.ElapsedMilliseconds;
        ReconcileTail(desiredTail);
        tReconcile = sw.ElapsedMilliseconds;
        OnPropertyChanged(nameof(HasDownloadTail)); OnPropertyChanged(nameof(DownloadPillState)); OnPropertyChanged(nameof(DownloadPillIdleText));

        OnPropertyChanged(nameof(HasActive)); OnPropertyChanged(nameof(HasWaiting));
        OnPropertyChanged(nameof(QueueIsEmpty)); OnPropertyChanged(nameof(QueueStatsText));
        long tN0 = sw.ElapsedMilliseconds; RaiseRunStats(); long tRunStats = sw.ElapsedMilliseconds - tN0;
        // The headline's queued note and the Files & issues card carry the queue too (owner 08-31).
        // retally:false -- the queue rebuild changes no file state; the tally (a full library walk) is redone by
        // the dashboard pass that follows every settle and every manifest refresh (UI-thread sweep 09-06 r2).
        OnPropertyChanged(nameof(LedeQueuedNote));
        long tN1 = sw.ElapsedMilliseconds; RebuildHealthChecks(retally: false); long tChecks = sw.ElapsedMilliseconds - tN1;
        OnPropertyChanged(nameof(ActivityTabHeader));
        OnPropertyChanged(nameof(HasQueuedWork));
        OnPropertyChanged(nameof(DownloadQueueCount)); OnPropertyChanged(nameof(DownloadCountText));
        long tN2 = sw.ElapsedMilliseconds;
        _root.RaiseBarState();   // "Resume · N files" vs "Back up" follows the queue count (owner 09-22)
        _root.RaiseDownloadControls();
        long tBar = sw.ElapsedMilliseconds - tN2;
        if (sw.ElapsedMilliseconds >= 250)
            _root.Log($"RebuildQueue {sw.ElapsedMilliseconds} ms on {_rowCache.Count} rows: rows {tRows}, per-game {tGames - tRows}, tail {tTail - tGames}, reconcile {tReconcile - tTail}, notifications {sw.ElapsedMilliseconds - tReconcile} (run stats {tRunStats}, checks {tChecks}, bar {tBar}); "
                + $"GC pause {(GC.GetTotalPauseDuration() - gcPause0).TotalMilliseconds:F0} ms, gen2 {GC.CollectionCount(2) - gc2_0}, lock waits {System.Threading.Monitor.LockContentionCount - lockWaits0}.", false, LogCategory.Download);
    }

    // A subtle dot on the Activity tab whenever something is downloading or moving in the background.
    public string ActivityTabHeader => (_root.Storage.IsReorgRunning || HasActive || _root.Storage.MoveWaitingForSlot) ? "Activity  ●" : "Activity";

    internal void RaiseActivityTabHeader() => OnPropertyChanged(nameof(ActivityTabHeader));
    /// <summary>The Move queue's count readouts on the Activity tab, raised by the storage VM's pane.</summary>
    internal void RaiseMoveCount()
    {
        OnPropertyChanged(nameof(MoveQueueCount)); OnPropertyChanged(nameof(MoveCountText));
        OnPropertyChanged(nameof(ShowMoveCount));
    }

    /// <summary>The Primary-first pill hides without a secondary (1117); a sort left on it would show no pill
    /// selected and no glyph. Back to queue order, the resting state.</summary>

    public ObservableCollection<LogEntry> LogEntries { get; } = new();
    private const int MaxLogEntries = 500;
    public bool HasLogEntries => LogEntries.Count > 0;

    /// <summary>The shell's Log() lands each entry here (newest first, capped); failures also feed the
    /// issues modal's RECENT ERRORS list. UI thread only (the shell marshals).</summary>
    internal void AppendLog(LogEntry entry, bool isError)
    {
        LogEntries.Insert(0, entry);
        while (LogEntries.Count > MaxLogEntries) LogEntries.RemoveAt(LogEntries.Count - 1);
        OnPropertyChanged(nameof(HasLogEntries));
        if (isError)
        {
            ErrorLogEntries.Insert(0, entry);
            while (ErrorLogEntries.Count > MaxLogEntries) ErrorLogEntries.RemoveAt(ErrorLogEntries.Count - 1);
            OnPropertyChanged(nameof(HasErrorEntries));
        }
    }

    [RelayCommand] private void ClearActivityLog()
    {
        LogEntries.Clear(); ErrorLogEntries.Clear();
        OnPropertyChanged(nameof(HasLogEntries)); OnPropertyChanged(nameof(HasErrorEntries));
    }

    // ---- Transient toast. Surfaces when a run finishes, then auto-dismisses; the Health dot stays badged
    // until the issue is resolved. A toast carries a SEVERITY -- red is destructive/error only.
    [ObservableProperty] private bool _toastVisible;
    [ObservableProperty] private string _toastMessage = "";
    [ObservableProperty] private bool _toastIsError;
    /// <summary>Three-tier severity: 0 info (green), 1 warning (orange), 2 error (red) -- the same colors
    /// as the tray icon escalation. ToastIsError mirrors severity >= 2 for existing bindings.</summary>
    [ObservableProperty] private int _toastSeverity;
    /// <summary>Where the toast's Fix link goes ("Overview", "Accounts"...) -- the page that fixes the problem.</summary>
    private ToastFix _toastFix = "Overview";
    private int _toastGen;

    /// <summary>Accent for the toast's status dot, matching the tray icon tier exactly.</summary>
    public IBrush ToastAccent => ToastSeverity >= 2 ? Palette.ErrorRed
        : ToastSeverity == 1 ? Palette.WarnOrange : Palette.SuccessGreen;
    /// <summary>Border follows the severity too, but a success gets a plain surface stroke rather than a green
    /// ring: a good outcome should confirm quietly, not shout as loudly as a problem.</summary>
    public IBrush ToastStroke => ToastSeverity >= 2 ? Palette.ErrorRed
        : ToastSeverity == 1 ? Palette.WarnOrange : Palette.SurfaceStroke;
    /// <summary>"Fix" only makes sense when there IS something to fix; a success toast linking anywhere
    /// sends the user to look for a problem that does not exist.</summary>
    public bool ToastHasReview => ToastSeverity > 0;
    /// <summary>Sentinel fix target: the toast button reads "Retry" and starts the queue instead of navigating.</summary>
    public string ToastActionLabel => _toastFix.RetryQueue ? "Retry" : "Fix";
    /// <summary>Warnings and errors read larger: they exist to be noticed (readability law -- an alert in
    /// caption type is an alert designed to be missed).</summary>
    public double ToastFontSize => ToastSeverity > 0 ? 17 : 15;   // info at 12 was below the readability floor (owner, 09-04)
    /// <summary>Alerts get extra vertical body (roughly two text rows of air) so the card itself is
    /// bigger and harder to miss; info toasts keep their compact shape.</summary>
    public Avalonia.Thickness ToastPadding => ToastSeverity > 0 ? new Avalonia.Thickness(22, 34) : new Avalonia.Thickness(20, 20);
    public Avalonia.Media.FontWeight ToastFontWeight => ToastSeverity > 0 ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal;
    partial void OnToastSeverityChanged(int value)
    {
        OnPropertyChanged(nameof(ToastAccent));
        OnPropertyChanged(nameof(ToastStroke));
        OnPropertyChanged(nameof(ToastHasReview));
        OnPropertyChanged(nameof(ToastFontSize));
        OnPropertyChanged(nameof(ToastFontWeight));
        OnPropertyChanged(nameof(ToastPadding));
    }
    partial void OnToastIsErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(ToastAccent));
        OnPropertyChanged(nameof(ToastStroke));
        OnPropertyChanged(nameof(ToastHasReview));
    }

    internal void ShowToast(string message, int severity, ToastFix? fix = null)
    {
        void Set()
        {
            ToastMessage = message;
            ToastSeverity = severity;
            ToastIsError = severity >= 2;
            _toastFix = fix ?? "Overview";
            OnPropertyChanged(nameof(ToastActionLabel));
            ToastVisible = true;
            int gen = ++_toastGen;
            // Info toasts self-dismiss; warnings and errors STAY until the user acts (Fix / Close) so a
            // problem cannot flash by unread.
            if (severity == 0)
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(ToastSeverity > 0 ? 6500 : 10000);   // an info notice is read, not glanced at
                    Dispatcher.UIThread.Post(() => { if (gen == _toastGen) ToastVisible = false; });
                });
            }
        }
        if (Dispatcher.UIThread.CheckAccess()) Set(); else Dispatcher.UIThread.Post(Set);
    }

    [RelayCommand] private void DismissToast() { _toastGen++; ToastVisible = false; }
    [RelayCommand] private void OpenHealthFromToast()
    {
        _toastGen++; ToastVisible = false;
        _root.Navigate(_toastFix.View);
        if (_toastFix.RetryQueue) RetryRunFailureCommand.Execute(null);   // retries the failed files, not just Resume over whatever is queued (QA 09-30 C1)
    }

    /// <summary>The pill's eyebrow (STARTING / UP NEXT / PAUSED) follows the run flags the shell owns.</summary>
    internal void RaisePillState() => OnPropertyChanged(nameof(DownloadPillState));
    /// <summary>The queue's "N queued · X to go" line, per progress tick.</summary>
    internal void RaiseQueueStats() => OnPropertyChanged(nameof(QueueStatsText));
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using Grog.Core.Download;
using Grog.Core.Runs;

namespace Grog.App.ViewModels;

// The window's side of a BackupRun. The sequence (select -> place -> engine -> settle -> journal -> outcome)
// is Core's and is the same one the CLI runs; this host only shows it: the live engine for the queue tab,
// the coalesced progress feed, the per-file tallies behind the pill and footer, the log lines.
public partial class MainWindowViewModel
{
    /// <summary>One instance per run. Holds the run's transition memory so the App's own progress pass
    /// (meter, row readouts) fires each transition exactly once, the way it always has.</summary>
    private sealed class AppBackupHost : NullBackupHost
    {
        private readonly MainWindowViewModel _vm;
        private readonly bool _activityStrip;
        private readonly Dictionary<Guid, DownloadTaskState> _lastState = new();
        // Per-settle dashboard work, coalesced: a size-sorted queue settles several tiny files a second, and a
        // full Health + Dashboard + storage-row pass per file made the window sluggish (owner-hit 09-04). The
        // trailing edge guarantees the last file's state renders; the tallies and log line stay per file.
        private readonly Services.UiCoalescer _settledUi;
        private readonly Services.UiCoalescer _inventoryUi;

        /// <param name="activityStrip">The Back-up button's run shows the "Backing up your games" strip; the
        /// per-file executor deliberately does not (it is a background action, not a modal job).</param>
        public AppBackupHost(MainWindowViewModel vm, bool activityStrip)
        {
            _vm = vm; _activityStrip = activityStrip;
            // One second, trailing-only: the progress tick already runs RaiseDashboard at 1 Hz (Activity.cs
            // _lastDriveProbe gate) and the 250 ms leading edge here ran a second full pass alongside it on
            // every landed file (UI-thread sweep 09-06 r2). The trailing edge still renders the last file.
            _settledUi = new Services.UiCoalescer(TimeSpan.FromMilliseconds(1000), () =>
            {
                if (_vm._stopRequested) return;
                _vm.RaiseDashboard();   // includes RaiseHealth and the storage rows: folder bars + free-GB figures move with every landed file
                if (_vm.CurrentView == View.Drives) _inventoryUi?.Request();   // assigned just below; the lambda cannot run before the ctor returns
            }, trailingOnly: true);
            // The Storage grids replace every row when rebuilt and Avalonia then re-lays hundreds of rows out; per
            // landed file that was a 0.7-0.9 s UI stall every second or two on a small-file burst (grog.log
            // 16:03, 09-05). Two seconds between rebuilds keeps the page live without freezing it.
            // Trailing-only: the first landed file of a run paid the full rebuild on the leading edge, which
            // is the same stall the window exists to avoid (review 09-06).
            _inventoryUi = new Services.UiCoalescer(TimeSpan.FromMilliseconds(2000), () =>
            {
                if (_vm._stopRequested || _vm.CurrentView != View.Drives) return;
                _vm.Storage.RebuildInventory();
            }, trailingOnly: true);
        }

        public override void Placed(IReadOnlyList<DownloadTask> tasks, int held, long heldBytes)
        {
            _vm._heldNoSpaceCount = held; _vm._heldNoSpaceBytes = heldBytes;
            var line = _vm.SpillLine(tasks);
            // LOG ONLY, no toast (owner 09-10): anyone who added a second storage does not need to be told
            // its backups go there. The log line stays -- it is what names the split when reading a run back.
            if (line is not null) Dispatcher.UIThread.Post(() => _vm.Log(line.Replace('\n', ' '), false, LogCategory.Download));
        }

        public override void EngineReady(DownloadEngine engine)
        {
            // The queue tab reads and reorders THIS engine, so it has to be reachable while the job runs.
            _vm._liveEngine = engine;
            if (_activityStrip) _vm.DrainEngineReady(engine);   // the Back-up run only: the per-file executor has no drain
            engine.QueueChanged += () => Dispatcher.UIThread.Post(_vm.RequestQueueRebuild);
            engine.OldVersionArchived += _vm.Storage.OnOldVersionArchived;
            engine.RootWentAway += rid => Dispatcher.UIThread.Post(() => _vm.Storage.OnDriveWentAway(rid));   // (09-25) the card
            // Event-driven sort: apply the active queue sort now that the files are in, not on every tick,
            // then let Auto size the worker pool from the (sorted) queue head.
            if (_vm.Overview.QueueSort != OverviewViewModel.QueueSortKey.Order) _vm.Overview.ApplyQueueSortToModel();
            _vm.Settings.ApplyAutoConcurrency(engine, force: true);

            // Coalesced to 250ms (owner call 09-01): per-event full passes made 600-tiny-file runs
            // quadratic-ish; the trailing edge guarantees the burst's LAST state always renders.
            DownloadTask? latest = null;
            var progressUi = new Services.UiCoalescer(TimeSpan.FromMilliseconds(250), () =>
            {
                // A trailing-edge callback can land AFTER Clear tore the run down: never let a dead engine
                // repopulate the readouts it just reset.
                if (_vm.DownloadPaused || latest is null || _vm._stopRequested || !ReferenceEquals(_vm._liveEngine, engine)) return;
                var t = latest;
                _vm.OnDownloadProgress(engine, t, _lastState);
                if (!_activityStrip) return;
                // Both figures per tick: the mid-run re-plan and the run tail add tasks after EngineReady ("53 of 50", QA 09-30 A7).
                var snapNow = engine.Snapshot;
                int doneNow = snapNow.Count(x => x.State == DownloadTaskState.Completed);
                int total = snapNow.Count();
                _vm.UpdateActivity(doneNow, total, $"{doneNow} of {total} files · {t.GameTitle}");
            });
            engine.TaskChanged += t => { latest = t; Dispatcher.UIThread.Post(progressUi.Request); };

            Dispatcher.UIThread.Post(() =>
            {
                // (09-26) Files clicked while this run was starting join it now.
                if (_vm._joinLiveOnReady.Count > 0 && ReferenceEquals(_vm._liveEngine, engine))
                {
                    // HandToLiveEngine may re-add to the list: drain a copy so the loop never enumerates a mutating list.
                    var pending = _vm._joinLiveOnReady.ToList();
                    _vm._joinLiveOnReady.Clear();
                    foreach (var r in pending) _vm.HandToLiveEngine(engine, r);
                }
                _vm.RecomputeActivity();   // derive the queued state onto every surface
                if (_activityStrip) _vm.BeginActivity("Backing up your games", indeterminate: false);
            });
        }

        /// <summary>Core settled the record (strikes, Unavailable, partial flags, queue entry). The run tallies
        /// that drive the pill and footer, the per-file dashboard refresh and the log line are this view's.</summary>
        public override void TaskSettled(DownloadTask t, DownloadSettlement.Outcome outcome)
        {
            // A stopped task settles nothing, and Clear cancels every pending task at once: 400 of them must not
            // become 400 full dashboard passes on an app that already tore the run down.
            if (outcome == DownloadSettlement.Outcome.NotFinished || _vm._stopRequested) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_vm._stopRequested) return;   // Clear landed between the post and the run of it
                switch (outcome)
                {
                    case DownloadSettlement.Outcome.Completed:
                        _vm._runFailedKeys.Remove((t.File.GameGogId, t.File.FileKey));
                        _vm._runFilesDone++;   // one more file finished this run (drives the pill's Files - Done)
                        _vm._runBytesSettled += Math.Max(t.BytesTotal ?? 0, t.BytesReceived);   // learned size into the footer
                        _vm._settledTaskIds.Add(t.Id);   // the meter stops counting it as unsettled in the same breath
                        break;
                    case DownloadSettlement.Outcome.GaveUp:
                        _vm._runFailedKeys.Add((t.File.GameGogId, t.File.FileKey));
                        break;
                }
                // Health tally AND dashboard as files land (not per run): the Overview's coverage rows, legend
                // buckets and GB figures climb during the run, coalesced so a burst of small files is one pass.
                // No MarkLastRunDirty per settle (review 09-06): every reader of the journal wants a FINISHED run
                // (the card ignores "running", the failure lede reads the run-end mark), and dirtying here re-read
                // three journal files from the config dir every 5 s for the whole run.
                // (09-26) The row re-derives HERE, after Core wrote the record. The Completed event's refresh
                // (Activity.cs) usually runs before the settler's write, and nothing followed it, so a game's last
                // file left the row at 2/3 Partial with the detail pane all checked (owner-hit 09-25).
                if (outcome is DownloadSettlement.Outcome.Completed or DownloadSettlement.Outcome.GaveUp
                    && _vm.Library._gameById.TryGetValue(t.File.GameGogId, out var row) && row.Item is not null)
                {
                    row.RowProgress = Grog.Core.Sync.Rollups.GameFilePercent(row.Item, _vm.Library.EffectiveScope);
                    row.RaiseCompletion();
                }
                _settledUi.Request();
                _vm.LogSettled(t, outcome);
            });
        }

        public override void Log(string message, bool isError = false)
            => Dispatcher.UIThread.Post(() => _vm.Log(message, isError, LogCategory.Download));

        /// <summary>Progress dressing for a scan pass; second arg is the account name ("name (i/n)"), "" when
        /// naming is noise. Set by the scan site before the run. (S2.2)</summary>
        public Action<Grog.Core.Sync.LibrarySyncService, string>? ScanDress { get; set; }

        /// <summary>LibraryScan calls this once per account (null account = legacy single session). The art hook,
        /// the per-account panel title and the site's own progress dressing all live here. (S2.2)</summary>
        public override void ConfigureScan(Grog.Core.Sync.LibrarySyncService svc, Grog.Core.Models.GrogAccount? account, int index, int count)
            => _vm.ConfigureScanPass(svc, account, index, count, ScanDress);
    }

    /// <summary>The window's side of a run that does not download (verify, import, layout move, cloud saves,
    /// fix): every line the run says lands in the Activity log under the run's category. (S2.2)</summary>
    internal sealed class AppRunHost : NullBackupHost
    {
        private readonly MainWindowViewModel _vm;
        private readonly LogCategory _category;
        public AppRunHost(MainWindowViewModel vm, LogCategory category) { _vm = vm; _category = category; }

        public Action<Grog.Core.Sync.LibrarySyncService, string>? ScanDress { get; set; }

        public override void ConfigureScan(Grog.Core.Sync.LibrarySyncService svc, Grog.Core.Models.GrogAccount? account, int index, int count)
            => _vm.ConfigureScanPass(svc, account, index, count, ScanDress);

        // The VM's Log already hops to the dispatcher when called off it.
        public override void Log(string message, bool isError = false) => _vm.Log(message, isError, _category);
    }

    /// <summary>One scan pass is being set up (LibraryScan -> host.ConfigureScan): fold art fetching in, name
    /// the pass on the scan panel when several accounts take turns (position counts ALL accounts, skipped
    /// included, so the counter never appears to jump), then let the site dress progress. (S2.2)</summary>
    private void ConfigureScanPass(Grog.Core.Sync.LibrarySyncService svc, Grog.Core.Models.GrogAccount? account, int index, int count,
                                   Action<Grog.Core.Sync.LibrarySyncService, string>? dress)
    {
        AttachArtHook(svc);
        string who = "";
        if (account is not null && index > 0)
        {
            bool several = count > 1;
            var name = string.IsNullOrEmpty(account.Username) ? (account.Id.Length == 0 ? "primary" : account.Id) : account.Username;
            var title = several ? $"SCANNING {name.ToUpperInvariant()} ({index}/{count})" : "SCANNING YOUR GOG LIBRARY";
            Dispatcher.UIThread.Post(() => Library.ScanPanelTitle = title);
            who = several ? $"{name} ({index}/{count})" : "";
        }
        dress?.Invoke(svc, who);
    }

    /// <summary>The one log line per finished file. GOG's extras are often named after the game, so
    /// "Venba - Venba: ..." stutters: say it once. (09-14) An installer named after its game read as
    /// "failed: Ascendant" with no hint it was a file: those carry the real filename, or the OS, instead.</summary>
    private void LogSettled(DownloadTask x, DownloadSettlement.Outcome outcome)
    {
        string who;
        if (!string.Equals(x.GameTitle, x.File.Name, StringComparison.OrdinalIgnoreCase)) who = $"{x.GameTitle} - {x.File.Name}";
        else if (!string.IsNullOrEmpty(x.File.ResolvedFileName)) who = $"{x.GameTitle} - {x.File.ResolvedFileName}";
        else if (!string.IsNullOrEmpty(x.File.Os)) who = $"{x.GameTitle} - {x.File.Os} installer";
        else who = x.GameTitle;
        // (09-19) These lines show in the Activity panel, so a hidden game is not named in them either. The
        // outcome, the timings and the account stay; while hiding is on, the log file carries the same line.
        if (QueueRow.IsHiddenGame(x.File.GameGogId)) who = QueueRow.HiddenLabel;
        switch (outcome)
        {
            case DownloadSettlement.Outcome.Completed:
                // With multiple accounts the log names the session that fetched it.
                if (x.DownloadedVia is { } viaId && _manifest is { } mfLog && mfLog.Current.Accounts.Count > 1)
                    Log($"{AccountPrefix(new[] { viaId })}downloaded: {who}", isError: false, category: LogCategory.Download);
                if (x.PhaseSummary is { } ph) Log($"{who}: {ph}", isError: false, category: LogCategory.Download);
                break;
            case DownloadSettlement.Outcome.Skipped:
                Log($"{AccountPrefix(x.File.OwnerIds)}skipped: {who} ({x.SkipReason ?? "no owner signed in"}). Sign that account in and back up again.",
                    isError: false, category: LogCategory.Download);
                break;
            case DownloadSettlement.Outcome.Unavailable:
                Log($"{who}: GOG won't provide this file to your account. {x.UnavailableReason}", isError: false, category: LogCategory.Download);
                break;
            case DownloadSettlement.Outcome.NoRoom:
                // Once per file per run: a file re-admitted by a re-plan and refused again said the same thing twice.
                if (NoteHeldOnce(x.File.GameGogId, x.File.FileKey))
                    Log($"held: {who} - {x.Error ?? "no room on the drive"}. Stays queued as won't fit.", isError: false, category: LogCategory.Download);
                break;
            case DownloadSettlement.Outcome.WillRetry:
            case DownloadSettlement.Outcome.GaveUp:
                Log($"{(x.DownloadedVia is { } fv ? AccountPrefix(new[] { fv }) : "")}failed: {who} ({x.Error ?? "download failed"})",
                    isError: true, category: LogCategory.Download);
                break;
        }
    }

    /// <summary>The one-time "what lands where" line when the plan spills past the primary; null when every
    /// task targets one device (the common case says nothing). Games are counted whole, as they are placed.</summary>
    private string? SpillLine(IReadOnlyList<DownloadTask> tasks)
    {
        if (_manifest is null) return null;
        var m = _manifest.Current;
        // Only a SPILL counts: a ByContent layout that routes extras to a secondary is the user's choice and
        // must not be announced on every run.
        bool spilled = tasks.Any(t => t.TargetRootId is { } rid
            && rid != m.Routing.ResolveRootId(t.File.GameGogId, t.File.Kind, m.PrimaryRootId!, m.ExtrasLayout));
        if (!spilled) return null;
        var byRoot = tasks.GroupBy(t => t.TargetRootId ?? m.PrimaryRootId ?? "")
                          .OrderByDescending(g => g.Key == m.PrimaryRootId)
                          .ToList();
        if (byRoot.Count < 2) return null;
        var parts = byRoot.Select(g =>
        {
            var root = m.Roots.FirstOrDefault(r => r.Id == g.Key);
            string name = g.Key == m.PrimaryRootId ? "Primary" : (string.IsNullOrWhiteSpace(root?.Label) ? "Secondary" : root!.Label);
            long bytes = g.Sum(t => t.File.ExpectedSizeBytes ?? 0);
            int games = g.Select(t => t.File.GameGogId).Distinct().Count();
            return $"{name}: {Grog.Core.Format.ByteFormat.Size(bytes)}, {games} game{(games == 1 ? "" : "s")}";
        });
        return "Backing up to more than one storage.\n" + string.Join("\n", parts);   // one device per line: the numbers must not wrap mid-item
    }

    /// <summary>Flush timing per device, kept for the life of the window so a Pause/Resume does not forget
    /// that the stick is slow.</summary>
    private readonly Grog.Core.Download.DeviceWriteMonitor _deviceMonitor = new();

    /// <summary>A BackupRun over this window's manifest, root and sessions.</summary>
    /// <remarks>Builds a BackupLayout (disk): callers on the dispatcher wrap this in Task.Run. Config dir
    /// only here; the backup-root legacy probe ran at bind (UI-thread sweep 09-06 r2).</remarks>
    private BackupRun NewBackupRun(AppBackupHost host)
        => new(_manifest, _api, _http, EngineSessions(),
               new Grog.Core.Volumes.BackupLayout(_manifest.Current, _backupRoot), _backupRoot,
               Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot).ConfigDir, host) { PendingWrites = Session.PendingWrites };

    /// <summary>The engine settings the user controls, as run options. Auto concurrency is applied by the
    /// host once the queue is in (it needs the sorted head); a fixed setting goes in here.</summary>
    private BackupRunOptions AppRunOptions(BackupRunOptions selection, string journalCommand)
        => selection with
        {
            Scan = ScanPolicy.Never,   // the App scans on its own path, with its own UI
            FixedConcurrency = Settings.Xfer.AutoConcurrency ? null : Settings.Xfer.MaxConcurrentDownloads,
            DeviceMonitor = Settings.Xfer.AutoConcurrency ? _deviceMonitor : null,   // Auto only: a typed count is the user's call
            BytesPerSecondLimit = Settings.EffectiveBytesPerSecondLimit,
            JournalCommand = journalCommand,
        };
}

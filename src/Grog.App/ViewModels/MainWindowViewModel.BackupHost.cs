// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using Grog.Core.Download;
using Grog.Core.Runs;

namespace Grog.App.ViewModels;

public partial class MainWindowViewModel
{
    private sealed class AppBackupHost : NullBackupHost
    {
        private readonly MainWindowViewModel _vm;
        private readonly bool _activityStrip;
        private readonly Dictionary<Guid, DownloadTaskState> _lastState = new();
        private readonly Services.UiCoalescer _settledUi;
        private readonly Services.UiCoalescer _inventoryUi;

        public AppBackupHost(MainWindowViewModel vm, bool activityStrip)
        {
            _vm = vm; _activityStrip = activityStrip;
            _settledUi = new Services.UiCoalescer(TimeSpan.FromMilliseconds(1000), () =>
            {
                if (_vm._stopRequested) return;
                _vm.RaiseDashboard();   // includes RaiseHealth and the storage rows: folder bars + free-GB figures move with every landed file
                if (_vm.CurrentView == View.Drives) _inventoryUi?.Request();   // assigned just below; the lambda cannot run before the ctor returns
            }, trailingOnly: true);
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
            if (line is not null) Dispatcher.UIThread.Post(() => _vm.Log(line.Replace('\n', ' '), false, LogCategory.Download));
        }

        public override void EngineReady(DownloadEngine engine)
        {
            // The queue tab reads and reorders THIS engine, so it has to be reachable while the job runs.
            _vm._liveEngine = engine;
            if (_activityStrip) _vm.DrainEngineReady(engine);   // the Back-up run only: the per-file executor has no drain
            engine.QueueChanged += () => Dispatcher.UIThread.Post(_vm.RequestQueueRebuild);
            engine.OldVersionArchived += _vm.Storage.OnOldVersionArchived;
            engine.RootWentAway += rid => Dispatcher.UIThread.Post(() => _vm.Storage.OnDriveWentAway(rid));   // the card
            if (_vm.Overview.QueueSort != OverviewViewModel.QueueSortKey.Order) _vm.Overview.ApplyQueueSortToModel();
            _vm.Settings.ApplyAutoConcurrency(engine, force: true);

            DownloadTask? latest = null;
            var progressUi = new Services.UiCoalescer(TimeSpan.FromMilliseconds(250), () =>
            {
                if (_vm.DownloadPaused || latest is null || _vm._stopRequested || !ReferenceEquals(_vm._liveEngine, engine)) return;
                var t = latest;
                _vm.OnDownloadProgress(engine, t, _lastState);
                if (!_activityStrip) return;
                // Both figures per tick: the mid-run re-plan and the run tail add tasks after EngineReady.
                var snapNow = engine.Snapshot;
                int doneNow = snapNow.Count(x => x.State == DownloadTaskState.Completed);
                int total = snapNow.Count();
                _vm.UpdateActivity(doneNow, total, $"{doneNow} of {total} files · {t.GameTitle}");
            });
            engine.TaskChanged += t => { latest = t; Dispatcher.UIThread.Post(progressUi.Request); };

            Dispatcher.UIThread.Post(() =>
            {
                if (_vm._joinLiveOnReady.Count > 0 && ReferenceEquals(_vm._liveEngine, engine))
                {
                    var pending = _vm._joinLiveOnReady.ToList();
                    _vm._joinLiveOnReady.Clear();
                    foreach (var r in pending) _vm.HandToLiveEngine(engine, r);
                }
                _vm.RecomputeActivity();   // derive the queued state onto every surface
                if (_activityStrip) _vm.BeginActivity("Backing up your games", indeterminate: false);
            });
        }

        public override void TaskSettled(DownloadTask t, DownloadSettlement.Outcome outcome)
        {
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

        public Action<Grog.Core.Sync.LibrarySyncService, string>? ScanDress { get; set; }

        public override void ConfigureScan(Grog.Core.Sync.LibrarySyncService svc, Grog.Core.Models.GrogAccount? account, int index, int count)
            => _vm.ConfigureScanPass(svc, account, index, count, ScanDress);
    }

    internal sealed class AppRunHost : NullBackupHost
    {
        private readonly MainWindowViewModel _vm;
        private readonly LogCategory _category;
        public AppRunHost(MainWindowViewModel vm, LogCategory category) { _vm = vm; _category = category; }

        public Action<Grog.Core.Sync.LibrarySyncService, string>? ScanDress { get; set; }

        public override void ConfigureScan(Grog.Core.Sync.LibrarySyncService svc, Grog.Core.Models.GrogAccount? account, int index, int count)
            => _vm.ConfigureScanPass(svc, account, index, count, ScanDress);

        public override void Log(string message, bool isError = false) => _vm.Log(message, isError, _category);
    }

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

    private void LogSettled(DownloadTask x, DownloadSettlement.Outcome outcome)
    {
        string who;
        if (!string.Equals(x.GameTitle, x.File.Name, StringComparison.OrdinalIgnoreCase)) who = $"{x.GameTitle} - {x.File.Name}";
        else if (!string.IsNullOrEmpty(x.File.ResolvedFileName)) who = $"{x.GameTitle} - {x.File.ResolvedFileName}";
        else if (!string.IsNullOrEmpty(x.File.Os)) who = $"{x.GameTitle} - {x.File.Os} installer";
        else who = x.GameTitle;
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

    private string? SpillLine(IReadOnlyList<DownloadTask> tasks)
    {
        if (_manifest is null) return null;
        var m = _manifest.Current;
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

    private readonly Grog.Core.Download.DeviceWriteMonitor _deviceMonitor = new();

    private BackupRun NewBackupRun(AppBackupHost host)
        => new(_manifest, _api, _http, EngineSessions(),
               new Grog.Core.Volumes.BackupLayout(_manifest.Current, _backupRoot), _backupRoot,
               Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot).ConfigDir, host) { PendingWrites = Session.PendingWrites };

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

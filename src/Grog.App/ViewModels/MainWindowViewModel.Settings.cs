// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Download;
using Grog.Core.Format;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Verify;

public partial class MainWindowViewModel
{
    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (Busy) return;
        var firstSync = Library.TotalCount == 0;
        var scoped = !firstSync && Library.SelectedRows.Count > 0;
        var ids = scoped ? Library.SelectedRows.Select(r => r.GogId).ToHashSet() : null;
        var verb = firstSync ? "Scanning your library"
                 : scoped ? $"Checking {ids!.Count} selected"
                 : "Rescanning GOG library";
        if (!await ConfirmManualRescanAsync()) return;   // manual scan: ask about standing flags
        Library.NoteScanStarting();
        await RunBusy($"{verb}…", async () =>
        {
            Dispatcher.UIThread.Post(() => { Library.BeginCatalog("Contacting GOG…"); if (firstSync) Library.BeginPreview(); });
            // "Current Task: Scanning Library" already labels this -- the detail is just the count (no verb).
            Grog.Core.Sync.SyncResult syncResult;
            if (ids is { Count: > 0 })
            {
                // Scoped "check selected": one account's view is fine (no sweep runs on a scoped pass).
                var scopedHost = new AppRunHost(this, LogCategory.Scan)
                {
                    ScanDress = (svc, _) =>
                    {
                        svc.Progress += p => Dispatcher.UIThread.Post(() =>
                        {
                            Library.UpdateCatalog(p.Completed, p.Total,
                                p.Total > 0 ? $"{p.Completed}/{p.Total} products" : "Contacting GOG…");
                            Library.FlushPreview();   // batched: the progress tick is the natural flush point
                        });
                        svc.Discovered += Library.OnDiscovered;
                    },
                };
                syncResult = await Grog.Core.Runs.LibraryScan.RunScopedAsync(_manifest, _api, ids, scopedHost, _cts?.Token ?? default);
            }
            else syncResult = await RunFullScanAsync((svc, who) =>
            {
                svc.Progress += p => Dispatcher.UIThread.Post(() =>
                {
                    Library.UpdateCatalog(p.Completed, p.Total,
                        p.Total > 0
                            ? (who.Length > 0 ? $"{who}: {p.Completed}/{p.Total} products" : $"{p.Completed}/{p.Total} products")
                            : "Contacting GOG…");
                    Library.FlushPreview();   // batched: the progress tick is the natural flush point
                });
                svc.Discovered += Library.OnDiscovered;
            }, _cts?.Token ?? default);
            Library._lastProductsSeen = syncResult.GamesSeen;
            _settings.LastProductsSeen = syncResult.GamesSeen;
            _settings.SaveSoon();
            var summary = new Grog.Core.Sync.ChangesService(_manifest).OneLineSummary();
            Dispatcher.UIThread.Post(() =>
            {
                Library.RebuildRows();
                Library.RaiseComposition();
                Library.NoteScanResult(syncResult);
                Library.EndCatalog(completed: ids is { Count: > 0 } || Library.LastScanStoredAnything);
                Log(Library.PendingTaskCount > 0 ? summary : "");
            });
        });
        await Storage.RunPendingImportsAsync(interactive: true);
    }

    // --- per-file download queue (detail pane ↓ buttons) ---
    private readonly System.Collections.Generic.Queue<DetailFileRow> _fileQueue = new();
    private bool _fileQueueRunning;
    private int _fileQueueDone, _fileQueueTotal;

    private string AccountPrefix(System.Collections.Generic.IEnumerable<string> ids)
    {
        if (_manifest is null || _manifest.Current.Accounts.Count <= 1) return "";
        var names = ids.Select(AccountDisplayName).Where(n => n.Length > 0).Distinct().ToList();
        if (names.Count == 0) return "";
        return names.Count == 1 ? $"account: {names[0]} - " : $"accounts: {string.Join(", ", names)} - ";
    }

    internal string AccountDisplayName(string id)
    {
        var a = _manifest?.Current.Accounts.FirstOrDefault(x => x.Id == id);
        if (a is null) return id.Length == 0 ? "primary" : id;
        return string.IsNullOrEmpty(a.Username) ? (a.Id.Length == 0 ? "primary" : a.Id) : a.Username;
    }

    [RelayCommand]
    private void DownloadFile(DetailFileRow? row)
    {
        if (row is null || _manifest is null) return;
        if (!RequireAccount("download this file")) return;
        if (Grog.Core.Sync.BackupScope.IsPresent(row.State) || row.IsDownloading || row.IsQueued) return;
        // Enqueue is an Action: append to the PERSISTED queue (the source of truth), then run the executor.
        if (_manifest.Read(m => m.Downloads.Enqueue(row.GogId, row.File.FileKey)))   // 
        {
            SaveInBackground("queue file");
            if (_liveEngine is { } live) HandToLiveEngine(live, row);
            else if (DownloadRunning) _joinLiveOnReady.Add(row);   // a run is starting: EngineReady hands it over
            else { _fileQueue.Enqueue(row); _fileQueueTotal++; }
            Log($"{AccountPrefix(row.File.OwnerIds)}queued: {row.Name} ({_fileQueueDone}/{_fileQueueTotal} files)");
            RecomputeActivity();
            RefreshStorageIfIdle();
        }
        if (_liveEngine is null && !_fileQueueRunning && !DownloadPaused) _ = RunFileQueue();

        if (Guide.GuideActive && Guide.GuideFilePick is { } gp && gp.GogId == row.GogId && gp.FileKey == row.File.FileKey)
        {
            Guide.DetailKeep = Library.Games.FirstOrDefault(g => g.GogId == row.GogId);
            Library.SelectedRow = Guide.DetailKeep ?? Library.SelectedRow;
        }
    }

    [RelayCommand]
    private void QueueRemainingFiltered(InventoryGroupRow? group) => QueueRemainingForGame(group, toTop: false, filtered: true);

    [RelayCommand]
    private void QueueRemainingAll(InventoryGroupRow? group) => QueueRemainingForGame(group, toTop: false, filtered: false);

    [RelayCommand]
    private void MoveRemainingToTop(InventoryGroupRow? group) => QueueRemainingForGame(group, toTop: true, filtered: true);

    private void QueueRemainingForGame(InventoryGroupRow? group, bool toTop, bool filtered)
    {
        if (group is null || _manifest is null) return;
        var item = Library.ItemById(group.GogId);
        if (item is null) return;
        QueueRemainingForItem(item, toTop, filtered);
    }

    internal void QueueRemainingForItem(LibraryItem item, bool toTop, bool filtered, Grog.Core.Sync.Scope? scope = null, bool refresh = true)
    {
        if (_manifest is null) return;

        var sc = scope ?? Library.EffectiveScope;
        var remaining = item.Files.Where(f =>
            sc.Includes(f) &&
            (!filtered || Library._allContentSelected || Library._selectedContent.Contains(GameRowViewModel.ContentKey(f, item.Type))) &&
            !Grog.Core.Sync.BackupScope.IsPresent(f)).ToList();
        if (remaining.Count == 0) return;

        int slot = 0;
        var newRows = new List<DetailFileRow>();
        using (_manifest.Gate.Enter())   // 
            foreach (var f in remaining)
            {
                if (!_manifest.Current.Downloads.Contains(item.GogId, f.FileKey))
                {
                    _manifest.Current.Downloads.Enqueue(item.GogId, f.FileKey);
                    newRows.Add(new DetailFileRow(f) { GogId = item.GogId });
                }
                if (toTop) _manifest.Current.Downloads.MoveTo(item.GogId, f.FileKey, slot++);
            }
        if (refresh) SaveInBackground("queue game");

        if (_liveEngine is not null)
        {
            var have = _liveEngine.Snapshot.Select(t => (t.File.GameGogId, t.File.FileKey)).ToHashSet();
            var newTasks = newRows
                .Where(r => have.Add((r.GogId, r.File.FileKey)))
                .Select(r => new DownloadTask { File = r.File, GameTitle = item.Title, GameSlug = item.Slug, BytesReceived = r.File.PartialBytes ?? 0 })
                .ToList();
            if (newTasks.Count > 0) _liveEngine.EnqueueRange(newTasks);
            if (refresh) SyncLiveEngineToQueue(toTop);   // in a per-game loop, once after it (AfterQueueEdits)
        }
        else if (!DownloadPaused)
        {

            foreach (var r in newRows) _fileQueue.Enqueue(r);
            _fileQueueTotal += newRows.Count;
            if (!_fileQueueRunning) _ = RunFileQueue();
        }

        Log($"{(toTop ? "Moved" : "Queued")} {remaining.Count} remaining file(s) for {item.Title}{(toTop ? " to the top of the download queue" : " for download")}.", category: LogCategory.Download);
        if (!refresh) return;
        RecomputeActivity();
        RefreshStorageIfIdle();
    }

    internal readonly List<DetailFileRow> _joinLiveOnReady = new();

    internal void HandToLiveEngine(DownloadEngine live, DetailFileRow row)
    {
        if (!DownloadRunning || !ReferenceEquals(_liveEngine, live)) { _joinLiveOnReady.Add(row); return; }
        if (!live.Snapshot.Any(t => t.File.GameGogId == row.GogId && t.File.FileKey == row.File.FileKey))
        {
            var item = Library.ItemById(row.GogId);
            live.EnqueueRange(new List<DownloadTask> { new DownloadTask { File = row.File, GameTitle = item?.Title ?? row.Name,
                GameSlug = item?.Slug ?? "", BytesReceived = row.File.PartialBytes ?? 0 } });
        }
        SyncLiveEngineToQueue(false);
    }

    private void SyncLiveEngineToQueue(bool toTop)
    {
        if (_liveEngine is null || _manifest is null) return;
        if (toTop) _liveEngine.Reorder(_manifest.Current.Downloads.Snapshot().Select(q => (q.GogId, q.FileKey)).ToList());
        if (Settings.Xfer.AutoConcurrency) Settings.ApplyAutoConcurrency(_liveEngine, force: true);
        else _liveEngine.SetMaxConcurrent(Settings.Xfer.MaxConcurrentDownloads);
    }

    internal void AfterQueueEdits(bool toTop)
    {
        SaveInBackground("queue edits");
        if (_liveEngine is not null) SyncLiveEngineToQueue(toTop);
        RecomputeActivity();
        RefreshStorageIfIdle();
    }

    internal void RefreshStorageIfIdle()
    {
        if (DownloadPaused || _liveEngine is null) Storage.RefreshBackupLocations();
    }

    [RelayCommand]
    private void DownloadSection(string? which)
    {
        var rows = which == "extras" ? Library.DetailExtraFiles : Library.DetailGameFiles;
        foreach (var r in rows)
            if (!Grog.Core.Sync.BackupScope.IsPresent(r.State) && !r.IsDownloading && !r.IsQueued)
                DownloadFile(r);
    }

    private async Task RunFileQueue()
    {
        if (_liveEngine is not null || DownloadRunning) return;
        _fileQueueRunning = true;
        _stopRequested = false;
        try
        {
            await ScaffoldChosenFolderAsync();
            if (_stopRequested) return;   // Stop clicked during the scaffold
            SetDownloadPaused(false);   // a freshly-triggered download is active, not a leftover paused session
            ResetRunCounters();   // fresh throughput stats for this run
            DownloadRunning = true;
            _downloadCts?.Dispose();
            _downloadCts = new CancellationTokenSource();   // per-file path has no RunBusy _cts; this is what Pause/Stop cancel
            if (!await EnsureReadyToDownloadAsync())
            {
                while (_fileQueue.Count > 0) { _fileQueue.Dequeue(); }
                return;
            }

            while (_fileQueue.Count > 0 && !_stopRequested)
            {
                while (_fileQueue.Count > 0) { _fileQueue.Dequeue(); }
                var host = new AppBackupHost(this, activityStrip: false);
                var run = await Task.Run(() => NewBackupRun(host));
                var r = await run.RunAsync(
                    AppRunOptions(new Grog.Core.Runs.BackupRunOptions(FromPersistedQueue: true), "download"),
                    _downloadCts?.Token ?? default);
                if (r.Error is Grog.Core.Runs.RunAlreadyActiveException)
                {
                    Log("A download run is already active: the files joined its queue.", category: LogCategory.Download);   // backstop, not a failure
                    break;
                }
                if (r.Error is { } err)
                    Log($"Download failed: {Grog.Core.Api.GogError.Describe(err)}", isError: true, category: LogCategory.Download);
                // A full drive is the same answer here as on the Back-up button: say it, do not sit "queued".
                if (r.NothingFits)
                    Dispatcher.UIThread.Post(() => { RaiseNoSpaceText(); ShowNoSpaceAtAll = true; });
                else if (r.HeldNoSpace > 0)
                {
                    Log($"{r.HeldNoSpace} file{(r.HeldNoSpace == 1 ? "" : "s")} ({Grog.Core.Format.ByteFormat.Size(r.HeldNoSpaceBytes)}) "
                        + "had no room on any storage and stay queued.", category: LogCategory.Download);
                    OfferNoSpaceSummary(r.HeldNoSpace, r.HeldNoSpaceBytes);
                }

                if (!DownloadPaused)   // paused = freeze; Stop/normal-end reconciles + re-derives
                {
                    _liveEngine = null;
                    await ReconcileAndRefresh();
                    Dispatcher.UIThread.Post(RecomputeActivity);
                }
                if (DownloadPaused || _stopRequested) break;
                if (_fileQueue.Count == 0) TakeJoinedFilesIntoQueue();
            }

            if (!DownloadPaused)
            {
                DownloadSpeedBytes = 0;
                Overview.RaiseRunStats();
            }
        }
        finally
        {
            _fileQueueRunning = false;
            _fileQueueDone = 0; _fileQueueTotal = 0;
            DownloadRunning = false;
            if (!DownloadPaused && _manifest is not null && _manifest.Current.Downloads.Paused)
                SetDownloadPaused(false);   // single mutator owns the Paused fact (never poke it inline)
        }
    }

    internal async Task ReconcileAndRefresh()
    {
        var m = _manifest;
        if (m is null) return;
        await Task.Run(() =>
        {
            using (m.Gate.Enter())   // status rollup writes item.Status under the gate
                foreach (var item in m.Current.Items.ToArray())
                    LibrarySyncService.RecomputeStatus(item);
        });

        await m.SaveAsync();
        await Dispatcher.UIThread.InvokeAsync(RefreshFromManifest);
    }

    private async Task<bool> EnsureReadyToDownloadAsync()
    {
        try
        {
            if (await AnyAccountConnectedAsync()) return true;   // token probes off the dispatcher
            await Dispatcher.UIThread.InvokeAsync(async () => await _auth.EnsureAuthenticatedAsync());
            return true;
        }
        catch (OperationCanceledException)
        {
            Log("Sign-in needed to download. Login was canceled.");
            return false;
        }
        catch (Exception ex)
        {
            Log($"Couldn't sign in to GOG: {ex.Message}", isError: true);
            return false;
        }
    }

    internal Grog.Core.Runs.BackupRunOptions SelectionOptions(bool updatesOnly, HashSet<long>? onlyGameIds, bool fromQueue,
                                                              Grog.Core.Sync.Scope scope, bool allContent, bool promotePicked = false)
    {
        var sel = Library._selectedContent; bool movies = Library.ShowLegacyMovies;
        return new Grog.Core.Runs.BackupRunOptions(
            FromPersistedQueue: fromQueue, UpdatesOnly: updatesOnly, OnlyGameIds: onlyGameIds, ScopeOverride: scope,
            PromotePicked: promotePicked,   // the App's "Back up" joins the end; only "Download next" jumps the queue
            ViewFilter: (item, f) => (item.Type != ProductType.Movie || movies)
                                     && (allContent || sel.Contains(GameRowViewModel.ContentKey(f, item.Type))));
    }

    internal async Task DownloadFiltered(bool updatesOnly, HashSet<long>? onlyGameIds = null, bool fromQueue = false,
                                        bool? scopeGamesOverride = null, bool? scopeExtrasOverride = null,
                                        bool ignoreContentChips = false, bool promotePicked = false, bool unattended = false)
    {
        _downloadRefused = false; _downloadRefusedReason = null;
        if (Busy) return;
        if (_fileQueueRunning || DownloadRunning)
        {
            _downloadRefused = true; _downloadRefusedReason = "a download run is already active";
            Log("A download run is already active: add games from the Library (they join it), or try again when it ends.", category: LogCategory.Download);
            return;
        }
        if (!await EnsureReadyToDownloadAsync()) { _downloadRefused = true; _downloadRefusedReason = RefusedSignInNotReady; return; }
        if (unattended && DownloadPaused && Storage.PausedForDriveLabel is { } gone)
        { _downloadRefused = true; _downloadRefusedReason = $"{gone} is disconnected"; return; }
        if (unattended && DownloadPaused) Log("Scheduled backup: lifting the pause.", category: LogCategory.Download);
        if (!TryBeginActivitySlot("Can't start the download")) { _downloadRefused = true; _downloadRefusedReason = "a move is in progress"; return; }
        _stopRequested = false;
        SetDownloadPaused(false);   // a freshly-triggered / resumed backup is active, not a leftover paused session
        if (!fromQueue) ResetRunCounters();
        DownloadRunning = true;
        try
        {
        await RunBusy("Downloading…", async () =>
        {
            var selection = SelectionOptions(updatesOnly, onlyGameIds, fromQueue,
                Library.ScopeFor(scopeGamesOverride ?? Library.IncludeGames, scopeExtrasOverride ?? Library.IncludeExtras),
                allContent: Library._allContentSelected || ignoreContentChips, promotePicked: promotePicked);
            _heldNoSpaceCount = 0; _heldNoSpaceBytes = 0;   // Placed() fills them once the plan exists
            BackupRunBegan();
            var host = new AppBackupHost(this, activityStrip: true);
            var r = await NewBackupRun(host).RunAsync(AppRunOptions(selection, "backup"), _cts?.Token ?? default);
            _lastRunOutcome = r.Outcome;   // the run's own verdict (Partial counts a strike the App's GaveUp count does not;)

            if (r.NothingToDo)
            {
                Log("Nothing to download");
                if (fromQueue) Dispatcher.UIThread.Post(RecomputeActivity);   // Core cleared the drained queue
                return;
            }
            if (r.NothingFits)
            {
                Dispatcher.UIThread.Post(() => { RecomputeActivity(); RaiseNoSpaceText(); ShowNoSpaceAtAll = true; });
                return;
            }
            if (r.Error is Grog.Core.Runs.RunAlreadyActiveException)
            {
                _downloadRefused = true; _downloadRefusedReason = "a download run is already active";
                _liveEngine = null;
                Log("A download run is already active: this one did not start.", category: LogCategory.Download);
            }
            else if (r.Error is { } err)
            {
                _runErrored = true;
                _liveEngine = null;
                Log($"Backup failed: {Grog.Core.Api.GogError.Describe(err)}", isError: true, category: LogCategory.Download);
                Dispatcher.UIThread.Post(() =>
                {
                    _meter.MarkIdle(); DownloadSpeedBytes = 0;   // no stale rate under an errored run
                    EndActivity(); RecomputeActivity(); Overview.RaiseRunStats(); Storage.RefreshBackupLocations();
                });
                return;
            }

            if (Drain.Finishing)
            {
                bool anythingLeft = _manifest is not null && !_manifest.Current.Downloads.IsEmpty;
                if (anythingLeft)
                {
                    bool endAsPause = !DownloadPaused;
                    if (!endAsPause) Drain.Reset();
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (endAsPause)
                        {
                            Drain.EndedPaused();
                            SetDownloadPaused(true); _meter.MarkIdle(); DownloadSpeedBytes = 0;
                            Log("Finished the files in progress. Resume continues the queue.", category: LogCategory.Download);
                        }
                        EndActivity(); RecomputeActivity(); Overview.RaiseRunStats(); RaiseDashboard();
                    });
                    return;
                }
                Drain.Reset();
            }
            if (DownloadPaused)
            {
                Dispatcher.UIThread.Post(EndActivity);   // hide the modal activity strip; keep everything else frozen
                return;
            }

            _liveEngine = null;   // over before the reconcile: a file queued meanwhile waits for the next run instead
            await ReconcileAndRefresh();
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var row in Library.DetailGameFiles.Concat(Library.DetailExtraFiles)) row.RaiseStateChanged();
                Library.RaiseSectionDone();
                EndActivity();
            });
            Dispatcher.UIThread.Post(RecomputeActivity);
            if (_stopRequested) return;   // not r.Canceled: a drain ("Finish current files") also reports Canceled and is a finished run here
            int completed = r.Completed;
            int failedCount = r.NotLanded;
            var refusedTail = r.Refused > 0
                ? $" {r.Refused} file{(r.Refused == 1 ? "" : "s")} GOG won't provide to your account (not counted)."
                : "";
            if (r.Skipped > 0)
                refusedTail += $" {r.Skipped} file{(r.Skipped == 1 ? "" : "s")} skipped: owner signed out.";
            Log(failedCount > 0
                ? $"Backup finished: {completed} of {completed + failedCount} file(s) downloaded, {failedCount} failed.{refusedTail}"
                : $"Backup finished: {completed} file(s) downloaded.{refusedTail}", isError: failedCount > 0, category: LogCategory.Download);

            if (r.DriveOffline > 0)
                Log($"{r.DriveOffline} file{(r.DriveOffline == 1 ? "" : "s")} stopped because their storage was disconnected. They wait for it.",
                    category: LogCategory.Download);

            if (failedCount > 0)
            {
                if (Settings.NotifyOnErrors)
                    NotifyUser("Grog - backup finished with errors",
                        $"{failedCount} file{(failedCount == 1 ? "" : "s")} couldn't be backed up - open Grog to review.",
                        toastBody: $"{failedCount} file{(failedCount == 1 ? "" : "s")} couldn't be backed up.",
                        fixNav: ToastFix.Retry);   // the toast's action RETRIES the queue, it does not just navigate
            }
            else if (r.DriveOffline > 0)
            {
                if (Settings.NotifyOnErrors)
                    NotifyUser("Grog - waiting for storage",
                        $"{r.DriveOffline} file{(r.DriveOffline == 1 ? " is" : "s are")} waiting for a disconnected storage. Reconnect it to finish.",
                        isError: false, attention: TrayAttentionWarning);
            }
            else if (completed > 0 && Settings.NotifyOnComplete)
            {
                NotifyUser("Grog - queue complete",
                    $"{completed} file{(completed == 1 ? "" : "s")} backed up with no errors.", isError: false);
            }
            if (r.HeldNoSpace > 0)
            {
                Log($"{r.HeldNoSpace} file{(r.HeldNoSpace == 1 ? "" : "s")} ({Grog.Core.Format.ByteFormat.Size(r.HeldNoSpaceBytes)}) "
                    + "had no room on any storage and stay queued.", category: LogCategory.Download);
                OfferNoSpaceSummary(r.HeldNoSpace, r.HeldNoSpaceBytes);
            }
        });
        }
        finally
        {
            BackupRunOver();
            EndActivitySlot();
            Dispatcher.UIThread.Post(Storage.RefreshDriveWait);   // what still waits for a disconnected drive, now the run is over
            _meter.MarkIdle(); DownloadSpeedBytes = 0;
            DownloadRunning = false;

            if (!DownloadPaused && _manifest is not null && _manifest.Current.Downloads.Paused)
                SetDownloadPaused(false);   // single mutator owns the Paused fact (never poke it inline)

            if (!DownloadPaused && _liveEngine is null && _joinLiveOnReady.Count > 0)
            {
                TakeJoinedFilesIntoQueue();
                if (!_fileQueueRunning) _ = RunFileQueue();
            }
        }
    }

    private void TakeJoinedFilesIntoQueue()
    {
        if (_joinLiveOnReady.Count == 0) return;
        foreach (var r in _joinLiveOnReady) { _fileQueue.Enqueue(r); _fileQueueTotal++; }
        _joinLiveOnReady.Clear();
    }

    private async Task CacheArtForProductAsync(LibrarySyncService svc, long gogId, string dir, CancellationToken ct)
    {
        if (_api is null) return;
        var item = Library.ItemById(gogId);
        bool needMeta = item is null || item.AgeRatings.Count == 0;
        var art = await Services.ArtCache.CacheProductAsync(_api, gogId, dir, needMeta, ct);
        if (art is not null && item is null) svc.StashMeta(gogId, art.Languages, art.AgeRatings);
        if (art is not null && item is not null && _manifest is not null)
        {
            using (_manifest.Gate.Enter())   // 
            {
                if (art.Languages.Count > 0) item.Languages = art.Languages.ToList();
                if (art.AgeRatings.Count > 0) item.AgeRatings = new Dictionary<string, int>(art.AgeRatings);
            }
        }
    }

    private void AttachArtHook(LibrarySyncService svc)
    {
        var dir = Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot).ArtDir;   // art dir only; no backup-root probe
        Directory.CreateDirectory(dir);
        GameRowViewModel.ArtDir = dir;
        svc.FetchArtAsync = (id, ct) => CacheArtForProductAsync(svc, id, dir, ct);
    }

    internal Func<GameFile, CancellationToken, Task<string?>>? ServerChecksumFetcher()
    {
        if (_api is null || !IsConnected) return null;
        return async (f, ct) =>
        {
            try
            {
                var api = _api;
                if (EngineSessions() is { } s && s.FirstAuthenticatedOwner(f) is { } owner) api = s.ApiFor(owner);
                var resolved = await api.ResolveDownloadAsync(f.FileKey, ct, probe: false);
                return resolved.ChecksumXmlUrl is { } url ? await api.FetchMd5Async(url, ct) : null;
            }
            catch { return null; }   // offline / resolve failed → can't disambiguate; caller falls back to Corrupt
        };
    }

    private Services.UiCoalescer? _queueUi;
    private void RequestQueueRebuild()
        => (_queueUi ??= new Services.UiCoalescer(TimeSpan.FromMilliseconds(250), () => { Overview.RebuildQueue(); RequestSizing(); })).Request();

    private Grog.Core.Runs.QueueSizer? _sizer;
    private Grog.Core.Manifest.JsonManifestStore? _sizerManifest;
    internal void RequestSizing()
    {
        if (_manifest is null || !IsConnected) return;
        if (_sizer is null || !ReferenceEquals(_sizerManifest, _manifest))
        {
            _sizer?.Stop();
            var manifest = _manifest; var api = _api;
            _sizer = new Grog.Core.Runs.QueueSizer(manifest,
                async (fileKey, ct) => (await api.ResolveDownloadAsync(fileKey, ct, probe: true)).ContentLength,
                key => _liveEngine?.Snapshot.Any(t => t.State == DownloadTaskState.Active && t.File.GameGogId == key.GogId && t.File.FileKey == key.FileKey) == true,
                msg => Dispatcher.UIThread.Post(() => Log(msg, category: LogCategory.Download)));
            _sizer.SizesLearned += _ => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_manifest, manifest)) return;
                manifest.SaveSoon();
                Overview.RequestIdleReplan();   // the plan re-prices what just got a real size
                Storage.RefreshBackupLocations();
            });
            _sizerManifest = manifest;
        }
        _sizer.Request();
    }
    internal void StopSizing() => _sizer?.Stop();

    private HashSet<long> _prevActiveGames = new();

    [RelayCommand]
    internal void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        _ = Task.Run(() =>   // ShellExecute off the dispatcher
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* best-effort */ }
        });
    }

    [RelayCommand]
    private async Task ShowLocationPicker()
    {
        var seed = !string.IsNullOrEmpty(_backupRoot)
            ? _backupRoot
            : Grog.Core.Storage.BackupLocationSuggester.DefaultRoot();
        var picked = await PickFolderAsync(HasLocation ? "Change primary backup folder" : "Choose backup folder", seed);
        if (string.IsNullOrWhiteSpace(picked)) return;
        var full = System.IO.Path.GetFullPath(picked);
        var previous = _backupRoot;
        var primary = Storage.BackupLocations.FirstOrDefault(r => r.IsPrimary);
        if (HasLocation && primary is not null)
        {
            await Storage.RepointRootAsync(primary, full);
        }
        else
        {
            await Task.Run(() => System.IO.Directory.CreateDirectory(full));   // 
            await BindToRootAsync(full);
            _settings.ChosenRoot = Grog.Core.Storage.GrogPaths.StorePath(_backupRoot); _settings.SaveSoon();
        }
        await TidyAutoCreatedDefaultAsync(previous, full);
        await ScaffoldChosenFolderAsync();
        Storage.RefreshBackupLocations();
        RaiseState();
    }

    public void ScaffoldChosenFolder() => _ = ScaffoldChosenFolderAsync();   // folder creation off the UI thread; best-effort either way

    private Task ScaffoldChosenFolderAsync()
    {
        var m = _manifest; var root = _backupRoot;
        if (m is null || string.IsNullOrWhiteSpace(root)) return Task.CompletedTask;
        return Task.Run(() =>
        {
            try { new Grog.Core.Volumes.BackupLayout(m.Current, root).ScaffoldCategoryFolders(); }
            catch { /* best-effort: the download engine creates whatever it needs anyway */ }
        });
    }

    private async Task TidyAutoCreatedDefaultAsync(string previous, string chosen)
    {
        if (!_settings.DefaultRootAutoCreated) return;
        if (string.IsNullOrWhiteSpace(previous)) return;
        if (string.Equals(previous, chosen, StringComparison.OrdinalIgnoreCase)) return;   // nothing moved

        var def = Grog.Core.Storage.BackupLocationSuggester.DefaultRoot();
        if (!string.Equals(previous, System.IO.Path.GetFullPath(def), StringComparison.OrdinalIgnoreCase)) return;

        bool removed = await Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(previous) && !Directory.EnumerateFileSystemEntries(previous).Any())
                {
                    Directory.Delete(previous);
                    return true;
                }
            }
            catch { /* best-effort: a folder we cannot remove is harmless, and never worth an error */ }
            return false;
        });
        if (removed) Log($"Removed the empty default folder Grog created at {previous}.");
        _settings.DefaultRootAutoCreated = false;   // one chance only; never revisit a path we no longer own
        _settings.SaveSoon();
    }

    private (int Count, long Bytes)? _noSpaceShownFor;
    private void OfferNoSpaceSummary(int held, long heldBytes)
    {
        if (_noSpaceShownFor is { } prev && prev.Count == held && prev.Bytes == heldBytes) return;
        _noSpaceShownFor = (held, heldBytes);
        _heldNoSpaceCount = held; _heldNoSpaceBytes = heldBytes;
        Dispatcher.UIThread.Post(() => { RaiseNoSpaceText(); ShowNoSpaceSummary = true; });
    }
}

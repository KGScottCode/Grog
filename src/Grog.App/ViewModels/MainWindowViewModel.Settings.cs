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

// Run entry points and the bound settings surface: the catalog check, the per-file download queue, the bulk
// backup run, Fresh Library, theme, extras layout, the location picker. (Scope, art toggles and the item
// lookup moved to LibraryViewModel in S3.2.)
public partial class MainWindowViewModel
{
    /// <summary>"Check for updates": refresh the catalog and surface what changed; downloads nothing.</summary>
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
        if (!await ConfirmManualRescanAsync()) return;   // (New items 09-09) manual scan: ask about standing flags
        Library.NoteScanStarting();
        await RunBusy($"{verb}…", async () =>
        {
            Dispatcher.UIThread.Post(() => { Library.BeginCatalog("Contacting GOG…"); if (firstSync) Library.BeginPreview(); });
            // "Current Task: Scanning Library" already labels this -- the detail is just the count (no verb).
            Grog.Core.Sync.SyncResult syncResult;
            if (ids is { Count: > 0 })
            {
                // Scoped "check selected": one account's view is fine (no sweep runs on a scoped pass). (S2.2)
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
            // The changes walk is a manifest read the sync thread already owns: build the line here, not
            // inside the UI post (UI-thread sweep 09-06 r2). PendingTaskCount stays on the UI side below.
            var summary = new Grog.Core.Sync.ChangesService(_manifest).OneLineSummary();
            Dispatcher.UIThread.Post(() =>
            {
                Library.RebuildRows();
                Library.RaiseComposition();
                // Scan summary is written and offered here for user-initiated scans; never raised for the
                // unattended scheduled run, which would leave a modal on screen until morning.
                Library.NoteScanResult(syncResult);
                // Scoped passes don't touch _lastScanPasses; only the full-scan path consults it.
                Library.EndCatalog(completed: ids is { Count: > 0 } || Library.LastScanStoredAnything);
                // ON THE UI THREAD, AFTER RebuildRows: PendingTaskCount enumerates _allGames, and the
                // worker thread reading it while first-sync preview inserts / the rebuild mutate the
                // list is a real NRE (owner-hit 09-01, first scan after a fresh-library reset).
                Log(Library.PendingTaskCount > 0 ? summary : "");
            });
        });
        await Storage.RunPendingImportsAsync(interactive: true);
    }

    // --- per-file download queue (detail pane ↓ buttons) ---
    private readonly System.Collections.Generic.Queue<DetailFileRow> _fileQueue = new();
    private bool _fileQueueRunning;
    private int _fileQueueDone, _fileQueueTotal;

    /// <summary>"account: X - " (or "accounts: X, Y - ") log prefix for multi-account libraries; empty otherwise.
    /// Account-first so the log greps per account and reads who -> did what -> to what.</summary>
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
        if (_manifest.Read(m => m.Downloads.Enqueue(row.GogId, row.File.FileKey)))   // (manifest gate 09-08)
        {
            SaveInBackground("queue file");
            // (09-26) A run is live: hand the file to THAT engine. Starting the file-queue executor here built a
            // second engine over the same persisted queue, and both downloaded every pending file (duplicate
            // completions, "in use by another process" on the shared .part, false strikes). grog.log 09-25 23:50.
            if (_liveEngine is { } live) HandToLiveEngine(live, row);
            else if (DownloadRunning) _joinLiveOnReady.Add(row);   // a run is starting: EngineReady hands it over
            else { _fileQueue.Enqueue(row); _fileQueueTotal++; }
            // With multiple accounts the log names the file's owner(s) up front, so which session a
            // queue/download exercised is readable straight off the log.
            Log($"{AccountPrefix(row.File.OwnerIds)}queued: {row.Name} ({_fileQueueDone}/{_fileQueueTotal} files)");
            RecomputeActivity();
            RefreshStorageIfIdle();
        }
        // Adding a file is an Action, not a Resume: if the queue is paused, the new file joins the
        // (persisted) queue and waits -- clicking download must never silently un-pause a paused run.
        if (_liveEngine is null && !_fileQueueRunning && !DownloadPaused) _ = RunFileQueue();

        // Guide's example file: pin the selection to the queued file's game as it is queued.
        if (Guide.GuideActive && Guide.GuideFilePick is { } gp && gp.GogId == row.GogId && gp.FileKey == row.File.FileKey)
        {
            // The selection stays so grid highlight, detail pane and queued file agree; the next step's
            // whole-library button is handled at its own step (see GuideWantsWholeLibrary).
            Guide.DetailKeep = Library.Games.FirstOrDefault(g => g.GogId == row.GogId);
            Library.SelectedRow = Guide.DetailKeep ?? Library.SelectedRow;
        }
    }

    /// <summary>Right-click queue actions for a game's remaining files: filtered (honors content chips),
    /// all, or move-to-front. "Remaining" = in scope and not present anywhere; distinct from Missing.</summary>
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

    /// <summary>The one "queue this game's remaining files (and maybe put them first)" path, live run or not: the
    /// Storage rollup's entries and (09-20) the Library's "Download next" during a run both come through here, so
    /// the ENGINE is told too (new tasks, Reorder). The Library's first cut moved only the persisted entries and
    /// the promoted file sat at the head reading DOWNLOADING while the engine worked on in its old order.</summary>
    /// <param name="refresh">False inside a per-game loop (09-25): the caller saves and refreshes ONCE after the loop.
    /// A 100-game "Download next" ran 100 saves, 100 activity passes and 100 storage refreshes on the UI thread.</param>
    internal void QueueRemainingForItem(LibraryItem item, bool toTop, bool filtered, Grog.Core.Sync.Scope? scope = null, bool refresh = true)
    {
        if (_manifest is null) return;

        // Scope honors games/extras AND language (EffectiveScope): "ALL" means every content type, not other
        // languages. Filtered additionally requires a lit content chip (mirrors bulk Download); ALL ignores chips.
        var sc = scope ?? Library.EffectiveScope;
        var remaining = item.Files.Where(f =>
            sc.Includes(f) &&
            (!filtered || Library._allContentSelected || Library._selectedContent.Contains(GameRowViewModel.ContentKey(f, item.Type))) &&
            !Grog.Core.Sync.BackupScope.IsPresent(f)).ToList();
        if (remaining.Count == 0) return;

        // Persisted queue is the source of truth: enqueue any not-yet-queued files; when promoting, also move
        // the whole set to the front (0..n-1), preserving the game's own file order.
        int slot = 0;
        var newRows = new List<DetailFileRow>();
        using (_manifest.Gate.Enter())   // (manifest gate 09-08)
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
            // Live run: add genuinely-new files; when promoting, re-order (pre-empts so a promoted file starts
            // now) and re-ensure workers so additions are picked up even if a slot just went idle.
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
            // Idle: start a run. For "move to top" these are seeded first (front of the file-queue executor).
            foreach (var r in newRows) _fileQueue.Enqueue(r);
            _fileQueueTotal += newRows.Count;
            if (!_fileQueueRunning) _ = RunFileQueue();
        }

        Log($"{(toTop ? "Moved" : "Queued")} {remaining.Count} remaining file(s) for {item.Title}{(toTop ? " to the top of the download queue" : " for download")}.", category: LogCategory.Download);
        if (!refresh) return;
        RecomputeActivity();
        RefreshStorageIfIdle();
    }

    /// <summary>(09-26) Files clicked while a run was starting (DownloadRunning, no engine yet); EngineReady hands
    /// them to the engine, which built its list before they were queued.</summary>
    internal readonly List<DetailFileRow> _joinLiveOnReady = new();

    /// <summary>(09-26) Add one file to the live engine unless it already holds it; no second engine, ever.</summary>
    internal void HandToLiveEngine(DownloadEngine live, DetailFileRow row)
    {
        // An engine that is unwinding takes nothing: the file waits for the next run instead of vanishing into it.
        if (!DownloadRunning || !ReferenceEquals(_liveEngine, live)) { _joinLiveOnReady.Add(row); return; }
        if (!live.Snapshot.Any(t => t.File.GameGogId == row.GogId && t.File.FileKey == row.File.FileKey))
        {
            var item = Library.ItemById(row.GogId);
            live.EnqueueRange(new List<DownloadTask> { new DownloadTask { File = row.File, GameTitle = item?.Title ?? row.Name,
                GameSlug = item?.Slug ?? "", BytesReceived = row.File.PartialBytes ?? 0 } });
        }
        SyncLiveEngineToQueue(false);
    }

    /// <summary>Hand the persisted order to a live engine (pre-empting for a promotion) and re-size its worker pool.</summary>
    private void SyncLiveEngineToQueue(bool toTop)
    {
        if (_liveEngine is null || _manifest is null) return;
        if (toTop) _liveEngine.Reorder(_manifest.Current.Downloads.Snapshot().Select(q => (q.GogId, q.FileKey)).ToList());
        if (Settings.Xfer.AutoConcurrency) Settings.ApplyAutoConcurrency(_liveEngine, force: true);
        else _liveEngine.SetMaxConcurrent(Settings.Xfer.MaxConcurrentDownloads);
    }

    /// <summary>The once-after-the-loop half of <see cref="QueueRemainingForItem"/> with refresh: false.</summary>
    internal void AfterQueueEdits(bool toTop)
    {
        SaveInBackground("queue edits");
        if (_liveEngine is not null) SyncLiveEngineToQueue(toTop);
        RecomputeActivity();
        RefreshStorageIfIdle();
    }

    /// <summary>A queue edit while a run is live is picked up by the 1 Hz in-flight probe; paused or idle,
    /// nothing else rebuilds the storage rows, so their "queued here" would lag until a navigation.</summary>
    internal void RefreshStorageIfIdle()
    {
        if (DownloadPaused || _liveEngine is null) Storage.RefreshBackupLocations();
    }

    /// <summary>Download every file in a detail section (Game files / Extras), in order.</summary>
    [RelayCommand]
    private void DownloadSection(string? which)
    {
        var rows = which == "extras" ? Library.DetailExtraFiles : Library.DetailGameFiles;
        foreach (var r in rows)
            if (!Grog.Core.Sync.BackupScope.IsPresent(r.State) && !r.IsDownloading && !r.IsQueued)
                DownloadFile(r);
    }

    /// <summary>On a task's terminal state, update the persisted queue: Completed removes; Failed strikes and
    /// removes only when the 3-strike policy gives up (Corrupt). Returns true if the queue changed.</summary>
    private async Task RunFileQueue()
    {
        // Backstop scaffold: guide-skips, scheduled runs and CLI-set folders all arrive here without one.
        // CreateDirectory is idempotent, so this costs nothing when already done. Off the dispatcher: the
        // BackupLayout ctor + CreateDirectory xN stat a possibly-slow drive (UI-thread sweep 09-06 r2).
        // The running flag goes up BEFORE that first await so a second click cannot start a second executor.
        // (09-26) ONE engine at a time: a live run (the Back-up / Resume path never sets _fileQueueRunning) or one
        // still starting (DownloadRunning before EngineReady) owns the persisted queue; files join it instead.
        if (_liveEngine is not null || DownloadRunning) return;
        _fileQueueRunning = true;
        _stopRequested = false;
        try
        {
            // Inside the try (final check 09-06): a throw here must still clear the running flag, or the
            // per-file queue is dead for the session.
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

            // Drain the PERSISTED queue (DownloadFile/DownloadRemaining already appended the rows to it) through
            // the same BackupRun as the Back-up button, so per-file downloads behave like a bulk backup (pane
            // rows, Pause/Stop/cancel, journal). Files queued while a batch runs are picked up next loop.
            while (_fileQueue.Count > 0 && !_stopRequested)
            {
                while (_fileQueue.Count > 0) { _fileQueue.Dequeue(); }
                var host = new AppBackupHost(this, activityStrip: false);
                // BackupLayout ctor (ensures the primary root, scaffolds) is disk work: build the run off the
                // dispatcher, which this per-file path otherwise sits on (UI-thread sweep 09-06 r2).
                var run = await Task.Run(() => NewBackupRun(host));
                var r = await run.RunAsync(
                    AppRunOptions(new Grog.Core.Runs.BackupRunOptions(FromPersistedQueue: true), "download"),
                    _downloadCts?.Token ?? default);
                if (r.Error is Grog.Core.Runs.RunAlreadyActiveException)
                {
                    Log("A download run is already active: the files joined its queue.", category: LogCategory.Download);   // (09-26) backstop, not a failure
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
                    // The engine is over before the reconcile: a file queued meanwhile must not be handed to it.
                    _liveEngine = null;
                    await ReconcileAndRefresh();
                    Dispatcher.UIThread.Post(RecomputeActivity);
                }
                if (DownloadPaused || _stopRequested) break;
                // Files queued while the last batch was reconciling waited for an engine; they run as the next batch.
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

    /// <summary>The single funnel keeping derived state honest: re-derives each game's rollup status from
    /// file states, persists it, and refreshes every derived surface. Call after any state-changing op.</summary>
    internal async Task ReconcileAndRefresh()
    {
        var m = _manifest;
        if (m is null) return;
        // The Items x Files status walk runs on the thread pool whichever thread called (RunFileQueue resumed
        // here on the dispatcher after each batch); the snapshot ToArray keeps a concurrent UI render from
        // tripping enumeration (UI-thread sweep 09-06 r2).
        await Task.Run(() =>
        {
            using (m.Gate.Enter())   // (manifest gate 09-08) status rollup writes item.Status under the gate
                foreach (var item in m.Current.Items.ToArray())
                    LibrarySyncService.RecomputeStatus(item);
        });

        await m.SaveAsync();
        // The refresh is applied unconditionally, as before: it re-derives from whatever manifest is live now.
        await Dispatcher.UIThread.InvokeAsync(RefreshFromManifest);
    }

    /// <summary>Ensure a valid session before any download: validate/refresh the token, else open the WebView
    /// login on the UI thread. Returns false (with a status message) so callers can bail.</summary>
    private async Task<bool> EnsureReadyToDownloadAsync()
    {
        try
        {
            // Multi-account: any signed-in account can serve its files, so a lapsed session must NOT pop a
            // login here. Only when NO account is connected does the login window open.
            if (await AnyAccountConnectedAsync()) return true;   // token probes off the dispatcher (UI-thread sweep 09-06 r2)
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

    /// <summary>THE App's selection rule, as Core options: the movie toggle and the content chips are this
    /// view's filter, everything else (scope, gaps, updates, offline roots, the persisted-queue drain) is
    /// <see cref="Grog.Core.Runs.BackupQueueBuilder"/>. The enqueue, the scoped-label count and the submenu
    /// counts all build from here, so they cannot disagree (the grid-count bug was three hand copies).</summary>
    internal Grog.Core.Runs.BackupRunOptions SelectionOptions(bool updatesOnly, HashSet<long>? onlyGameIds, bool fromQueue,
                                                              Grog.Core.Sync.Scope scope, bool allContent, bool promotePicked = false)
    {
        var sel = Library._selectedContent; bool movies = Library.ShowLegacyMovies;
        return new Grog.Core.Runs.BackupRunOptions(
            FromPersistedQueue: fromQueue, UpdatesOnly: updatesOnly, OnlyGameIds: onlyGameIds, ScopeOverride: scope,
            PromotePicked: promotePicked,   // (09-20) the App's "Back up" joins the end; only "Download next" jumps the queue
            ViewFilter: (item, f) => (item.Type != ProductType.Movie || movies)
                                     && (allContent || sel.Contains(GameRowViewModel.ContentKey(f, item.Type))));
    }

    internal async Task DownloadFiltered(bool updatesOnly, HashSet<long>? onlyGameIds = null, bool fromQueue = false,
                                        bool? scopeGamesOverride = null, bool? scopeExtrasOverride = null,
                                        bool ignoreContentChips = false, bool promotePicked = false, bool unattended = false)
    {
        // Cleared before ANY early return, so a stale refusal never suppresses a later run's stamp.
        _downloadRefused = false; _downloadRefusedReason = null;
        if (Busy) return;
        // (09-26) ONE engine at a time: a per-file run holds neither Busy nor the activity slot. DownloadRunning is up
        // for the whole of either run and down once it unwinds; a PAUSED engine (kept for Resume) is not a live run.
        if (_fileQueueRunning || DownloadRunning)
        {
            _downloadRefused = true; _downloadRefusedReason = "a download run is already active";
            Log("A download run is already active: add games from the Library (they join it), or try again when it ends.", category: LogCategory.Download);
            return;
        }
        // A refusal below ran nothing, so it records nothing: SyncBackupCore reads the flag and stamps no outcome.
        if (!await EnsureReadyToDownloadAsync()) { _downloadRefused = true; _downloadRefusedReason = RefusedSignInNotReady; return; }
        // A schedule lifts the user's pause (owner 10-01: "pause now, let the 03:00 run do it" is the point of a
        // schedule). Grog's own pause for a missing drive is different: the drive is still gone, so the slot waits
        // for the next tick (transient) instead of starting a run that stops on its first file.
        if (unattended && DownloadPaused && Storage.PausedForDriveLabel is { } gone)
        { _downloadRefused = true; _downloadRefusedReason = $"{gone} is disconnected"; return; }
        if (unattended && DownloadPaused) Log("Scheduled backup: lifting the pause.", category: LogCategory.Download);
        // One file operation at a time: don't start downloading while a move holds the activity slot.
        if (!TryBeginActivitySlot("Can't start the download")) { _downloadRefused = true; _downloadRefusedReason = "a move is in progress"; return; }
        _stopRequested = false;
        SetDownloadPaused(false);   // a freshly-triggered / resumed backup is active, not a leftover paused session
        // Resume (fromQueue) continues the same session: keep the Done tally + byte meter so footer
        // progress does not reset. Only a fresh backup restarts the counters.
        if (!fromQueue) ResetRunCounters();
        DownloadRunning = true;
        try
        {
        await RunBusy("Downloading…", async () =>
        {
            // Selection, placement, engine, settlement, journal and outcome are Core's (BackupRun) -- the same
            // sequence the CLI runs. This side supplies the selection rule (scope, chips, movies), the host
            // that draws the run, and what to say when it ends.
            var selection = SelectionOptions(updatesOnly, onlyGameIds, fromQueue,
                Library.ScopeFor(scopeGamesOverride ?? Library.IncludeGames, scopeExtrasOverride ?? Library.IncludeExtras),
                allContent: Library._allContentSelected || ignoreContentChips, promotePicked: promotePicked);
            _heldNoSpaceCount = 0; _heldNoSpaceBytes = 0;   // Placed() fills them once the plan exists
            BackupRunBegan();
            var host = new AppBackupHost(this, activityStrip: true);
            var r = await NewBackupRun(host).RunAsync(AppRunOptions(selection, "backup"), _cts?.Token ?? default);
            _lastRunOutcome = r.Outcome;   // the run's own verdict (Partial counts a strike the App's GaveUp count does not; QA 09-30 C4)

            if (r.NothingToDo)
            {
                Log("Nothing to download");
                if (fromQueue) Dispatcher.UIThread.Post(RecomputeActivity);   // Core cleared the drained queue
                return;
            }
            if (r.NothingFits)
            {
                // Nothing at all fits (the Resume-with-a-full-drive case): said up front instead of a run that
                // would download zero files and end with a completion summary.
                Dispatcher.UIThread.Post(() => { RecomputeActivity(); RaiseNoSpaceText(); ShowNoSpaceAtAll = true; });
                return;
            }
            if (r.Error is Grog.Core.Runs.RunAlreadyActiveException)
            {
                // (09-26) The Core backstop refused: nothing ran, so nothing failed and nothing is stamped.
                _downloadRefused = true; _downloadRefusedReason = "a download run is already active";
                _liveEngine = null;
                Log("A download run is already active: this one did not start.", category: LogCategory.Download);
            }
            else if (r.Error is { } err)
            {
                // BackupRun hands the exception back instead of throwing, so RunBusy's catch never sees it:
                // stamp the run errored here or SyncBackupCore records a Completed night.
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

            // (09-19) "Finish current files" ran to its end: the files in progress landed and nothing new started.
            // With files still queued it ends as a pause for that reason; a user Pause in the meantime wins.
            if (Drain.Finishing)
            {
                // (review 09-19) Nothing left (the finish caught the last files, or the user cleared the queue
                // meanwhile): this is a finished run, not a pause. Fall through to the normal ending.
                bool anythingLeft = _manifest is not null && !_manifest.Current.Downloads.IsEmpty;
                if (anythingLeft)
                {
                    bool endAsPause = !DownloadPaused;
                    if (!endAsPause) Drain.Reset();
                    // Paused flag, meter and speed are UI state: set on the dispatcher, like the sibling endings.
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
            // Paused = freeze: leave bars, detail rows and the Downloading card as they are so a pause reads
            // as paused, not canceled. Only a real end / Stop tears the run down.
            if (DownloadPaused)
            {
                // No log line here: PauseDownloads (the only path that sets the flag) already logged the pause on
                // the click, and a second line when the run unwinds read as two pauses (09-17).
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
            // Stop / Clear queue ended the run: the user did that, so no "Backup finished", no failure toast, no
            // "stay queued" over a queue they just emptied (QA 09-30). CancelDownloads logged the stop already.
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
            // (09-25) Not failures: their storage went away. They stay queued for it (the card offers the choices).
            if (r.DriveOffline > 0)
                Log($"{r.DriveOffline} file{(r.DriveOffline == 1 ? "" : "s")} stopped because their storage was disconnected. They wait for it.",
                    category: LogCategory.Download);
            // Completion notifications fire for any run (manual or scheduled); each event is individually
            // opt-out in Settings, both default on.
            if (failedCount > 0)
            {
                if (Settings.NotifyOnErrors)
                    NotifyUser("Grog - backup finished with errors",
                        $"{failedCount} file{(failedCount == 1 ? "" : "s")} couldn't be backed up - open Grog to review.",
                        toastBody: $"{failedCount} file{(failedCount == 1 ? "" : "s")} couldn't be backed up.",
                        fixNav: ToastFix.Retry);   // (09-22, owner) the toast's action RETRIES the queue, it does not just navigate
            }
            else if (r.DriveOffline > 0)
            {
                // (review 09-25) Not "complete, no errors": files are waiting for a disconnected storage.
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
            // Files with nowhere to land were never attempted; report them once, at the end, the way the
            // scan reports what it could not take. They are still queued, so this is a "when you free space"
            // message rather than a failure.
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
            Dispatcher.UIThread.Post(Storage.RefreshDriveWait);   // (09-25) what still waits for a disconnected drive, now the run is over
            // The rate meter stops with the run: a finished run with files left queued kept the last live
            // speed and an ETA on the card (owner-hit 09-17).
            _meter.MarkIdle(); DownloadSpeedBytes = 0;
            DownloadRunning = false;
            // A run that reached its end (not paused) clears any lingering paused flag.
            if (!DownloadPaused && _manifest is not null && _manifest.Current.Downloads.Paused)
                SetDownloadPaused(false);   // single mutator owns the Paused fact (never poke it inline)
            // Files queued while this run was ending waited for an engine: the per-file executor takes them now.
            if (!DownloadPaused && _liveEngine is null && _joinLiveOnReady.Count > 0)
            {
                TakeJoinedFilesIntoQueue();
                if (!_fileQueueRunning) _ = RunFileQueue();
            }
        }
    }

    /// <summary>Move the files that waited for an engine into the per-file queue (UI thread).</summary>
    private void TakeJoinedFilesIntoQueue()
    {
        if (_joinLiveOnReady.Count == 0) return;
        foreach (var r in _joinLiveOnReady) { _fileQueue.Enqueue(r); _fileQueueTotal++; }
        _joinLiveOnReady.Clear();
    }

    // --- cover art: download+cache pipeline (the display toggles live on the Library page) ---

    /// <summary>Fetch + cache one game's grid cover and detail band from a single v2 call; skips when both
    /// files exist. The art cache is a derived, GogId-keyed store -- never domain truth.</summary>
    private async Task CacheArtForProductAsync(LibrarySyncService svc, long gogId, string dir, CancellationToken ct)
    {
        if (_api is null) return;
        var item = Library.ItemById(gogId);
        // v2 metadata (languages / age ratings) rides along in the same response as the art;
        // once captured, skip re-fetching for meta.
        // (09-19) A product not in the library yet NEEDS the metadata too: the merge has not made its item, so it
        // is stashed on the sync service, which puts it on the new item. It used to be dropped (first scan: all of it).
        bool needMeta = item is null || item.AgeRatings.Count == 0;
        var art = await Services.ArtCache.CacheProductAsync(_api, gogId, dir, needMeta, ct);
        if (art is not null && item is null) svc.StashMeta(gogId, art.Languages, art.AgeRatings);
        if (art is not null && item is not null && _manifest is not null)
        {
            using (_manifest.Gate.Enter())   // (manifest gate 09-08)
            {
                if (art.Languages.Count > 0) item.Languages = art.Languages.ToList();
                if (art.AgeRatings.Count > 0) item.AgeRatings = new Dictionary<string, int>(art.AgeRatings);
            }
        }
    }

    /// <summary>Fold per-product art fetching into a sync pass (no trailing art pass); resolves the art dir
    /// once and points the grid at it so rebuilt rows read art already on disk.</summary>
    private void AttachArtHook(LibrarySyncService svc)
    {
        var dir = Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot).ArtDir;   // art dir only; no backup-root probe (UI-thread sweep 09-06 r2)
        Directory.CreateDirectory(dir);
        GameRowViewModel.ArtDir = dir;
        svc.FetchArtAsync = (id, ct) => CacheArtForProductAsync(svc, id, dir, ct);
    }

    /// <summary>On a hash mismatch, ask GOG for the file's current checksum so a repack reads as
    /// UpdateAvailable, not Corrupt. Null unless connected; offline treats every mismatch as corruption.
    /// Handed to <see cref="Grog.Core.Runs.VerifyRequest.FetchCurrentServerMd5"/>. (S2.2)</summary>
    internal Func<GameFile, CancellationToken, Task<string?>>? ServerChecksumFetcher()
    {
        if (_api is null || !IsConnected) return null;
        return async (f, ct) =>
        {
            try
            {
                // Owner-picked session when accounts exist; the legacy single session otherwise.
                var api = _api;
                if (EngineSessions() is { } s && s.FirstAuthenticatedOwner(f) is { } owner) api = s.ApiFor(owner);
                // probe: false -- this caller only wants the checksum URL, never the CDN size probe.
                var resolved = await api.ResolveDownloadAsync(f.FileKey, ct, probe: false);
                return resolved.ChecksumXmlUrl is { } url ? await api.FetchMd5Async(url, ct) : null;
            }
            catch { return null; }   // offline / resolve failed → can't disambiguate; caller falls back to Corrupt
        };
    }

    /// <summary>Queue-pane rebuild, coalesced to 250ms: QueueChanged fires per enqueue/complete/remove
    /// and each rebuild is O(queue); on long small-file runs the per-event cost was the UI bottleneck.
    /// The coalescer's trailing edge guarantees the last change always renders.</summary>
    private Services.UiCoalescer? _queueUi;
    private void RequestQueueRebuild()
        => (_queueUi ??= new Services.UiCoalescer(TimeSpan.FromMilliseconds(250), () => { Overview.RebuildQueue(); RequestSizing(); })).Request();

    // (1287) Real sizes for queued files, learned in the background so the plan prices them at bytes, not GOG's label.
    // Starts when something is queued (every queue change comes through RequestQueueRebuild), never at launch.
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

    // Games active on the last RecomputeActivity pass, so the next pass resets only those rows to Idle.
    private HashSet<long> _prevActiveGames = new();

    /// <summary>Open any URL in the user's browser.</summary>
    [RelayCommand]
    internal void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        _ = Task.Run(() =>   // ShellExecute off the dispatcher (final check 09-06)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* best-effort */ }
        });
    }

    /// <summary>"Set backup location": pick a folder natively, then set or repoint the primary folder.</summary>
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
            await Task.Run(() => System.IO.Directory.CreateDirectory(full));   // (UI-thread sweep 09-06 r2)
            await BindToRootAsync(full);
            _settings.ChosenRoot = Grog.Core.Storage.GrogPaths.StorePath(_backupRoot); _settings.SaveSoon();
        }
        // Same order as before (tidy the old default, then scaffold the new folder), disk work off the
        // dispatcher (UI-thread sweep 09-06 r2).
        await TidyAutoCreatedDefaultAsync(previous, full);
        await ScaffoldChosenFolderAsync();
        Storage.RefreshBackupLocations();
        RaiseState();
    }

    /// <summary>Create Games / Extras / Cloud Saves under the primary folder. Called ONLY once the user has
    /// deliberately settled on a folder -- binding alone must not write to a still-tentative path.</summary>
    public void ScaffoldChosenFolder() => _ = ScaffoldChosenFolderAsync();   // (09-25) folder creation off the UI thread; best-effort either way

    /// <summary><see cref="ScaffoldChosenFolder"/> off the dispatcher: the manifest and root are captured on
    /// the calling thread and only the locals are used inside (UI-thread sweep 09-06 r2).</summary>
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

    /// <summary>Remove the auto-created default folder once the user picks elsewhere. THREE conditions, all
    /// required (the only place the app deletes anything): Grog-created, DEFAULT path, and EMPTY.
    /// The exists/enumerate/delete run off the dispatcher; the settings flag flips back on it (UI-thread sweep 09-06 r2).</summary>
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

    /// <summary>The "not everything fit" dialog is shown ONCE per set of leftovers: a user who read it, kept the red
    /// rows queued (the banner's stated contract) and later resumed to fetch one new file got the identical
    /// modal at that run's end, every time (QA 09-18). The banner and the divider carry the fact; the log line
    /// above records each run. A different count or size is news and shows again.</summary>
    private (int Count, long Bytes)? _noSpaceShownFor;
    private void OfferNoSpaceSummary(int held, long heldBytes)
    {
        if (_noSpaceShownFor is { } prev && prev.Count == held && prev.Bytes == heldBytes) return;
        _noSpaceShownFor = (held, heldBytes);
        // (09-19) The dialog reads these fields; Placed() filled them from the plan at the START of the run, and
        // mid-run re-plans move the line (749 in the dialog beside 744 in the log). The run's END result is the fact.
        _heldNoSpaceCount = held; _heldNoSpaceBytes = heldBytes;
        Dispatcher.UIThread.Post(() => { RaiseNoSpaceText(); ShowNoSpaceSummary = true; });
    }
}

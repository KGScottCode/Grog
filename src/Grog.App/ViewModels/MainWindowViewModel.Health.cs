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

// Run-side helpers the Overview's health flows hand off to: the fix re-queue and its engine handoff, the
// Esc detour, per-game verify, cancel and the RunBusy wrapper. (The health readouts moved to OverviewViewModel in S3.3.)
public partial class MainWindowViewModel
{
    /// <summary>Device summary line: what THIS folder holds against what the whole in-scope library needs.
    /// Both halves come from one pass with one predicate -- the device's raw FileCount counts every file on
    /// the drive, not just in-scope ones, and mixing the two yields "holds 4/3". The figures are gathered by
    /// the single per-root pass in RefreshBackupLocationsCore (UI-thread sweep 09-06); this only words them.</summary>
    internal static string CoverageLineFrom(int hereGames, int hereN, long hereB, int totalN, long totalB)
    {
        if (totalN == 0) return "";
        string bytes = $"{Grog.Core.Format.ByteFormat.Size(hereB)} / {Grog.Core.Format.ByteFormat.Size(totalB)}";
        return $"{hereGames} game{(hereGames == 1 ? "" : "s")}  ·  {bytes}  ·  {hereN} / {totalN} files";
    }

    /// <summary>(S2.2) Core's FixRun resets and front-queues the selection; what follows is the App's
    /// handoff: a paused queue stays staged, a live engine gets the fresh copies at the front, otherwise the
    /// queue is drained now.</summary>
    internal async Task RequeueProblemFiles(Grog.Core.Runs.FixSelector selector, string label,
                                           IReadOnlySet<(long, string)>? exclude = null)
    {
        if (_manifest is null) return;
        var result = await Grog.Core.Runs.FixRun.RequeueAsync(_manifest, selector, exclude);
        if (result.Count == 0)
        {
            Log($"No {label} files to fix - already resolved.", category: LogCategory.Verify);
            Overview.RaiseHealth();
            return;
        }
        AfterRequeue(result.Keys, result.Count, label);
    }

    /// <summary>The handoff after a FixRun re-queued <paramref name="keys"/> (front of the persisted queue).</summary>
    private void AfterRequeue(IReadOnlyList<(long GogId, string FileKey)> keys, int count, string label)
    {
        if (_manifest is null) return;
        _ = ReconcileAndRefresh();
        Overview.RaiseHealth();
        Storage.RefreshBackupLocations();   // the queue grew: "queued here" / fits-nowhere on the storage rows
        if (DownloadPaused)
        {
            // A paused queue stays paused: the fix is staged at the top, the user decides when it runs.
            Log($"Re-queued {count} {label} file(s) at the top of the queue - paused, so nothing downloads until you Resume.", category: LogCategory.Download);
            ShowToast($"Re-queued {count} file{(count == 1 ? "" : "s")} at the top of the queue. Resume to fetch.", 0);
            RecomputeActivity();
            return;
        }
        if (_liveEngine is { } live)
        {
            // A run is under way: DownloadFiltered would refuse (Busy) and "fetching now" would be a lie.
            // Hand the fresh copies to the LIVE engine at the front, the way QueueRemainingForGame does.
            // Only a LIVE task blocks a re-add: a Failed or Canceled one is exactly what is being re-queued (QA 09-19).
            var have = live.Snapshot
                .Where(t => t.State is Grog.Core.Download.DownloadTaskState.Pending or Grog.Core.Download.DownloadTaskState.Active)
                .Select(t => (t.File.GameGogId, t.File.FileKey)).ToHashSet();
            var fresh = _manifest.Read(m =>
            {
                var list = new List<Grog.Core.Download.DownloadTask>();
                foreach (var k in keys)
                {
                    if (!have.Add(k)) continue;
                    var item = m.ItemById(k.GogId);
                    var file = item?.Files.FirstOrDefault(f => f.FileKey == k.FileKey);
                    if (item is null || file is null) continue;
                    list.Add(new Grog.Core.Download.DownloadTask { File = file, GameTitle = item.Title, GameSlug = item.Slug });
                }
                return list;
            });
            if (fresh.Count > 0) live.EnqueueRange(fresh);
            live.Reorder(_manifest.Current.Downloads.Snapshot().Select(q => (q.GogId, q.FileKey)).ToList());
            if (Settings.Xfer.AutoConcurrency) Settings.ApplyAutoConcurrency(live, force: true);   // retired workers respawn for the additions
            else live.SetMaxConcurrent(Settings.Xfer.MaxConcurrentDownloads);
            Log($"Re-queued {count} {label} file(s) at the top of the running queue.", category: LogCategory.Download);
            // These went in unplanned (no target, Fits by default): the whole-queue pass gives each a drive or
            // takes it back out as won't-fit, instead of letting it fail disk-full a second time (QA 09-19).
            _ = Overview.ReplanQueueAsync("the re-queued files");
            RecomputeActivity();
            return;
        }
        Log($"Re-queued {count} {label} file(s) at the top of the queue - fetching now.", category: LogCategory.Download);
        // Show the fix happening: switch to the Library and drain the just-filled queue.
        Navigate("Library");
        _ = DownloadFiltered(false, fromQueue: true);
    }

    private string _filterBeforeDetour = "";
    private string _searchBeforeDetour = "";
    private bool _hasDetour;

    /// <summary>Esc backs out of a temporary filter and restores the prior view. Returns false when there
    /// is nothing to back out of, so Esc can fall through to clearing the selection.</summary>
    public bool TryEndDetour()
    {
        if (!_hasDetour) return false;
        _hasDetour = false;
        Library.FilterText = _searchBeforeDetour;
        Navigate(_filterBeforeDetour switch
        {
            "updates" => "Updates",
            "missing" => "Missing",
            "gone" => "Gone",
            _ => "Library",
        });
        return true;
    }

    // ---- Health-card verify progress: inline bar + "Verifying N of M" text by the Rescan & Verify button ----
    [ObservableProperty] private bool _verifying;

    /// <summary>Per-game "Rescan &amp; Verify": a FULL re-hash of every checksummed file of this one game (09-19).
    /// It was read-frugal (Unverified: a file already Verified got a size check only), so the user's explicit
    /// verify passed a Verified installer overwritten with same-size junk and toasted "all files verified".
    /// The user asked for THIS game; reading its files is the point. Automatic sweeps stay out of scope.
    /// Serialized against downloads/moves via the activity slot.</summary>
    [RelayCommand]
    private async Task VerifyGame(InventoryGroupRow? group)
    {
        if (group is null || Busy || DownloadRunning || _manifest is null || Storage.VerifyBlockedByMove()) return;
        var item = Library.ItemById(group.GogId);
        if (item is null) return;
        if (!TryBeginActivitySlot("Can't verify right now")) return;
        try
        {
            // Said at the start, not only at the end: a large game hashes for minutes with nothing else on screen (walk 7.2).
            ShowToast($"Rescanning and verifying {group.Title}…", 0);
            await RunBusy($"Verifying {group.Title}…", async () =>
            {
                // (S2.2) Core's VerifyRun; the per-game line and toast below stay this view's (silent host).
                var request = Grog.Core.Runs.VerifyRequest.ForItems(new[] { item.GogId }, Grog.Core.Verify.VerifyMode.FullRehash) with
                {
                    Progress = p => Dispatcher.UIThread.Post(() =>
                    {
                        ProgressVisible = true;
                        ProgressValue = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
                    }),
                    FetchCurrentServerMd5 = ServerChecksumFetcher(),   // a repack reads as an update, not as corruption
                };
                var r = (await Grog.Core.Runs.VerifyRun.RunAsync(_manifest, _backupRoot, request,
                    new Grog.Core.Runs.NullBackupHost(), _cts?.Token ?? default)).Verify;
                await ReconcileAndRefresh();
                bool trouble = r.Corrupt > 0 || r.Missing > 0;
                Log($"Verify {group.Title}: {r.Verified} verified, {r.SizeOnlyOk} size-only, {r.Corrupt} corrupt, {r.Missing} missing.",
                    isError: trouble, category: LogCategory.Verify);
                ShowToast(trouble
                    ? $"{group.Title}: {r.Corrupt + r.Missing} problem file{(r.Corrupt + r.Missing == 1 ? "" : "s")} - review Health."
                    : r.SizeOnlyOk > 0 ? $"{group.Title}: {r.Verified} verified by checksum, {r.SizeOnlyOk} by size (GOG gives no checksum)."
                    : $"{group.Title}: all files verified.", isError: trouble);
            });
        }
        finally { EndActivitySlot(); }
    }

    /// <summary>(09-19, owner) Files an import adopted are verified at once, as a VISIBLE activity: the import
    /// returns first so the grid and the outcome show immediately, then this takes the activity slot (so a
    /// Forget, a move or a run cannot land mid-verify), shows progress in the status bar and can be cancelled.
    /// Core's own in-import verify is for hosts with no UI (CLI, BackupRun); the App turns it off and calls this.</summary>
    /// <remarks>(09-25) <paramref name="withChecksums"/> is the Add dialog's "Verify file integrity with GOG's
    /// checksums": on, every adopted file is hashed against GOG's checksum (fetched when none is on record); off,
    /// a size check only.</remarks>
    internal async Task VerifyAdoptedAsync(Grog.Core.Runs.ImportRunResult result, bool withChecksums)
    {
        var keys = result.Plan?.AdoptedKeys;
        if (keys is null || keys.Count == 0 || _manifest is null) return;
        var label = result.Root.Label;
        if (Busy || DownloadRunning || Storage.IsReorgRunning || Storage.ReorgPaused
            || !TryBeginActivitySlot($"Can't verify the {keys.Count} adopted file(s) on {label} right now"))
        {
            Log($"{keys.Count} adopted file(s) on {label} are not verified yet: right-click a game on the Storage page, Rescan & verify.", category: LogCategory.Verify);
            return;
        }
        try
        {
            await RunBusy($"Verifying {keys.Count} adopted file(s) on {label}…", async () =>
            {
                var fetcher = withChecksums ? ServerChecksumFetcher() : null;
                if (withChecksums && fetcher is null)
                    Log($"GOG's checksums can't be fetched while offline: adopted files on {label} are size-checked only. Rescan & Verify checks them later.",
                        category: LogCategory.Verify);
                var request = Grog.Core.Runs.VerifyRequest.ForItems(keys.Select(k => k.GogId).Distinct(),
                    withChecksums ? Grog.Core.Verify.VerifyMode.Unverified : Grog.Core.Verify.VerifyMode.SizeOnly) with
                {
                    FetchMissingChecksums = withChecksums && fetcher is not null,
                    Progress = p => Dispatcher.UIThread.Post(() => Storage.SetVerifyProgress(result.Root.Id, p.Done, p.Total)),
                    FetchCurrentServerMd5 = fetcher,
                };
                Dispatcher.UIThread.Post(() => Storage.SetVerifyProgress(result.Root.Id, 0, keys.Count));
                Grog.Core.Verify.VerifyResult r;
                try
                {
                    r = (await Grog.Core.Runs.VerifyRun.RunAsync(_manifest, _backupRoot, request,
                        new Grog.Core.Runs.NullBackupHost(), _cts?.Token ?? default)).Verify;
                }
                finally { Dispatcher.UIThread.Post(() => Storage.SetVerifyProgress(null, 0, 0)); }
                await ReconcileAndRefresh();
                bool trouble = r.Corrupt > 0 || r.Missing > 0;
                Log($"Verified adopted files on {label}: {r.Verified} verified, {r.SizeOnlyOk} size-only, {r.Corrupt} corrupt, {r.Missing} missing.",
                    isError: trouble, category: LogCategory.Verify);
                if (r.DiffersFromGog > 0)
                {
                    var msg = $"{r.DiffersFromGog} adopted file(s) on {label} differ from GOG's current file (an older build, or damaged). Nothing was changed. Re-downloading replaces the copy on disk.";
                    Log(msg, category: LogCategory.Verify);
                    ShowToast(msg);
                }
            });
        }
        finally { EndActivitySlot(); }
    }

    public bool CanCancel => Busy;

    [RelayCommand]
    private void CancelWork() => _cts?.Cancel();

    internal async Task RunBusy(string startMsg, Func<Task> work)
    {
        try
        {
            _cts = new CancellationTokenSource();
            // Catalog ops drive the CTA mini bar, not the status-bar bar; download/verify opt in themselves.
            Busy = true; ProgressValue = 0; Log(startMsg);
            OnPropertyChanged(nameof(CanCancel));
            await Task.Run(work);
        }
        catch (OperationCanceledException) { Log(Library.CatalogBusy ? "Scan canceled." : "Paused. Partial downloads kept. They resume where they left off."); }
        catch (Exception ex) { _runErrored = true; Log($"Error: {ex.Message}", isError: true); }
        finally
        {
            Busy = false; ProgressVisible = false;
            _cts?.Dispose(); _cts = null;
            // A canceled/errored scan threw before EndCatalog: the library ends it as NOT completed and drops
            // the preview rows, so a half-read catalog never reports itself as scanned. (No-op after a normal completion.)
            Library.EndAbandonedScan();
            OnPropertyChanged(nameof(CanCancel));
            EndActivity();
            RaiseDashboard();
            // A move deferred behind a scan, verify or sign-out starts now; moves run beside downloads, and the call is idempotent.
            Storage.StartDeferredMove();
        }
    }
}

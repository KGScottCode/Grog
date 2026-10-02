// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Download;
using Grog.Core.Format;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Volumes;

namespace Grog.App.ViewModels;

// Overview: the Downloading pane's queue MOVES and the won't-fit line (09-16). The tail is drawn (Activity.cs
// RebuildQueueCore); this file owns everything that changes the order or reads the line: drag mapping, the row
// menu, Pull up, the drop rule, the glow, Show me and Remove them. Rules live in Core (QueueShaper,
// PlacementPlanner.WouldFit); this file renders and commits.
public sealed partial class OverviewViewModel
{
    /// <summary>Reconcile the bound <see cref="DownloadTail"/> to <paramref name="desired"/> in place: no
    /// Clear()/Reset, so the list control only realizes/drops the rows that actually changed.</summary>
    private void ReconcileTail(List<object> desired)
    {
        // (09-25) A re-sort or a regroup reorders most of the list: one Move per displaced row, each an O(N) shift plus
        // a list-control pass, was the 150-450 ms "reconcile" phase (grog.log 09-25). Past a quarter of the rows out
        // of place, ONE reset is cheaper: a virtualized list re-realizes only the rows on screen.
        // Counted as breaks in RELATIVE order, not rows out of position: a file landing at the head shifts every row by
        // one, which is one change, not N (review 09-25).
        int n = desired.Count, breaks = 0;
        if (n >= 64)
        {
            var pos = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < DownloadTail.Count; i++) pos[DownloadTail[i]] = i;
            int prev = -2;
            for (int i = 0; i < n && breaks <= n / 4; i++)
            {
                int p = pos.TryGetValue(desired[i], out var at) ? at : -1;
                if (p < 0 || p != prev + 1) breaks++;
                prev = p;
            }
            if (breaks > n / 4) { DownloadTail.ReplaceAll(desired); return; }
        }
        LibraryViewModel.ReconcileCollection(DownloadTail, desired);
    }

    /// <summary>Map a drop onto the DRAWN tail (index into <see cref="DownloadTail"/>) to a position in the
    /// persisted queue. A QueueRow target gives its own persisted index; the divider means "just below the
    /// last row that fits". -1 when nothing sensible (an empty target).</summary>
    public int PersistedIndexOfTailDrop(int tailIndex, QueueRow moving)
    {
        if (_manifest is null || tailIndex < 0 || tailIndex >= DownloadTail.Count) return -1;
        var snap = _manifest.Current.Downloads.Snapshot();
        if (DownloadTail[tailIndex] is QueueRow target)
        {
            var k = KeyOf(target);
            return k is null ? -1 : snap.FindIndex(q => q.GogId == k.Value.Item1 && q.FileKey == k.Value.Item2);
        }
        // Divider: the bottom of the white group, in MoveTo's post-removal index space.
        return Grog.Core.Runs.QueueShaper.EndOfFittingBlock(snap, KeyOf(moving));
    }

    /// <summary>The persisted (GogId, FileKey) of a row: the cache key IS that pair (the file's own GameGogId is
    /// not set on every seed), so never derive it from the file.</summary>
    private (long, string)? KeyOf(QueueRow r)
    {
        foreach (var kv in _rowCache) if (ReferenceEquals(kv.Value, r)) return kv.Key;
        return null;
    }

    /// <summary>Reorder within the persisted download queue (the action), then mirror onto the running
    /// engine so the change takes effect immediately. The queue is the truth; the engine follows it.</summary>
    public void MoveQueueRow(QueueRow row, int newIndex)
    {
        var manifest = _manifest;
        if (manifest is null) return;
        // A manual drag takes over from any sticky sort: revert to "Queue order" so the sort does not
        // re-sequence over the user's hand-placement on the next activity pass.
        if (QueueSort != QueueSortKey.Order) { _queueSortState.Reset(QueueSortKey.Order, asc: true); RaiseQueueSortGlyphs(); }
        manifest.Mutate(m => m.Downloads.MoveTo(row.Task.File.GameGogId, row.Task.File.FileKey, newIndex));
        manifest.SaveSoon();   // ordering only
        // The engine follows the persisted ORDER, never the persisted INDEX: the engine holds only fitting rows,
        // so an index counted over red rows landed elsewhere in the engine. Gate released: Reorder raises QueueChanged.
        _liveEngine?.Reorder(manifest.Current.Downloads.Snapshot().Select(q => (q.GogId, q.FileKey)).ToList());
        bool wasRed = row.WontFit;
        _root.RecomputeActivity();
        // Placement is planned in queue order, so the flags must be re-planned with the new order, run live or
        // not. RefreshStorageIfIdle skipped a PAUSED run (engine still up), so a red row dragged above the divider
        // kept its stale flag and dropped straight back below it (owner-hit 09-16). The glow + scroll come AFTER
        // the re-plan: a row can cross the divider on the re-plan and land somewhere else than the drop point
        // (a small red row dropped among red rows fits, turns white and regroups up), and scrolling to the
        // pre-plan spot showed the row "vanishing" (owner-hit 09-16, twice).
        _ = ReplanThenFlashAsync(row, wasRed);
    }

    /// <summary>Row menu (09-16): straight to the front. Behind the file downloading now when one is; the very
    /// head otherwise (nothing running to preempt).</summary>
    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveQueueRowToFront(QueueRow? row)
    {
        if (row is null || _manifest is null) return;
        // Behind the ACTIVE row wherever it sits in the persisted order (a red row can sit at the persisted
        // head while the pill is the first fitting row): assuming index 1 displaced the file downloading now
        // (QA 09-18). MoveTo's index is post-removal, so a row above the active one counts one less.
        int at = 0;
        if (DownloadPill is { IsActive: true } pill && KeyOf(pill) is { } pk)
        {
            var snap = _manifest.Current.Downloads.Snapshot();
            int active = snap.FindIndex(q => q.GogId == pk.Item1 && q.FileKey == pk.Item2);
            int self = KeyOf(row) is { } rk ? snap.FindIndex(q => q.GogId == rk.Item1 && q.FileKey == rk.Item2) : -1;
            at = active < 0 ? 0 : active + 1 - (self >= 0 && self < active ? 1 : 0);
        }
        MoveQueueRow(row, at);
    }

    /// <summary>A fitting row can always move up; a red row only when the planner's own rule says it could land
    /// somewhere with the room still free (routed root first, spill only outside Manual mode). The menu greys
    /// out otherwise, so a move the next re-plan would undo is never offered (owner 09-16).</summary>
    private bool CanMoveUp(QueueRow? row)
        => row is not null && _manifest is not null
           && (!row.WontFit || Grog.Core.Runs.QueueShaper.CouldFit(_manifest.Current, row.Task.File, WontFitDivider.RoomByRoot));

    /// <summary>Row menu (09-16): lift this red row to the bottom of the white group, the lowest place it fits.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void PullUpRow(QueueRow? row)
    {
        if (row is null || _manifest is null) return;
        int at = Grog.Core.Runs.QueueShaper.EndOfFittingBlock(_manifest.Current.Downloads.Snapshot(), KeyOf(row));
        if (at < 0) return;
        MoveQueueRow(row, at);
    }

    /// <summary>Drag rule for the download tail (09-16): a fitting row over a won't-fit row is refused, with a
    /// hint saying why; everything else is a valid target. The hint shows on EVERY attempt (owner: they may not
    /// have read it the first time), throttled to once per few seconds because this is asked on every pointer
    /// move over the red group during one drag.</summary>
    public bool CanDropOn(QueueRow row, object target)
    {
        if (row.WontFit || target is not QueueRow { WontFit: true }) return true;
        long now = Environment.TickCount64;
        if (now - _whiteIntoRedHintAt > 4000)
        {
            _whiteIntoRedHintAt = now;
            const string hint = "Files that fit always stay above the line. Drag red files up to try to fit them, or remove files to free additional space.";
            ShowToast(hint, 0);
            Log(hint, category: LogCategory.Download);
        }
        return false;
    }
    private long _whiteIntoRedHintAt = long.MinValue / 2;

    private async Task ReplanThenFlashAsync(QueueRow row, bool wasRed)
    {
        bool planned = await ReplanQueueAsync("the new order");
        if (planned)
        {
            // The one outcome that surprises: a red row dragged up that still has no room. Say the number.
            if (wasRed && row.WontFit)
                ShowToast($"{row.Name} ({row.SizeText}) still won't fit: {ByteFormat.Size(WontFitDivider.RoomLeft)} free. Remove or move down larger files above it.", 1);
            string where = row.WontFit ? (wasRed ? "still below the line" : "now below the line: it no longer fits")
                                       : (wasRed ? "now above the line: it fits" : "above the line");
            // Row N of the group it is drawn in, so the log says WHERE to look (a fitting row dropped among red
            // rows is drawn at the bottom of the white group, not where the pointer let go).
            int drawn = 0, of = 0;
            foreach (var o in DownloadTail) if (o is QueueRow q && q.WontFit == row.WontFit) { of++; if (ReferenceEquals(q, row)) drawn = of; }
            // The pill is not in the tail: a row moved to the head is "next up", not "row 0 of N" (QA 09-18).
            Log(drawn == 0
                ? $"Moved {row.Name}: {where}, next up."
                : $"Moved {row.Name}: {where} (row {drawn} of {of} {(row.WontFit ? "below" : "above")} it).", category: LogCategory.Download);
        }
        else Log($"Moved {row.Name}.", category: LogCategory.Download);
        FlashMoved(row);
    }

    /// <summary>The Storage tally sets the room; the menus' enabled state follows it.</summary>
    internal void RoomLeftChanged()
    {
        MoveQueueRowToFrontCommand.NotifyCanExecuteChanged(); PullUpRowCommand.NotifyCanExecuteChanged();
        PullUpWhatFitsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Divider action (09-16): walk the won't-fit rows in queue order and lift every one that fits in the
    /// room still free up to the end of the fitting block, greedily, keeping their relative order. The user's
    /// click, one reorder, then one re-plan; nothing is removed.</summary>
    [RelayCommand(CanExecute = nameof(CanPullUpAnything))]
    private void PullUpWhatFits()
    {
        if (_manifest is null) return;
        var (order, pulledKeys) = Grog.Core.Runs.QueueShaper.PullUpWhatFits(
            _manifest.Current, _manifest.Current.Downloads.Snapshot(), WontFitDivider.RoomByRoot);
        if (pulledKeys.Count == 0) { Log("Nothing below the line fits in the room still free.", category: LogCategory.Download); return; }
        var pulled = pulledKeys.Select(k => _rowCache.TryGetValue(k, out var r) ? r : null).Where(r => r is not null).Select(r => r!).ToList();
        // One reorder, not one MoveTo per file: each engine MoveTo raises QueueChanged and runs pre-emption
        // (the O(N x queue) shape CancelRange/EnqueueRange exist to avoid; review 09-16).
        _manifest.Mutate(m => m.Downloads.SetOrder(order));   // (manifest gate 09-08)
        _liveEngine?.Reorder(order);
        if (QueueSort != QueueSortKey.Order) { _queueSortState.Reset(QueueSortKey.Order, asc: true); RaiseQueueSortGlyphs(); }
        _manifest?.SaveSoon();   // ordering only
        Log($"Pulled up {pulled.Count} file(s) that fit in the room still free.", category: LogCategory.Download);
        _root.RecomputeActivity();
        _ = ReplanQueueAsync("the new order").ContinueWith(_ => { foreach (var r in pulled) FlashMoved(r); },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Greyed until at least one red row could land in the room still free (review 09-16: the button
    /// did nothing, silently, on a full drive).</summary>
    // Reads the row CACHE (every queued row), not the drawn tail: red rows are only drawn while the divider is
    // unfolded, so the folded default greyed the button over rows that would have fit (QA 09-18).
    private bool CanPullUpAnything()
        => _manifest is not null && WontFitDivider.RoomLeft > 0
           && _rowCache.Values.Any(r => r.WontFit && Grog.Core.Runs.QueueShaper.CouldFit(_manifest.Current, r.Task.File, WontFitDivider.RoomByRoot));

    [RelayCommand]
    private void MoveQueueRowToEnd(QueueRow? row)
    {
        if (row is null || _manifest is null) return;
        MoveQueueRow(row, _manifest.Current.Downloads.Snapshot().Count - 1);
    }

    [RelayCommand]
    private void CancelQueueRow(QueueRow? row)
    {
        if (row is null || _manifest is null) return;
        // Remove from the persisted queue, cancel the live transfer, DELETE the partial. Pause keeps the
        // partial (coming back to it); an explicit cancel says you are not, so no .grog-tmp stub stays.
        _manifest.Mutate(m => m.Downloads.Remove(row.Task.File.GameGogId, row.Task.File.FileKey));   // (manifest gate 09-08)
        _liveEngine?.CancelFile(row.Task.File.GameGogId, row.Task.File.FileKey);
        row.Task.File.HasPartial = false;
        Log($"Canceled {row.Name}. Its partial download was removed.", category: LogCategory.Download);
        _root.RecomputeActivity();
        RaiseHealth();   // a failed file taken out of the queue is unaddressed again: the card reads queue membership (QA 09-30 C7)
        // Re-plan NOW, not on the next page visit: the fits-nowhere warning, bar segment and chip must
        // fall with every removal so trimming the queue down to "fits" is watchable in place.
        _root.Storage.RefreshBackupLocations();
        // DISK WORK OFF THE CLICK (owner-felt lag, 08-30): serializing the whole manifest and deleting a
        // partial on a busy USB device both block for real time -- neither belongs on the UI thread.
        // SaveAsync is gate-serialized, so a concurrent save is safe; the UI state above updated already.
        var engine = _liveEngine; var file = row.Task.File; var title = row.GameTitle;
        _ = Task.Run(async () =>
        {
            try { engine?.DeletePartial(file, title); } catch { /* best-effort cleanup */ }
            try { await _manifest.SaveAsync(); } catch { /* next save persists the removal */ }
        });
    }

    // ---- Won't-fit banner actions (owner 09-11: inform, never choose -- but make the informing actionable) ----

    /// <summary>The view scrolls the waiting list to this tail index. Raised by Show me (centered: the rows
    /// that fit above the divider, the red rows below it) and by a moved row (nearest edge).</summary>
    public event Action<int, bool>? ShowWontFitRequested;

    /// <summary>(09-16) A moved row is scrolled into view and lit for a moment: a drop that lands off screen, or
    /// under a re-plan that regroups the list, otherwise reads as the row vanishing (owner-hit). The pill is
    /// not in the tail, so a row moved to the head is not scrolled to (it is already at the top).</summary>
    private void FlashMoved(QueueRow row)
    {
        // A row that landed below the line lives in the folded group: unfold it, or the glow lights an
        // unrealized row and the drop still reads as "vanished" (review 09-16).
        if (row.WontFit && !WontFitDivider.Expanded) { WontFitDivider.Expanded = true; RebuildQueue(); }
        int i = DownloadTail.IndexOf(row);
        if (i >= 0) ShowWontFitRequested?.Invoke(i, false);
        row.JustMoved = true;
        // One timer per row, restarted on a second move, so the first glow's tick cannot cut the second short.
        if (!_glowTimers.TryGetValue(row, out var timer))
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            timer.Tick += (_, _) => { timer.Stop(); row.JustMoved = false; _glowTimers.Remove(row); };
            _glowTimers[row] = timer;
        }
        timer.Stop(); timer.Start();
    }
    private readonly Dictionary<QueueRow, DispatcherTimer> _glowTimers = new();

    /// <summary>Unfold the won't-fit divider and scroll the tail to it (09-16). The pill is never a won't-fit
    /// row unless every row is, so the divider is always in the tail while any row is flagged.</summary>
    [RelayCommand]
    private void ShowWontFit()
    {
        // Unfold the divider first so the red rows are on screen, then scroll to the divider itself: the count
        // is the headline and the rows sit right under it.
        if (!WontFitDivider.Expanded) { WontFitDivider.Expanded = true; RebuildQueue(); }
        int i = DownloadTail.IndexOf(WontFitDivider);
        if (i >= 0) ShowWontFitRequested?.Invoke(i, true);
    }

    [ObservableProperty] private bool _showRemoveWontFitConfirm;
    [ObservableProperty] private string _removeWontFitConfirmText = "";
    [ObservableProperty] private bool _dontAskRemoveWontFitAgain;

    /// <summary>Remove ONLY the rows flagged won't-fit. Everything else keeps its position. Confirms once,
    /// with "Don't ask again" (app setting, per machine). Never a modal from a run: this is the user's click.</summary>
    [RelayCommand]
    private void RemoveWontFit()
    {
        if (_manifest is null) return;
        var flagged = _manifest.Current.Downloads.WontFit();
        if (flagged.Count == 0) return;
        if (_session.Settings.SuppressRemoveWontFitConfirm) { DoRemoveWontFit(); return; }
        long bytes = 0;
        var itemById = _manifest.Current.Items.ToDictionary(i => i.GogId);
        foreach (var q in flagged)
            if (itemById.TryGetValue(q.GogId, out var it) && it.Files.FirstOrDefault(f => f.FileKey == q.FileKey) is { } f)
                bytes += Grog.Core.Volumes.PlacementPlanner.StillNeeds(f);
        RemoveWontFitConfirmText = $"{flagged.Count} file{(flagged.Count == 1 ? "" : "s")} ({ByteFormat.Size(bytes)}) "
            + "will be removed from the queue. Every other file keeps its place and its order. "
            + "Nothing already downloaded is touched; you can queue these again any time.";
        DontAskRemoveWontFitAgain = false;
        ShowRemoveWontFitConfirm = true;
    }

    [RelayCommand]
    private void CancelRemoveWontFit() { ShowRemoveWontFitConfirm = false; DontAskRemoveWontFitAgain = false; }

    [RelayCommand]
    private void ConfirmRemoveWontFit()
    {
        ShowRemoveWontFitConfirm = false;
        if (DontAskRemoveWontFitAgain) { _session.Settings.SuppressRemoveWontFitConfirm = true; _session.Settings.SaveSoon(); }
        DontAskRemoveWontFitAgain = false;
        DoRemoveWontFit();
    }

    private async void DoRemoveWontFit()
    {
        try { await DoRemoveWontFitCore(); }
        catch (Exception ex) { _root.Log($"Couldn't remove the files that won't fit: {ex.Message}", isError: true, category: LogCategory.Download); }   // async void: never unobserved
    }
    private async Task DoRemoveWontFitCore()
    {
        if (_manifest is null) return;
        // (09-25) The flags are re-decided off the UI thread now: settle them first, so a row a pending re-plan would
        // have turned green is never removed as red.
        await ReplanQueueAsync("removing what won't fit");
        var manifest = _manifest;
        // The queue walk and the gate wait run off the dispatcher; only the refresh comes back.
        var gone = await Task.Run(() => { var g = new List<QueuedDownload>(); manifest.Mutate(m => g = m.Downloads.RemoveWontFit()); return g; });
        if (gone.Count == 0) return;
        // They are not in the engine: Place never hands a won't-fit file over, and both re-plans cancel a file
        // that stops fitting (Replan via BackupRun's CancelRange, ReplanWholeQueue directly). The queue entry
        // was the only thing they had.
        Log($"Removed {gone.Count} file{(gone.Count == 1 ? "" : "s")} that won't fit from the queue. The rest of the queue is unchanged.", category: LogCategory.Download);
        RaiseHealth();   // (QA 09-30 C7)
        _root.RecomputeActivity();
        _root.Storage.RefreshBackupLocations();   // the banner and the red segment fall with the removal
        Runtime.ManifestSaves.Default.SaveInBackground(_manifest, "remove won't fit");
    }

}

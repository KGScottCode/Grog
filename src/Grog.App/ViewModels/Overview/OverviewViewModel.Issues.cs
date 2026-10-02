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
using Avalonia.Input.Platform;
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

// Overview (S3.3): the issues modal (checks, gap breakdown, drill-down lists, Fix / Acknowledge / Try Again),
// the recent-errors list and the health raise.
public sealed partial class OverviewViewModel
{
    // Issue groups: each kind is a row with a Fix action, shown only when non-empty. They live in a
    // dialog, not on the page; the lede carries the alert.
    [ObservableProperty] private bool _showIssuesModal;
    // Always opens: a healthy library gets an all-clear panel rather than a dead click target.
    [RelayCommand] private void OpenIssues()    => ShowIssuesModal = true;
    [RelayCommand] private void DismissIssues() => ShowIssuesModal = false;

    /// <summary>The trigger's label: the count, in the words of the worst finding.</summary>
    public string IssuesTriggerText
    {
        get
        {
            if (HealthIssueCount > 0)
                return HealthIssueCount == 1 ? "1 issue needs attention" : $"{HealthIssueCount} issues need attention";
            // A gap is not a fault so it is not an issue, but the panel must not claim all is well
            // while the headline reports the gap. Reads the tally RaiseHealth already computed - a bound
            // getter must never mutate state.
            int gap = Math.Max(0, _stats.InScopeFiles - _stats.InScopePresentFiles);
            if (_stats.InScopeFiles == 0) return "Nothing scanned yet";
            return gap > 0 ? $"{gap} file{(gap == 1 ? "" : "s")} still to back up" : "Nothing needs attention";
        }
    }

    /// <summary>The checks behind the verdict as a scannable list: always the same rows in the same order,
    /// healthy or not, so "good" is confirmed by glancing down a column of values.</summary>
    public System.Collections.ObjectModel.ObservableCollection<HealthCheckRow> HealthChecks { get; } = new();

    // ---- The gap, end to end (Files & issues top section, owner-picked Option C 08-31): the queue's
    //      place INSIDE the gap drawn as containment, so "8 GB fits nowhere" can never hide again. ----
    private int _unplaceableQueueCount; private long _unplaceableQueueBytes;   // set by RefreshBackupLocations
    private static string FilesAndBytes(int f, long b) => $"{f} file{(f == 1 ? "" : "s")} · {ByteFormat.Size(b)}";
    private long GapShownBytes => Math.Max(0, _stats.GapBytes - _partialGameBytes - _partialExtraBytes);
    private int GapFileCount => Math.Max(0, _stats.InScopeFiles - _stats.InScopePresentFiles);
    public string GapLineText => FilesAndBytes(GapFileCount, GapShownBytes);
    public string GapQueuedText => FilesAndBytes(DownloadQueueCount, DownloadQueueBytes);
    public bool HasUnplaceableQueue => _unplaceableQueueBytes > 0;
    public string GapUnplaceableText => FilesAndBytes(_unplaceableQueueCount, _unplaceableQueueBytes);
    public string GapNotQueuedText => FilesAndBytes(Math.Max(0, GapFileCount - DownloadQueueCount),
                                                    Math.Max(0, GapShownBytes - DownloadQueueBytes));
    public string BackedUpLineText => FilesAndBytes(_stats.InScopePresentFiles, _stats.PresentBytes);
    public string LibraryTotalLineText => FilesAndBytes(_stats.InScopeFiles, _stats.PresentBytes + _stats.GapBytes);
    internal void RaiseGapBreakdown()
    {
        OnPropertyChanged(nameof(GapLineText)); OnPropertyChanged(nameof(GapQueuedText));
        OnPropertyChanged(nameof(HasUnplaceableQueue)); OnPropertyChanged(nameof(GapUnplaceableText));
        OnPropertyChanged(nameof(GapNotQueuedText)); OnPropertyChanged(nameof(BackedUpLineText));
        OnPropertyChanged(nameof(LibraryTotalLineText));
        OnPropertyChanged(nameof(GamesSizeText)); OnPropertyChanged(nameof(ExtrasSizeText));
    }
    /// <summary>RefreshBackupLocations hands the live plan's fits-nowhere tally here.</summary>
    internal void SetUnplaceableQueue(int count, long bytes)
    {
        if (_unplaceableQueueCount == count && _unplaceableQueueBytes == bytes) return;
        _unplaceableQueueCount = count; _unplaceableQueueBytes = bytes;
        RaiseGapBreakdown();
    }

    /// <param name="retally">False when the caller has JUST run RecomputeHealthTally and nothing touched the
    /// manifest since (RaiseHealthCore, the dashboard pass): the tally is a full library walk (UI-thread sweep 09-06).</param>
    internal void RebuildHealthChecks(bool retally = true)
    {
        RaiseGapBreakdown();
        int roots = _root.Storage.BackupLocations.Count;
        int online = Math.Max(0, roots - HealthDeviceProblemCount);
        if (retally) RecomputeHealthTally();
        var rows = new[]
        {
            // Coverage moved to the gap-breakdown section above the checks (Option C, owner 08-31);
            // these rows are the FAULT checks only.
            // Damaged bytes and files gone from disk are the two real faults on this page.
            new HealthCheckRow("Corrupt files", HealthCorruptCount.ToString(),
                               HealthCorruptCount > 0 ? Grog.Core.Sync.Severity.Fault : Grog.Core.Sync.Severity.Quiet,
                               "Downloaded files whose contents no longer match GOG's checksum. Fix Now re-downloads them."),
            new HealthCheckRow("Missing from disk", HealthGoneCount.ToString(),
                               HealthGoneCount > 0 ? Grog.Core.Sync.Severity.Fault : Grog.Core.Sync.Severity.Quiet,
                               "Files Grog recorded as backed up that are no longer where it put them. Fix Now re-downloads them."),
            // "none set" is setup still to do, not a fault. HealthDeviceProblemCount excludes an unplugged
            // removable, so a safely-detached drive stays quiet here as it does on Folders.
            new HealthCheckRow("Storage connected", roots == 0 ? "none set" : $"{online} of {roots}",
                               HealthDeviceProblemCount > 0 ? Grog.Core.Sync.Severity.Fault
                                                            : roots == 0 ? Grog.Core.Sync.Severity.Attention : Grog.Core.Sync.Severity.Quiet,
                               "Storage folders Grog can reach right now. A drive marked removable does not count against this when unplugged."),
            // A failed run is recoverable work, so amber -- matching StatusLede's Kind.RunFailed level.
            new HealthCheckRow("Last backup run",
                               _lastRunFailure is { } r ? $"{r.Failed} failed{(r.ConnectionLost ? $" ({ConnectionLostShort})" : "")}" : "no failures",
                               _lastRunFailure is null ? Grog.Core.Sync.Severity.Quiet : Grog.Core.Sync.Severity.Attention,
                               "Files the most recent backup run could not download and that are no longer queued. Try Again puts them back at the top of the queue."),
            // Not a fault, never actionable, but VISIBLE (owner 09-13: transparent): files GOG lists but does not
            // serve, refused or listed with no size. Outside every completeness number, so this is the one place
            // the user can see the count is small and not a silent hole.
            new HealthCheckRow("Not offered by GOG", HealthUnavailableCount.ToString(), Grog.Core.Sync.Severity.Quiet,
                               "Files GOG lists for your games but does not serve: your account is refused, or GOG shows no size (nothing to download). "
                             + "They never count against your backup. Each rescan checks again and clears the flag if GOG starts offering the file."),
        };
        // Same five rows in the same order every time: swap only the ones whose value or level changed, so
        // a 1 Hz pass with nothing new costs no collection reset (UI-thread sweep 09-06). Records compare by value.
        if (HealthChecks.Count != rows.Length) { HealthChecks.Clear(); foreach (var row in rows) HealthChecks.Add(row); }
        else for (int i = 0; i < rows.Length; i++) if (HealthChecks[i] != rows[i]) HealthChecks[i] = rows[i];
    }

    /// <summary>Tooltip on the verdict lines; differs by state so the affordance never promises a problem
    /// list when there isn't one.</summary>
    public string HealthDetailTip => HealthOk
        ? "See the full breakdown of your backup's health."
        : "Click to see what is wrong and what you can do about it.";
    public bool HasCorrupt         => HealthCorruptCount > 0;
    public bool HasGone            => HealthGoneCount > 0;
    public bool HasDeviceIssue     => HealthDeviceProblemCount > 0;
    public string CorruptIssueText => $"{HealthCorruptCount} file{(HealthCorruptCount == 1 ? "" : "s")} failed verification (corrupt).";
    public string GoneIssueText    => HealthGoneCount == 1
        ? "1 backed-up file is missing from disk." : $"{HealthGoneCount} backed-up files are missing from disk.";
    public string DeviceIssueText  => $"{HealthDeviceProblemCount} backup drive{(HealthDeviceProblemCount == 1 ? " that should be connected isn't" : "s that should be connected aren't")}.";
    // Unavailable files get no issue row: not actionable and outside every completeness number. The
    // count still feeds the run summary, which reports what a run skipped.

    // Recent errors: this session's failure history. The whole section hides when there are none.
    public ObservableCollection<LogEntry> ErrorLogEntries { get; } = new();
    public bool HasErrorEntries => ErrorLogEntries.Count > 0;
    // (The collapsible Overview strip's toggle/glyph/count members went with the strip in 1044; the
    // dialog's RECENT ERRORS section is always expanded.)

    // ---- Fix confirm (owner 08-30): one click can commit hundreds of GB of re-downloads, and for
    //      files "gone" because a folder moved or was re-pointed the CHEAP cure is fixing the folder,
    //      not re-fetching. Every fix surface routes through this one dialog. ----
    [ObservableProperty] private bool _showFixConfirm;
    private string _pendingFixKind = "";
    public string FixConfirmTitle => _pendingFixKind switch
    {
        "corrupt" => "Re-download corrupt files?",
        "all"     => "Re-download corrupt and missing files?",
        _         => "Re-download missing files?",
    };
    public string FixConfirmText { get; private set; } = "";

    private void RequestFix(string kind)
    {
        if (_manifest is null) return;
        _pendingFixKind = kind;
        var affected = Grog.Core.Runs.FixRun.Select(_manifest, FixSelectorFor(kind));   // (S2.2)
        if (affected.Count == 0) return;
        long bytes = affected.Sum(a => a.File.ExpectedSizeBytes ?? 0);
        var what = $"{affected.Count} file{(affected.Count == 1 ? "" : "s")}"
                 + (bytes > 0 ? $" ({Grog.Core.Format.ByteFormat.Size(bytes)})" : "");
        FixConfirmText = kind == "corrupt"
            ? $"{what} will be re-queued at the top of the download queue and fetched fresh. The corrupt copies are replaced."
            : kind == "all"
            ? $"{what} will be re-queued at the top of the download queue and fetched fresh: corrupt copies are replaced, missing ones fetched again. "
              + "If the missing files might still exist somewhere (a folder you re-pointed or moved, another drive), Dismiss and fix the folder on the Storage page first: Grog re-adopts them without re-downloading a byte."
              + (_root.DownloadPaused ? " Downloads are paused, so nothing fetches until you Resume." : "")
            : $"If these files might still exist - a folder you re-pointed or moved, another drive - Dismiss and fix the folder on the Storage page instead: Grog re-adopts them without re-downloading a byte.\n\nOtherwise, {what} will be re-queued at the top of the download queue."
              + (_root.DownloadPaused ? " Downloads are paused, so nothing fetches until you Resume." : "");
        OnPropertyChanged(nameof(FixConfirmTitle)); OnPropertyChanged(nameof(FixConfirmText));
        ShowFixConfirm = true;
    }

    [RelayCommand] private void CancelFix() { ShowFixConfirm = false; _pendingFixKind = ""; }

    /// <summary>Once the user has ADJUDICATED a finding (fixed or acknowledged), its startup-check line
    /// leaves Recent errors (owner 08-30): the strip flags failures awaiting a decision, and a decided
    /// one lingering in red reads as an outstanding problem. The on-disk log keeps the full history.</summary>
    private void ClearResolvedErrorEntries(string kind)
    {
        var marker = kind == "corrupt" ? "failed verification" : "no longer on disk";
        for (int i = ErrorLogEntries.Count - 1; i >= 0; i--)
            if (ErrorLogEntries[i].Message.Contains(marker, System.StringComparison.OrdinalIgnoreCase))
                ErrorLogEntries.RemoveAt(i);
        OnPropertyChanged(nameof(HasErrorEntries));
    }

    [RelayCommand]
    private async Task ConfirmFix()
    {
        ShowFixConfirm = false;
        var kind = _pendingFixKind; _pendingFixKind = "";
        if (kind.Length == 0) return;
        await _root.RequeueProblemFiles(FixSelectorFor(kind), kind == "all" ? "corrupt or missing" : kind);
        if (kind is "all" or "corrupt") ClearResolvedErrorEntries("corrupt");
        if (kind is "all" or "missing") ClearResolvedErrorEntries("missing");
    }

    /// <summary>Which files a fix of <paramref name="kind"/> touches: "corrupt", "missing", or "all" (both). (S2.2)</summary>
    private static Grog.Core.Runs.FixSelector FixSelectorFor(string kind) => kind switch
    {
        "corrupt" => Grog.Core.Runs.FixSelector.Corrupt,
        "missing" => Grog.Core.Runs.FixSelector.Missing,
        _         => Grog.Core.Runs.FixSelector.CorruptOrMissing,
    };

    [RelayCommand] private void FixCorrupt() => RequestFix("corrupt");
    [RelayCommand] private void FixGone()    => RequestFix("missing");

    /// <summary>"Fix It" on the Overview lede: one click opens the fix confirm without a detour through
    /// Details, whenever anything is fixable. With both corrupt AND gone files in play it fixes both at once
    /// (owner 09-02: red text with no fix beside it is not a finding, it is a chore).</summary>
    public bool ShowLedeFixNow => HealthGoneCount > 0 || HealthCorruptCount > 0;

    /// <summary>(09-19, owner) A finding is OFFERED, not left to be noticed: when the count of missing + corrupt
    /// files RISES (startup check, a verify, an import's verify), the one fix dialog opens by itself with Fix It
    /// Now / Dismiss. Dismiss changes nothing: the red line and its Fix It pill stay on the card until the files
    /// are actually back. The watermark falls with the count, so a fixed file that breaks again is offered again.</summary>
    private int _problemsOffered;
    private void OfferFixForNewProblems()
    {
        int now = HealthGoneCount + HealthCorruptCount;
        if (now < _problemsOffered) _problemsOffered = now;
        if (now <= _problemsOffered || ShowFixConfirm) return;
        _problemsOffered = now;
        LedeFixNow();
    }

    [RelayCommand]
    private void LedeFixNow()
    {
        if (HealthGoneCount > 0 && HealthCorruptCount > 0) RequestFix("all");
        else if (HealthGoneCount > 0) RequestFix("missing");
        else if (HealthCorruptCount > 0) RequestFix("corrupt");
    }

    /// <summary>Fix flow for corrupt/gone files: mark them not-downloaded, re-queue AT THE TOP of the queue
    /// (a fix the user just clicked outranks a backlog of hundreds), and start the download - unless the
    /// queue is PAUSED, which the fix respects: it queues first and says so (owner call 08-30).</summary>
    /// <summary>"Try Again" on the last run's failure: re-queue the files that failed (strikes reset, front of the
    /// queue, handed to a live engine if one is running) and drop the failure record. It used to be bound to the
    /// transport's primary command, which mid-run is PAUSE: the first click paused the run, the second resumed
    /// it, and nothing was retried (owner-hit 09-05).</summary>
    [RelayCommand]
    private async Task RetryRunFailure()
    {
        // A scan or a move holds Busy: DownloadFiltered would refuse silently after the journal was already
        // cleared and the log said "fetching now" (review 09-06). Say so and keep the failure on the card.
        if (_root.Busy && _liveEngine is null) { ShowToast("Try again once the current task finishes.", 0); return; }
        ClearRunJournalFailure();
        // The run's failures are download failures (FixRun.IsFailedDownload: strikes on a file that is still a
        // gap; Corrupt belongs to the Fix flow). A file the live engine is mid-attempt on keeps its attempt
        // (resetting it under a worker made the manifest lie about the partial and handed out extra strikes).
        var active = _liveEngine?.Snapshot
            .Where(t => t.State == Grog.Core.Download.DownloadTaskState.Active)
            .Select(t => (t.File.GameGogId, t.File.FileKey)).ToHashSet();
        // The run's failed files BY NAME (the journal carries them since 1280), condemned ones included: after three
        // strikes a file is Corrupt and out of the queue, and the strike-only selector could not see it, so the card
        // named N files and Try Again said "No failed files to fix" (QA 09-30 C1).
        var named = _lastRunFailureRaw?.FailedFiles().Distinct() ?? Enumerable.Empty<(long, string)>();
        await _root.RequeueProblemFiles(Grog.Core.Runs.FixSelector.FailedOrKeys(named), "failed", active);   // (S2.2)
        RaiseHealth();
    }

    /// <summary>Forget the last run's failure without retrying: the lede's own verb, so the transport's Clear
    /// (which empties the queue) is never mistaken for it.</summary>
    [RelayCommand]
    private void DismissRunFailure() { ClearRunJournalFailure(); RaiseHealth(); }

    // Health drill-down: the actual files behind the corrupt/missing counts, each list expandable.
    public System.Collections.ObjectModel.ObservableCollection<HealthFileRow> HealthCorruptFiles { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<HealthFileRow> HealthGoneFiles { get; } = new();

    [ObservableProperty] private bool _corruptExpanded;
    [ObservableProperty] private bool _goneExpanded;
    public string CorruptExpandGlyph => CorruptExpanded ? "▾" : "▸";
    public string GoneExpandGlyph    => GoneExpanded ? "▾" : "▸";
    // The tip names the direction it will GO, or an expanded list looks like it has no way back.
    public string CorruptExpandTip => CorruptExpanded ? "Hide the affected files" : "Show the affected files";
    public string GoneExpandTip    => GoneExpanded ? "Hide the affected files" : "Show the affected files";
    partial void OnCorruptExpandedChanged(bool value)
    { OnPropertyChanged(nameof(CorruptExpandGlyph)); OnPropertyChanged(nameof(CorruptExpandTip)); }
    partial void OnGoneExpandedChanged(bool value)
    { OnPropertyChanged(nameof(GoneExpandGlyph)); OnPropertyChanged(nameof(GoneExpandTip)); }
    [RelayCommand] private void ToggleCorruptList() => CorruptExpanded = !CorruptExpanded;
    [RelayCommand] private void ToggleGoneList() => GoneExpanded = !GoneExpanded;

    private void RebuildHealthIssueLists()
    {
        // The rows were collected by RecomputeHealthTally's pass; the bound lists are only touched when their
        // content actually differs (UI-thread sweep 09-06) - a clear + re-add every tick reset both drill-downs.
        SyncRows(HealthCorruptFiles, _healthCorruptRows);
        SyncRows(HealthGoneFiles, _healthGoneRows);
    }

    /// <summary>Replace the collection's content with <paramref name="fresh"/> only when it differs (record
    /// value equality, same order).</summary>
    private static void SyncRows(ObservableCollection<HealthFileRow> target, List<HealthFileRow> fresh)
    {
        if (target.Count == fresh.Count)
        {
            bool same = true;
            for (int i = 0; i < fresh.Count; i++) if (target[i] != fresh[i]) { same = false; break; }
            if (same) return;
        }
        target.Clear();
        foreach (var r in fresh) target.Add(r);
    }

    [RelayCommand] private void AcknowledgeCorrupt() { AcknowledgeProblemFiles(f => f.State == FileState.Corrupt, "corrupt"); ClearResolvedErrorEntries("corrupt"); }
    [RelayCommand] private void AcknowledgeGone()    { AcknowledgeProblemFiles(f => f.State == FileState.Missing, "missing"); ClearResolvedErrorEntries("missing"); }

    /// <summary>Acknowledge &amp; Clear: reset each file to pristine not-downloaded. Clearing
    /// LocalRelativePath/RootId is essential -- the startup existence scan re-flags any file with a path.</summary>
    private void AcknowledgeProblemFiles(Func<GameFile, bool> pred, string label)
    {
        if (_manifest is null) return;
        var affected = _manifest.Current.Items
            .SelectMany(i => i.Files.Where(pred).Select(f => (item: i, file: f))).ToList();
        if (affected.Count == 0) { RaiseHealth(); return; }
        using (_manifest.Gate.Enter())   // (manifest gate 09-08)
        {
            // THE reset (GameFile.ResetLocal). The hand-rolled copy here had drifted: it kept FailedAttempts, so an
            // acknowledged corrupt file came back one failure away from being condemned again.
            foreach (var (item, f) in affected) f.ResetLocal();
            foreach (var item in affected.Select(a => a.item).Distinct())
                Grog.Core.Sync.LibrarySyncService.RecomputeStatus(item);
        }
        Runtime.ManifestSaves.Default.SaveInBackground(_manifest, "issue fix");

        Log($"Acknowledged and cleared {affected.Count} {label} file(s) - no longer counted as backed up.", category: LogCategory.Verify);
        _ = _root.ReconcileAndRefresh();
        RaiseHealth();
    }

    internal void RaiseHealth() => Services.UiWatch.Time("RaiseHealth", RaiseHealthCore);
    internal void RaiseHealthCore()
    {
        // Sub-timed (09-04 log: RaiseHealth 0.7-1.9 s per 1 Hz tick was every UI stall); the watchdog line
        // names which of these it is instead of leaving it to be guessed.
        var T = (string n, Action a) => Services.UiWatch.Time("RaiseHealth." + n, a);
        T("Tally", RecomputeHealthTally);   // one scoped tally feeds Coverage, Verified and the verdict
        T("IssueLists", RebuildHealthIssueLists);
        T("Props1", () => {
        OnPropertyChanged(nameof(CorruptExpandGlyph)); OnPropertyChanged(nameof(GoneExpandGlyph));
        OnPropertyChanged(nameof(HealthCorruptCount)); OnPropertyChanged(nameof(HealthGoneCount));
        OnPropertyChanged(nameof(HealthDeviceProblemCount)); OnPropertyChanged(nameof(HealthIssueCount));
        OnPropertyChanged(nameof(HealthOk)); OnPropertyChanged(nameof(ShowHealthBadge));
        OnPropertyChanged(nameof(HealthBadgeText)); OnPropertyChanged(nameof(HealthBadgeTip)); OnPropertyChanged(nameof(HealthDotBrush));
        OnPropertyChanged(nameof(RailLibraryCount)); OnPropertyChanged(nameof(RailLibraryTip)); OnPropertyChanged(nameof(RailPrimaryDrive));
        OnPropertyChanged(nameof(RailLibrarySub));   // (UX 09-08 #6)
        OnPropertyChanged(nameof(RailLibraryExceptions));   // (Rail exceptions 09-09)
        OnPropertyChanged(nameof(HealthVerdict)); OnPropertyChanged(nameof(HealthVerdictBrush));
        OnPropertyChanged(nameof(HealthDetail));
        });
        T("LastRun", RefreshLastRunFailure);
        T("Props2", () => { OnPropertyChanged(nameof(HasRunFailure)); OnPropertyChanged(nameof(RunFailureIssueText)); });
        T("Checks", () => RebuildHealthChecks(retally: false));   // the tally at the top of this method is current (UI-thread sweep 09-06)
        T("Props3", () => {
        OnPropertyChanged(nameof(HealthDetailTip));
        OnPropertyChanged(nameof(IssuesTriggerText)); OnPropertyChanged(nameof(HasCorrupt));
        OnPropertyChanged(nameof(HasGone)); OnPropertyChanged(nameof(HasDeviceIssue));
        OnPropertyChanged(nameof(CorruptIssueText)); OnPropertyChanged(nameof(GoneIssueText));
        OnPropertyChanged(nameof(ShowLedeFixNow));
        OfferFixForNewProblems();
        OnPropertyChanged(nameof(DeviceIssueText));
        OnPropertyChanged(nameof(HealthUnavailableCount));
        });
        T("Schedule", _root.Schedule.RaiseReadouts);   // the Overview SCHEDULE card's "ago" lines are clock-bound
        T("RunReadouts", RaiseRunReadouts);
        T("Lede", RaiseLede);   // the headline reads the tally recomputed at the top of this method
    }

    /// <summary>Copy the recent-error list to the clipboard for pasting into a bug report.</summary>
    [RelayCommand]
    private async Task CopyErrorLog()
    {
        // Masked like the on-disk log: Copy exists precisely to paste into public issues.
        var text = string.Join(System.Environment.NewLine,
            ErrorLogEntries.Select(e => $"{e.Time:yyyy-MM-dd HH:mm:ss} [{e.Category}] {_root.MaskAccountNames(e.Message)}"));
        if (string.IsNullOrEmpty(text)) return;
        var clipboard = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } w }
            ? w.Clipboard : null;
        if (clipboard is null) return;
        try { await clipboard.SetTextAsync(text); Log($"Copied {ErrorLogEntries.Count} error line(s) to the clipboard."); }
        catch { /* clipboard can be held by another app; not worth an alarm */ }
    }
}

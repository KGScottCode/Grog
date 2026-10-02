// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using Grog.Core.Download;
using Grog.Core.Models;
using Grog.Core.Scheduling;
using Grog.Core.Sync;
using Grog.Core.Verify;
using Grog.Core.Volumes;

namespace Grog.Core.Runs;

/// <summary>When the run refreshes the catalog before downloading.</summary>
public enum ScanPolicy
{
    /// <summary>Always scan first (the CLI's nightly `backup`).</summary>
    Always,
    /// <summary>Scan unless one completed within <see cref="BackupRunOptions.StaleWindow"/> (the App's Back Up button).</summary>
    IfStale,
    /// <summary>Never scan (a resume, a per-file download, the CLI `download` verb).</summary>
    Never,
}

/// <summary>
/// Everything that narrows or shapes one backup run. The App and the CLI build one of these from their own
/// inputs (buttons and chips, or flags); the decisions those inputs feed are made in <see cref="BackupRun"/>,
/// once, for both. Every field here is a SELECTION or a POLICY; nothing about presentation.
/// </summary>
public sealed record BackupRunOptions(
    ScanPolicy Scan = ScanPolicy.IfStale,
    /// <summary>How recent a completed scan must be to skip the refresh; null = 5 minutes.</summary>
    TimeSpan? StaleWindow = null,
    /// <summary>Drain the persisted queue in ITS order (a resume / launch drain), instead of selecting from the library.</summary>
    bool FromPersistedQueue = false,
    /// <summary>Only files GOG has superseded.</summary>
    bool UpdatesOnly = false,
    /// <summary>Lift Corrupt files back to NotBackedUp (strikes cleared) so they are selected again.</summary>
    bool RetryFailed = false,
    /// <summary>Keep only the N largest selected files (testing large transfers).</summary>
    int? KeepLargest = null,
    /// <summary>Restrict to these games.</summary>
    IReadOnlySet<long>? OnlyGameIds = null,
    /// <summary>The scope to select against; null = the manifest's saved scope.</summary>
    Scope? ScopeOverride = null,
    /// <summary>The CLI's flag scope (types, OS priority, title regex, blacklist); narrows further.</summary>
    DownloadScope? RunScope = null,
    /// <summary>Lift the manifest's saved scope entirely (CLI --ignore-saved-scope).</summary>
    bool IgnoreSavedScope = false,
    /// <summary>A VIEW filter (the Library's content chips, the legacy-movies toggle). Null = unattended: whole scope.</summary>
    Func<LibraryItem, GameFile, bool>? ViewFilter = null,
    /// <summary>Run the size verify pass after downloading (the CLI `backup` default).</summary>
    bool VerifyAfter = false,
    /// <summary>(09-20, owner) "Back up these games" and "download these next" are two intentions. True (the default,
    /// and the CLI's): the picked games' files go to the FRONT of the queue. False: they join the END of the queue
    /// and the run works the whole queue in its order; queue order is the user's.</summary>
    bool PromotePicked = true,
    /// <summary>Fixed worker count (CLI --parallel); null = ask the host per engine.</summary>
    int? FixedConcurrency = null,
    /// <summary>Per-device write timing shared across runs; when set, a device whose flushes run slow takes
    /// one file at a time. Hosts pass it when the worker count is automatic (App Auto, CLI without --parallel)
    /// and leave it null when the user typed a number: an explicit count is the user's call.</summary>
    DeviceWriteMonitor? DeviceMonitor = null,
    long BytesPerSecondLimit = 0,
    /// <summary>The run journal's command name.</summary>
    string JournalCommand = "backup",
    /// <summary>Run even while the queue is paused (the CLI's explicit `resume`, a user pressing Back Up). Default
    /// false: a paused queue ends the run as Skipped with <see cref="BackupRunResult.Reason"/> set.</summary>
    bool IgnorePause = false)
{
    public static readonly TimeSpan DefaultStaleWindow = TimeSpan.FromMinutes(5);
}

/// <summary>What a run did, as data. Hosts render it; the outcome is what the schedule stamps.</summary>
public sealed record BackupRunResult(
    ScheduleRunOutcome Outcome,
    SyncResult? Scan,
    IReadOnlyList<AccountSyncPass>? Passes,
    int Queued,
    int HeldNoSpace,
    long HeldNoSpaceBytes,
    Capacity.FitResult? Fit,
    int Completed,
    int Failed,
    int Retrying,
    int Refused,
    int Skipped,
    VerifyResult? Verify,
    string? JournalPath,
    Exception? Error)
{
    /// <summary>(09-25) Files stopped because their storage went away: waiting for it, not failed.</summary>
    public int DriveOffline { get; init; }
    /// <summary>Why a Skipped run never started, in words a host can print ("The download queue is paused.").</summary>
    public string? Reason { get; init; }
    /// <summary>The run ended because the persisted queue is paused and the caller did not ask to ignore that.</summary>
    public bool QueuePaused { get; init; }
    public bool Canceled => Outcome == ScheduleRunOutcome.Canceled;
    /// <summary>Nothing selected at all (not the same as everything held for space, nor a run stopped early).</summary>
    public bool NothingToDo => Queued == 0 && HeldNoSpace == 0 && Error is null && !Canceled;
    /// <summary>Every selected file was held for space: the "no room at all" case.</summary>
    public bool NothingFits => Queued == 0 && HeldNoSpace > 0;
    /// <summary>Every file that did not land and is not excused (refused, skipped, held): what a caller's exit
    /// code or "N failed" line reports. Condemned and will-retry alike.</summary>
    public int NotLanded => Failed + Retrying;
}

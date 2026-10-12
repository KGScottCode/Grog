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

public enum ScanPolicy
{
    Always,
    IfStale,
    Never,
}

public sealed record BackupRunOptions(
    ScanPolicy Scan = ScanPolicy.IfStale,
    TimeSpan? StaleWindow = null,
    bool FromPersistedQueue = false,
    bool UpdatesOnly = false,
    bool RetryFailed = false,
    int? KeepLargest = null,
    IReadOnlySet<long>? OnlyGameIds = null,
    Scope? ScopeOverride = null,
    DownloadScope? RunScope = null,
    bool IgnoreSavedScope = false,
    Func<LibraryItem, GameFile, bool>? ViewFilter = null,
    bool VerifyAfter = false,
    bool PromotePicked = true,
    int? FixedConcurrency = null,
    DeviceWriteMonitor? DeviceMonitor = null,
    long BytesPerSecondLimit = 0,
    string JournalCommand = "backup",
    bool IgnorePause = false)
{
    public static readonly TimeSpan DefaultStaleWindow = TimeSpan.FromMinutes(5);
}

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
    public int DriveOffline { get; init; }
    public string? Reason { get; init; }
    public bool QueuePaused { get; init; }
    public bool Canceled => Outcome == ScheduleRunOutcome.Canceled;
    public bool NothingToDo => Queued == 0 && HeldNoSpace == 0 && Error is null && !Canceled;
    public bool NothingFits => Queued == 0 && HeldNoSpace > 0;
    public int NotLanded => Failed + Retrying;
}

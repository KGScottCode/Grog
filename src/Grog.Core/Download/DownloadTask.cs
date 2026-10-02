// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Threading;
using Grog.Core.Models;

namespace Grog.Core.Download;

/// <summary>An item in the download queue (right-hand Process Queue panel).</summary>
public sealed class DownloadTask
{
    public Guid Id { get; } = Guid.NewGuid();
    public required GameFile File { get; init; }
    public required string GameTitle { get; init; }
    /// <summary>The .part this transfer writes, once resolved (the engine reads its real length on a stop).</summary>
    public string? PartPath { get; set; }
    /// <summary>The game's persisted slug (LibraryItem.Slug) - the folder every other subsystem plans
    /// against. Null/empty falls back to a title-derived slug (legacy callers); prefer to set it.</summary>
    public string? GameSlug { get; init; }

    /// <summary>Device this file was front-run planned onto (PlacementPlanner). When set + online, the
    /// engine writes here instead of re-deriving from routing -- this is how overflow/spill actually
    /// takes effect. Null = fall back to routing policy.</summary>
    public string? TargetRootId { get; set; }
    public long BytesReceived { get; set; }
    public long? BytesTotal { get; set; }
    public DownloadTaskState State { get; set; } = DownloadTaskState.Pending;
    public string? Error { get; set; }

    /// <summary>The actual CDN filename, set once resolved. Used for display so the label matches
    /// what lands on disk (GOG's generic Name is often just the game title).</summary>
    public string? ResolvedFileName { get; set; }
    /// <summary>Account id whose session the engine picked for this transfer (owner picker), so the log
    /// says which account fetched or failed a file. Null = legacy single-session.</summary>
    public string? DownloadedVia { get; set; }
    /// <summary>Live throughput for the active transfer, bytes/sec (rolling). 0 when idle.</summary>
    public double BytesPerSecond { get; set; }

    /// <summary>After the last byte: the tail of a transfer that is not network at all. Flushing to the drive
    /// (a USB stick with write-through can take many seconds for a few hundred MB) and hashing the file read
    /// back from it. Null while bytes are still arriving. Rows show it so "100% and stuck" names its cause.</summary>
    public string? FinishingPhase { get; set; }
    /// <summary>Seconds the drive took to accept the last bytes (flush to disk); part of the phase record.</summary>
    public double FlushSeconds { get; set; }
    /// <summary>One line per finished file: how long each phase took. Hosts log it so a slow device is
    /// a number in the record rather than a feeling about a progress bar.</summary>
    public string? PhaseSummary { get; set; }
    /// <summary>1-based position assigned when dequeued, for "NN/total" display.</summary>
    public int Index { get; set; }
    /// <summary>Cancels THIS transfer only. Needed for per-file cancel and for pre-emption: when a file is
    /// dragged to the front of the queue, whatever is running gets bumped back to Pending. Interrupting is
    /// cheap because the partial survives in .grog-tmp and resumes by HTTP range.</summary>
    public CancellationTokenSource? Cancellation { get; set; }
    /// <summary>Set while a task is being pre-empted, so the worker knows to requeue it rather than
    /// record it as canceled by the user.</summary>
    public bool PreemptRequested { get; set; }
    /// <summary>GOG's explanation when it REFUSED to serve this file to the account (entitlement, dead
    /// catalog entry). Non-null means the failure is permanent from our side: the settling code records
    /// <see cref="FileState.Unavailable"/> and must NOT count a retry strike, because no number of
    /// attempts can change GOG's answer. Null for every ordinary (retryable) failure.</summary>
    public string? UnavailableReason { get; set; }

    /// <summary>The transfer died because the DRIVE could not take the bytes (disk full, or a file over the
    /// filesystem's per-file ceiling). Not the file's fault: settled with no strike and flagged won't-fit.</summary>
    public bool DiskFull { get; set; }
    /// <summary>(09-25) Stopped because its storage went away (unplugged mid-transfer, or gone before it started).
    /// Not a failure: no strike, it stays queued and waits for the drive. Set before State, like DiskFull.</summary>
    public bool DriveGone { get; set; }
    /// <summary>What kind of failure this was (typed, at the catch). Set before State, like Error.</summary>
    public DownloadFailureKind FailureKind { get; set; }

    /// <summary>This transfer passed the engine's real-size room check: its remaining bytes are a true figure.</summary>
    public bool RoomChecked { get; set; }
    /// <summary>Why this file was SKIPPED without an attempt: every owner of the file is signed out
    /// ("no key to the door today"). Not a failure -- GOG refused nothing and no bytes were tried --
    /// so settling must not count a retry strike and must not mark Unavailable; the file simply waits
    /// for an owner to sign back in. Null for every task that actually ran.</summary>
    public string? SkipReason { get; set; }
    // Capped at 100: a file bigger than its advertised length (GOG under-reports some) must not
    // render 104% while the tail arrives.
    public double PercentComplete => BytesTotal is { } t && t > 0 ? Math.Min(100.0, 100.0 * BytesReceived / t) : 0;
}

public enum DownloadTaskState { Pending, Active, Paused, Completed, Failed, Canceled, Skipped }

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Threading;
using Grog.Core.Models;

namespace Grog.Core.Download;

public sealed class DownloadTask
{
    public Guid Id { get; } = Guid.NewGuid();
    public required GameFile File { get; init; }
    public required string GameTitle { get; init; }
    public string? PartPath { get; set; }
    public string? GameSlug { get; init; }

    public string? TargetRootId { get; set; }
    public long BytesReceived { get; set; }
    public long? BytesTotal { get; set; }
    public DownloadTaskState State { get; set; } = DownloadTaskState.Pending;
    public string? Error { get; set; }

    public string? ResolvedFileName { get; set; }
    public string? DownloadedVia { get; set; }
    public double BytesPerSecond { get; set; }
    public double? SecondsLeft { get; set; }

    public string? FinishingPhase { get; set; }
    public double FlushSeconds { get; set; }
    public string? PhaseSummary { get; set; }
    public int Index { get; set; }
    public CancellationTokenSource? Cancellation { get; set; }
    public bool PreemptRequested { get; set; }
    public string? UnavailableReason { get; set; }

    public bool DiskFull { get; set; }
    public bool DriveGone { get; set; }
    public DownloadFailureKind FailureKind { get; set; }

    public bool RoomChecked { get; set; }
    public string? SkipReason { get; set; }
    // Capped at 100: a file bigger than its advertised length (GOG under-reports some) must not
    // render 104% while the tail arrives.
    public double PercentComplete => BytesTotal is { } t && t > 0 ? Math.Min(100.0, 100.0 * BytesReceived / t) : 0;
}

public enum DownloadTaskState { Pending, Active, Paused, Completed, Failed, Canceled, Skipped }

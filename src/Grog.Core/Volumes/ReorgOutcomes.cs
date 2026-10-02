// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace Grog.Core.Volumes;

/// <summary>How a run ended.</summary>
public enum ReorgOutcome { Completed = 0, CompletedWithErrors = 1, Paused = 2, [JsonStringEnumMemberName("Cancelled")] Canceled = 3 }

/// <summary>Snapshot pushed to listeners (GUI checklist + CLI progress line) as the job advances.
/// Progress attaches to the object at every level, per design S5: this file, and the whole job.</summary>
public sealed record ReorgProgress(
    int FilesDone,
    int FilesTotal,
    long BytesDone,
    long BytesTotal,
    string? CurrentTitle,
    string? CurrentFromRel,
    string? CurrentToRel,
    double CurrentFileFraction,
    long CurrentCopiedBytes = 0,
    long CurrentSizeBytes = 0,
    string? CurrentToRootId = null);

/// <summary>Result of a run.</summary>
public sealed class ReorgResult
{
    public ReorgOutcome Outcome { get; set; }
    public int Moved { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    /// <summary>Why the runner paused ITSELF (a drive went away, failures in a row); null for a user pause.</summary>
    public string? PauseReason { get; set; }
    /// <summary>The root (destination or source) whose drive was not reachable when the runner paused itself;
    /// the host resumes the move on its own when that drive is back (walk 5.6).</summary>
    public string? PausedForRootId { get; set; }
}

/// <summary>What came of putting a stopped job's files back. <see cref="Unrecoverable"/> counts files that
/// were no longer at the destination to move back (deleted by hand, drive pulled) -- they are the ones the
/// user still has to deal with, so the count is reported rather than folded into failures.</summary>
public sealed class ReorgRevertResult
{
    public int Restored { get; set; }
    public int Failed { get; set; }
    public int Unrecoverable { get; set; }

    /// <summary>The user stopped the revert part-way. The journal is kept in this case, because files are
    /// still in the destination and the plan is the only record of where they belong.</summary>
    public bool Stopped { get; set; }

    public bool Clean => Failed == 0 && Unrecoverable == 0 && !Stopped;
}

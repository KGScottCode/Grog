// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace Grog.Core.Volumes;

public enum ReorgOutcome { Completed = 0, CompletedWithErrors = 1, Paused = 2, [JsonStringEnumMemberName("Cancelled")] Canceled = 3 }

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

public sealed class ReorgResult
{
    public ReorgOutcome Outcome { get; set; }
    public int Moved { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public string? PauseReason { get; set; }
    public string? PausedForRootId { get; set; }
}

public sealed class ReorgRevertResult
{
    public int Restored { get; set; }
    public int Failed { get; set; }
    public int Unrecoverable { get; set; }

    public bool Stopped { get; set; }

    public bool Clean => Failed == 0 && Unrecoverable == 0 && !Stopped;
}

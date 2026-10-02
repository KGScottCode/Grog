// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Text.Json.Serialization;

namespace Grog.Core.Scheduling;

/// <summary>How a backup run ended; one word per concept, reusing the app's existing vocabulary. Also the run
/// journal's on-disk vocabulary -- the state word is the enum member's name.</summary>
public enum ScheduleRunOutcome
{
    /// <summary>Reached the end of its queue with nothing left behind.</summary>
    Completed,
    /// <summary>Reached the end, but files failed or were skipped along the way.</summary>
    Partial,
    /// <summary>The run itself errored out.</summary>
    Failed,
    /// <summary>The user stopped it (Stop / Clear queue).</summary>
    // JSON name pinned to the pre-09-01 spelling so saved schedules and run journals still read.
    [JsonStringEnumMemberName("Cancelled")] Canceled,
    /// <summary>Never started: no account was signed in when the slot came round.</summary>
    Skipped,
    /// <summary>The process exited while the run was still going (journal closed without a finish).</summary>
    Interrupted,
}

/// <summary>The outcome's grade, decided once in Core; the App maps severity to brush (SeverityBrush.For) and
/// wording (ScheduleRunCopy) in exactly one place each.</summary>
public static class ScheduleRunOutcomeInfo
{
    /// <summary>Manage by exception: Completed is Quiet (plain ink, no green), anything that left work behind
    /// is Attention, only an errored run is a Fault.</summary>
    public static Grog.Core.Sync.Severity SeverityOf(ScheduleRunOutcome o) => o switch
    {
        ScheduleRunOutcome.Completed => Grog.Core.Sync.Severity.Quiet,
        ScheduleRunOutcome.Failed    => Grog.Core.Sync.Severity.Fault,
        _                            => Grog.Core.Sync.Severity.Attention,
    };
}

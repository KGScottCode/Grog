// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Grog.Core.Scheduling;

namespace Grog.App.ViewModels;

/// <summary>The user-facing wording for a run outcome. Presentation only -- the grade lives in Core
/// (ScheduleRunOutcomeInfo.SeverityOf) and the brush in SeverityBrush.For, so each layer owns its half.</summary>
public static class ScheduleRunCopy
{
    /// <summary>The word shown on the SCHEDULE card.</summary>
    public static string Label(ScheduleRunOutcome o) => o switch
    {
        ScheduleRunOutcome.Completed   => "Completed",
        ScheduleRunOutcome.Partial     => "Partial",
        ScheduleRunOutcome.Failed      => "Failed",
        ScheduleRunOutcome.Canceled   => "Canceled",
        ScheduleRunOutcome.Skipped     => "Skipped",
        ScheduleRunOutcome.Interrupted => "Interrupted",
        _                              => "Unknown",
    };

    /// <summary>The overdue-on-launch sentence's tail, given what (if anything) ended at that slot. Null means
    /// nothing ended there, so the honest reading is still "Grog wasn't running".</summary>
    public static string? OverdueBecause(ScheduleRunOutcome o) => o switch
    {
        ScheduleRunOutcome.Canceled   => "but you stopped it before it finished.",
        ScheduleRunOutcome.Skipped     => "but no account was signed in, so it was skipped.",
        ScheduleRunOutcome.Failed      => "but the run failed before it finished.",
        ScheduleRunOutcome.Partial     => "but some files were left behind.",
        ScheduleRunOutcome.Interrupted => "but it was interrupted before it finished.",
        _                              => null,
    };
}

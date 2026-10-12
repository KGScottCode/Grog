// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Grog.Core.Scheduling;

namespace Grog.App.ViewModels;

public static class ScheduleRunCopy
{
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

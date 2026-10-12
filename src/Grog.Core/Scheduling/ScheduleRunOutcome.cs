// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Text.Json.Serialization;

namespace Grog.Core.Scheduling;

public enum ScheduleRunOutcome
{
    Completed,
    Partial,
    Failed,
    [JsonStringEnumMemberName("Cancelled")] Canceled,
    Skipped,
    Interrupted,
}

public static class ScheduleRunOutcomeInfo
{
    public static Grog.Core.Sync.Severity SeverityOf(ScheduleRunOutcome o) => o switch
    {
        ScheduleRunOutcome.Completed => Grog.Core.Sync.Severity.Quiet,
        ScheduleRunOutcome.Failed    => Grog.Core.Sync.Severity.Fault,
        _                            => Grog.Core.Sync.Severity.Attention,
    };
}

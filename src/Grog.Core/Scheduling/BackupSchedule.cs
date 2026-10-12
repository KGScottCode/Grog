// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;

namespace Grog.Core.Scheduling;

public enum ScheduleFrequency { Daily, Weekly, Monthly, Custom }

public sealed class BackupSchedule
{
    public bool Enabled { get; set; }

    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Daily;

    public int TimeOfDayMinutes { get; set; } = 3 * 60;

    public DayOfWeek WeeklyDay { get; set; } = DayOfWeek.Sunday;

    public int MonthlyDay { get; set; } = 1;

    public int IntervalWeeks { get; set; } = 2;

    public List<DayOfWeek> CustomDays { get; set; } = new();

    public DateTimeOffset? AnchorDate { get; set; }

    public DateTimeOffset? LastRun { get; set; }

    public DateTimeOffset? LastAttempt { get; set; }

    public ScheduleRunOutcome? LastAttemptOutcome { get; set; }
}

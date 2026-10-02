// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;

namespace Grog.Core.Scheduling;

/// <summary>How often an automatic backup repeats. There is deliberately no "does not repeat" member:
/// that state IS <see cref="BackupSchedule.Enabled"/> = false (the scheduler is simply off).</summary>
public enum ScheduleFrequency { Daily, Weekly, Monthly, Custom }

/// <summary>Persisted automatic-backup schedule: Daily / Weekly-on-a-day / Monthly-on-a-date / Custom (every
/// N weeks on chosen weekdays), no "Ends" -- it runs indefinitely. Fires only while Grog is running or in
/// the tray; it is not an OS task.</summary>
public sealed class BackupSchedule
{
    /// <summary>Master switch. False (the default) means "does not repeat" -- the scheduler is off.</summary>
    public bool Enabled { get; set; }

    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Daily;

    /// <summary>Local time of day to run, as minutes past midnight (default 03:00).</summary>
    public int TimeOfDayMinutes { get; set; } = 3 * 60;

    /// <summary>Weekly: which weekday to run on.</summary>
    public DayOfWeek WeeklyDay { get; set; } = DayOfWeek.Sunday;

    /// <summary>Monthly: which day of the month (1-31); clamped to the month's length so "31" still fires
    /// on the last day of shorter months.</summary>
    public int MonthlyDay { get; set; } = 1;

    /// <summary>Custom: repeat every N weeks.</summary>
    public int IntervalWeeks { get; set; } = 2;

    /// <summary>Custom: the weekdays to run on within an active week.</summary>
    public List<DayOfWeek> CustomDays { get; set; } = new();

    /// <summary>Reference point for the Custom every-N-weeks cadence (set when the schedule is enabled).
    /// Which weeks are "active" is measured relative to this anchor's week.</summary>
    public DateTimeOffset? AnchorDate { get; set; }

    /// <summary>When the scheduler last kicked off a run (for display + so a run isn't double-counted).</summary>
    public DateTimeOffset? LastRun { get; set; }

    /// <summary>When a run last ENDED, however it ended. Distinct from <see cref="LastRun"/>: a canceled or
    /// skipped slot stamps an attempt and no completion, so the overdue banner can tell "user aborted" from
    /// "Grog wasn't running".</summary>
    public DateTimeOffset? LastAttempt { get; set; }

    /// <summary>How that attempt ended. Null means no run has ended since this schedule existed.</summary>
    public ScheduleRunOutcome? LastAttemptOutcome { get; set; }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;

namespace Grog.Core.Scheduling;

/// <summary>Pure next-fire computation for a <see cref="BackupSchedule"/> -- no timers, no I/O, so it's
/// trivially testable. Everything is computed in the supplied instant's local offset.</summary>
public static class SchedulePlanner
{
    /// <summary>The next instant the schedule should fire strictly after <paramref name="now"/>, or null
    /// when the schedule is off (or a Custom schedule has no weekdays selected).</summary>
    public static DateTimeOffset? NextFire(BackupSchedule? s, DateTimeOffset now)
    {
        if (s is null || !s.Enabled) return null;
        var time = TimeSpan.FromMinutes(Math.Clamp(s.TimeOfDayMinutes, 0, 24 * 60 - 1));
        return s.Frequency switch
        {
            ScheduleFrequency.Daily   => NextDaily(now, time),
            ScheduleFrequency.Weekly  => NextOnWeekdays(now, time, new[] { s.WeeklyDay }, 1, now),
            ScheduleFrequency.Monthly => NextMonthly(now, time, s.MonthlyDay),
            ScheduleFrequency.Custom  => s.CustomDays.Count == 0 ? null
                : NextOnWeekdays(now, time, s.CustomDays, Math.Max(1, s.IntervalWeeks), s.AnchorDate ?? now),
            _ => null,
        };
    }

    /// <summary>The most recent instant the schedule was due at or before <paramref name="now"/>, or null
    /// when off (or a Custom schedule has no weekdays). Used at launch to detect a run that was missed
    /// while the app was closed -- if the last backup predates this instant, we're overdue.</summary>
    public static DateTimeOffset? PreviousFire(BackupSchedule? s, DateTimeOffset now)
    {
        if (s is null || !s.Enabled) return null;
        var time = TimeSpan.FromMinutes(Math.Clamp(s.TimeOfDayMinutes, 0, 24 * 60 - 1));
        return s.Frequency switch
        {
            ScheduleFrequency.Daily   => PrevDaily(now, time),
            ScheduleFrequency.Weekly  => PrevOnWeekdays(now, time, new[] { s.WeeklyDay }, 1, now),
            ScheduleFrequency.Monthly => PrevMonthly(now, time, s.MonthlyDay),
            ScheduleFrequency.Custom  => s.CustomDays.Count == 0 ? null
                : PrevOnWeekdays(now, time, s.CustomDays, Math.Max(1, s.IntervalWeeks), s.AnchorDate ?? now),
            _ => null,
        };
    }

    /// <summary>Does a backup stamped at <paramref name="lastRun"/> cover the slot due at <paramref name="slot"/>?
    /// A run at or after the slot's time covers it, so the scheduler skips a redundant second run. The stamp
    /// must not be in the future -- a stamp written under a wrong clock is not evidence anything ran.</summary>
    public static bool SlotAlreadyCovered(DateTimeOffset? lastRun, DateTimeOffset slot, DateTimeOffset now) =>
        lastRun is { } last && last >= slot && last <= now;

    private static DateTimeOffset PrevDaily(DateTimeOffset now, TimeSpan time)
    {
        // The neighbouring day goes through MakeAt too: AddDays on a DateTimeOffset keeps today's UTC offset,
        // which is an hour wrong on the far side of a DST change.
        var today = MakeAt(now, now.Date, time);
        return today <= now ? today : MakeAt(now, now.Date.AddDays(-1), time);
    }

    private static DateTimeOffset? PrevOnWeekdays(DateTimeOffset now, TimeSpan time,
        IEnumerable<DayOfWeek> days, int intervalWeeks, DateTimeOffset anchor)
    {
        var set = new HashSet<DayOfWeek>(days);
        if (set.Count == 0) return null;
        int horizon = intervalWeeks * 7 + 8;
        for (int i = 0; i < horizon; i++)
        {
            var date = now.Date.AddDays(-i);
            if (!set.Contains(date.DayOfWeek)) continue;
            if (!WeekActive(date, anchor.Date, intervalWeeks)) continue;
            var cand = MakeAt(now, date, time);
            if (cand <= now) return cand;
        }
        return null;
    }

    private static DateTimeOffset PrevMonthly(DateTimeOffset now, TimeSpan time, int monthlyDay)
    {
        monthlyDay = Math.Clamp(monthlyDay, 1, 31);
        for (int m = 0; m < 13; m++)
        {
            var first = new DateTime(now.Year, now.Month, 1).AddMonths(-m);
            int day = Math.Min(monthlyDay, DateTime.DaysInMonth(first.Year, first.Month));
            var cand = MakeAt(now, new DateTime(first.Year, first.Month, day), time);
            if (cand <= now) return cand;
        }
        return MakeAt(now, now.Date, time).AddMonths(-1);   // unreachable in practice
    }

    /// <summary>Test seam: the zone whose rules stamp every slot. Local outside the suite.</summary>
    internal static TimeZoneInfo Zone = TimeZoneInfo.Local;

    private static DateTimeOffset MakeAt(DateTimeOffset refNow, DateTime date, TimeSpan time)
    {
        // Use the local offset FOR THE TARGET DATE, not refNow's: GetUtcOffset(wall) picks the right offset
        // across DST boundaries, so the backup keeps its intended local hour year-round.
        var wall = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified) + time;
        var offset = Zone.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset);
    }

    private static DateTimeOffset NextDaily(DateTimeOffset now, TimeSpan time)
    {
        var today = MakeAt(now, now.Date, time);
        return today > now ? today : MakeAt(now, now.Date.AddDays(1), time);
    }

    private static DateTimeOffset? NextOnWeekdays(DateTimeOffset now, TimeSpan time,
        IEnumerable<DayOfWeek> days, int intervalWeeks, DateTimeOffset anchor)
    {
        var set = new HashSet<DayOfWeek>(days);
        if (set.Count == 0) return null;
        int horizon = intervalWeeks * 7 + 8;   // always enough to reach the next active matching day
        for (int i = 0; i < horizon; i++)
        {
            var date = now.Date.AddDays(i);
            if (!set.Contains(date.DayOfWeek)) continue;
            if (!WeekActive(date, anchor.Date, intervalWeeks)) continue;
            var cand = MakeAt(now, date, time);
            if (cand > now) return cand;
        }
        return null;
    }

    /// <summary>True when <paramref name="date"/>'s week is an "active" week under an every-N-weeks cadence
    /// measured from the anchor's week. Interval 1 makes every week active.</summary>
    private static bool WeekActive(DateTime date, DateTime anchor, int intervalWeeks)
    {
        if (intervalWeeks <= 1) return true;
        int weeks = (int)Math.Floor((StartOfWeek(date) - StartOfWeek(anchor)).TotalDays / 7.0);
        int mod = ((weeks % intervalWeeks) + intervalWeeks) % intervalWeeks;
        return mod == 0;
    }

    private static DateTime StartOfWeek(DateTime d)   // Monday-based week start
    {
        int diff = ((int)d.DayOfWeek + 6) % 7;        // Monday -> 0 ... Sunday -> 6
        return d.Date.AddDays(-diff);
    }

    private static DateTimeOffset NextMonthly(DateTimeOffset now, TimeSpan time, int monthlyDay)
    {
        monthlyDay = Math.Clamp(monthlyDay, 1, 31);
        for (int m = 0; m < 13; m++)
        {
            var first = new DateTime(now.Year, now.Month, 1).AddMonths(m);
            int day = Math.Min(monthlyDay, DateTime.DaysInMonth(first.Year, first.Month));
            var cand = MakeAt(now, new DateTime(first.Year, first.Month, day), time);
            if (cand > now) return cand;
        }
        return MakeAt(now, now.Date, time).AddMonths(1);   // unreachable in practice
    }
}

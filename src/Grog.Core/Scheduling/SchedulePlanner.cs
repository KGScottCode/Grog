// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;

namespace Grog.Core.Scheduling;

public static class SchedulePlanner
{
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

    public static bool SlotAlreadyCovered(DateTimeOffset? lastRun, DateTimeOffset slot, DateTimeOffset now) =>
        lastRun is { } last && last >= slot && last <= now;

    private static DateTimeOffset PrevDaily(DateTimeOffset now, TimeSpan time)
    {
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

    internal static TimeZoneInfo Zone = TimeZoneInfo.Local;

    private static DateTimeOffset MakeAt(DateTimeOffset refNow, DateTime date, TimeSpan time)
    {
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

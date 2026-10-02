// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Linq;
using Grog.Core.Scheduling;
using Grog.Core.Tests.Framework;

/// <summary>
/// When the scheduler is due to fire. Pure arithmetic over a supplied instant, so all of it is
/// testable without a timer.
///
/// <para>WHY THESE ARE PLATFORM-INTERESTING: <c>SchedulePlanner.MakeAt</c> asks
/// <c>TimeZoneInfo.Local.GetUtcOffset</c> for the offset that applies on the TARGET date, and that
/// answer comes from the Windows registry on one machine and from tzdata on the other. A backup
/// scheduled for 03:00 that quietly fires at 02:00 or 04:00 for half the year is exactly the kind of
/// bug that only shows up in November. The DST tests below adapt to whatever zone the suite runs in,
/// so running the suite under TZ=America/New_York (or any DST zone) exercises the tzdata path.</para>
/// </summary>
[NewBatch]
[Trait("schedule")]
public class SchedulePlannerTests
{
    private const int ThreeAm = 3 * 60;

    private static BackupSchedule Daily(int minutes = ThreeAm) =>
        new() { Enabled = true, Frequency = ScheduleFrequency.Daily, TimeOfDayMinutes = minutes };

    /// <summary>A local instant, stamped with the offset the LOCAL zone really uses then. Building the
    /// input any other way would test the test rather than the planner.</summary>
    private static DateTimeOffset Local(int y, int m, int d, int hh = 12, int mm = 0, int ss = 0)
    {
        var wall = new DateTime(y, m, d, hh, mm, ss, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall));
    }

    // ---- the basics -----------------------------------------------------------------------

    [Test]
    void An_off_schedule_is_never_due()
    {
        // "Does not repeat" IS Enabled=false; there is deliberately no frequency member for it, so a
        // disabled schedule must answer null on BOTH ends rather than a date nobody will honor.
        var s = Daily(); s.Enabled = false;
        Assert.Null(SchedulePlanner.NextFire(s, Local(2026, 8, 22)), "nothing ahead");
        Assert.Null(SchedulePlanner.PreviousFire(s, Local(2026, 8, 22)), "and nothing behind");
        Assert.Null(SchedulePlanner.NextFire(null, Local(2026, 8, 22)), "and null is off, not a crash");
    }

    [Test]
    void Daily_fires_today_when_the_hour_is_still_ahead_and_tomorrow_once_it_passes()
    {
        var s = Daily();
        var beforeSlot = SchedulePlanner.NextFire(s, Local(2026, 8, 22, 1, 0))!.Value;
        Assert.Equal(22, beforeSlot.Day, "01:00 is before 03:00, so today's slot still counts");
        Assert.Equal(3, beforeSlot.Hour, "at the configured hour");

        var afterSlot = SchedulePlanner.NextFire(s, Local(2026, 8, 22, 4, 0))!.Value;
        Assert.Equal(23, afterSlot.Day, "04:00 is past it, so the next one is tomorrow");
        Assert.Equal(3, afterSlot.Hour, "still at the configured hour");
    }

    [Test]
    void The_slot_exactly_now_is_behind_us_not_ahead()
    {
        // NextFire is strictly AFTER now and PreviousFire is at-or-before. The boundary matters: the tick
        // fires when now >= next, and if NextFire returned the same instant the run would re-trigger
        // itself forever at exactly 03:00:00.
        var s = Daily();
        var atSlot = Local(2026, 8, 22, 3, 0);
        Assert.Equal(23, SchedulePlanner.NextFire(s, atSlot)!.Value.Day, "the next one is tomorrow");
        Assert.Equal(22, SchedulePlanner.PreviousFire(s, atSlot)!.Value.Day, "and this one has just come due");
    }

    [Test]
    void Weekly_lands_on_its_weekday()
    {
        var s = new BackupSchedule
        {
            Enabled = true, Frequency = ScheduleFrequency.Weekly,
            WeeklyDay = DayOfWeek.Sunday, TimeOfDayMinutes = ThreeAm,
        };
        var next = SchedulePlanner.NextFire(s, Local(2026, 8, 19))!.Value;   // a Wednesday
        Assert.Equal(DayOfWeek.Sunday, next.DayOfWeek, "the configured weekday");
        Assert.Equal(3, next.Hour, "at the configured hour");

        var prev = SchedulePlanner.PreviousFire(s, Local(2026, 8, 19))!.Value;
        Assert.Equal(DayOfWeek.Sunday, prev.DayOfWeek, "and backwards lands on it too");
        Assert.True(prev < Local(2026, 8, 19), "strictly behind us");
    }

    [Test]
    void Monthly_clamps_to_the_last_day_of_a_shorter_month()
    {
        // "The 31st" has to keep meaning something in February, or a monthly backup silently skips four
        // months of the year.
        var s = new BackupSchedule
        {
            Enabled = true, Frequency = ScheduleFrequency.Monthly,
            MonthlyDay = 31, TimeOfDayMinutes = ThreeAm,
        };
        var feb = SchedulePlanner.NextFire(s, Local(2026, 2, 1))!.Value;
        Assert.Equal(2, feb.Month, "still February");
        Assert.Equal(28, feb.Day, "clamped to the month's length, not skipped");

        var leap = SchedulePlanner.NextFire(s, Local(2028, 2, 1))!.Value;
        Assert.Equal(29, leap.Day, "and a leap year gets the 29th");
    }

    [Test]
    void Custom_with_no_days_selected_is_not_due()
    {
        // The UI can leave every weekday unticked. That is "no answer", not "every day".
        var s = new BackupSchedule { Enabled = true, Frequency = ScheduleFrequency.Custom, IntervalWeeks = 2 };
        Assert.Null(SchedulePlanner.NextFire(s, Local(2026, 8, 22)), "nothing to fire on");
        Assert.Null(SchedulePlanner.PreviousFire(s, Local(2026, 8, 22)), "in either direction");
    }

    [Test]
    void Custom_every_two_weeks_skips_the_off_week()
    {
        var anchor = Local(2026, 8, 3);   // a Monday: week 0 is active, week 1 is not
        var s = new BackupSchedule
        {
            Enabled = true, Frequency = ScheduleFrequency.Custom, IntervalWeeks = 2,
            CustomDays = new() { DayOfWeek.Monday }, AnchorDate = anchor, TimeOfDayMinutes = ThreeAm,
        };
        var next = SchedulePlanner.NextFire(s, Local(2026, 8, 4))!.Value;   // Tue of the ACTIVE week
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek, "a Monday");
        Assert.Equal(17, next.Day, "two weeks on, not the Monday in the off week (the 10th)");
    }

    // ---- "has this slot already been covered?" --------------------------------------------

    [Test]
    void A_backup_at_or_after_the_slot_covers_it_and_one_before_does_not()
    {
        var slot = Local(2026, 8, 22, 3, 0);
        var now  = Local(2026, 8, 22, 3, 0, 30);   // the tick that found the slot due

        Assert.True(SchedulePlanner.SlotAlreadyCovered(slot, slot, now), "a run exactly on the slot covers it");
        Assert.True(SchedulePlanner.SlotAlreadyCovered(slot.AddMinutes(10), slot, now.AddHours(1)),
            "and one after it covers it");
        Assert.True(!SchedulePlanner.SlotAlreadyCovered(slot.AddMinutes(-10), slot, now),
            "a run BEFORE the slot does not - that backup predates the work this slot is for");
        Assert.True(!SchedulePlanner.SlotAlreadyCovered(null, slot, now), "and never having run covers nothing");
    }

    [Test]
    void A_stamp_in_the_future_does_not_cover_anything()
    {
        // THE BUG. Both call sites tested only "lastRun >= slot". A stamp ahead of now satisfies that for
        // every slot until real time catches up, so a clock corrected BACKWARDS (a bad RTC fixed by NTP)
        // silently suppressed scheduled runs AND kept the overdue banner quiet about them - for as long as
        // the clock had been wrong. A future stamp is not a backup that happened.
        var slot = Local(2026, 8, 22, 3, 0);
        var now  = Local(2026, 8, 22, 3, 0, 30);
        var stampedUnderAWrongClock = now.AddDays(90);

        Assert.True(!SchedulePlanner.SlotAlreadyCovered(stampedUnderAWrongClock, slot, now),
            "a stamp 90 days ahead of now covers nothing");
        Assert.True(!SchedulePlanner.SlotAlreadyCovered(now.AddSeconds(1), slot, now),
            "not even one second ahead");
        Assert.True(SchedulePlanner.SlotAlreadyCovered(now, slot, now),
            "but a stamp at exactly now is fine - that is a run finishing this instant");
    }

    // ---- the platform-sensitive half ------------------------------------------------------

    /// <summary>The next DST transition at or after <paramref name="from"/> in the AMBIENT zone, or null
    /// when this zone does not observe DST (UTC, Asia/Kolkata, most of Asia and Africa).</summary>
    private static DateTime? NextTransition(DateTime from)
    {
        for (var d = from.Date; d < from.Date.AddDays(400); d = d.AddDays(1))
            if (TimeZoneInfo.Local.GetUtcOffset(d.AddHours(12)) !=
                TimeZoneInfo.Local.GetUtcOffset(d.AddDays(1).AddHours(12)))
                return d.AddDays(1);
        return null;
    }

    [Test]
    void A_daily_slot_keeps_its_LOCAL_hour_across_a_DST_boundary()
    {
        // THE regression this guards. Stamping every future slot with TODAY'S offset makes a 03:00 backup
        // fire at 02:00 or 04:00 local on the other side of a transition. The offset must change and the
        // wall-clock hour must not.
        var transition = NextTransition(new DateTime(2026, 1, 1));
        Assert.SkipUnless(transition is not null,
            $"{TimeZoneInfo.Local.Id} does not observe DST, so there is no boundary to cross");

        var s = Daily();
        var before = SchedulePlanner.NextFire(s, Local(transition!.Value.Year, transition.Value.Month, transition.Value.Day).AddDays(-3))!.Value;
        var after  = SchedulePlanner.NextFire(s, Local(transition.Value.Year, transition.Value.Month, transition.Value.Day).AddDays(3))!.Value;

        Assert.Equal(3, before.Hour, "03:00 local before the transition");
        Assert.Equal(3, after.Hour, "and still 03:00 local after it");
        Assert.True(before.Offset != after.Offset,
            $"the UTC offset must move across the boundary ({before.Offset} -> {after.Offset}); " +
            "equal offsets mean the target date's zone rules were never consulted");
    }

    [Test]
    void Daily_next_and_previous_keep_03_00_on_the_day_after_a_US_DST_switch()
    {
        // The "tomorrow" and "yesterday" slots were today's slot plus or minus 24 h with today's UTC offset,
        // so the first fire after a switch landed an hour off. Pinned to America/New_York through the planner's
        // zone seam so the case runs the same on every suite machine.
        TimeZoneInfo ny;
        try { ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch (TimeZoneNotFoundException) { Assert.Skip("America/New_York is not installed on this machine"); return; }
        var was = SchedulePlanner.Zone;
        SchedulePlanner.Zone = ny;
        try
        {
            DateTimeOffset At(int y, int m, int d, int hh)
            {
                var wall = new DateTime(y, m, d, hh, 0, 0, DateTimeKind.Unspecified);
                return new DateTimeOffset(wall, ny.GetUtcOffset(wall));
            }
            var s = Daily();
            // Spring forward: 2026-03-08 02:00 EST -> 03:00 EDT. Asked at 04:00 on the 7th, tomorrow is EDT.
            var next = SchedulePlanner.NextFire(s, At(2026, 3, 7, 4))!.Value;
            Assert.Equal(8, next.Day, "the next slot is on the switch day");
            Assert.Equal(3, next.Hour, "03:00 local, not 04:00");
            Assert.Equal(TimeSpan.FromHours(-4), next.Offset, "stamped with the switch day's EDT offset");
            // Fall back: 2026-11-01 02:00 EDT -> 01:00 EST. Asked at 01:00 on the 2nd, yesterday's slot is EST.
            var prev = SchedulePlanner.PreviousFire(s, At(2026, 11, 2, 1))!.Value;
            Assert.Equal(1, prev.Day, "the previous slot is on the switch day");
            Assert.Equal(3, prev.Hour, "03:00 local, not 02:00");
            Assert.Equal(TimeSpan.FromHours(-5), prev.Offset, "stamped with the switch day's EST offset");
            // And the other direction on each: asked at 04:00 on the switch day going into it, still 03:00.
            var prevSpring = SchedulePlanner.PreviousFire(s, At(2026, 3, 9, 1))!.Value;
            Assert.Equal(3, prevSpring.Hour, "yesterday across the spring switch is 03:00 EDT");
            var nextFall = SchedulePlanner.NextFire(s, At(2026, 10, 31, 4))!.Value;
            Assert.Equal(3, nextFall.Hour, "tomorrow across the fall switch is 03:00 EST");
        }
        finally { SchedulePlanner.Zone = was; }
    }

    [Test]
    void Every_frequency_reports_a_local_hour_that_matches_the_setting()
    {
        // Across a whole year, in whatever zone the suite is running in: the configured wall-clock time is
        // the one contract a scheduler cannot get wrong.
        var freqs = new[]
        {
            Daily(),
            new BackupSchedule { Enabled = true, Frequency = ScheduleFrequency.Weekly, WeeklyDay = DayOfWeek.Tuesday, TimeOfDayMinutes = ThreeAm },
            new BackupSchedule { Enabled = true, Frequency = ScheduleFrequency.Monthly, MonthlyDay = 15, TimeOfDayMinutes = ThreeAm },
        };
        foreach (var s in freqs)
            for (int month = 1; month <= 12; month++)
            {
                var next = SchedulePlanner.NextFire(s, Local(2026, month, 10))!.Value;
                Assert.Equal(3, next.Hour, $"{s.Frequency} in month {month} fires at 03:00 local");
                Assert.Equal(0, next.Minute, $"{s.Frequency} in month {month} fires on the hour");
            }
    }

    [Test]
    void A_half_hour_offset_zone_still_gets_the_configured_minute()
    {
        // India (+05:30) and Lord Howe (+10:30/+11:00) have offsets that are not whole hours, and Lord
        // Howe's DST step is THIRTY MINUTES. Arithmetic that assumes hour-aligned offsets survives
        // Windows testing and then drifts here.
        var s = Daily(2 * 60 + 45);   // 02:45
        for (int month = 1; month <= 12; month++)
        {
            var next = SchedulePlanner.NextFire(s, Local(2026, month, 10))!.Value;
            Assert.Equal(2, next.Hour, $"month {month} keeps the configured hour");
            Assert.Equal(45, next.Minute, $"month {month} keeps the configured minute");
        }
    }

    [Test]
    void Previous_and_next_bracket_now_with_no_gap_and_no_overlap()
    {
        // The overdue-on-launch check compares LastRun against PreviousFire. If the two ends could ever
        // cross, a launch would either miss a due slot or invent one.
        var s = Daily();
        foreach (var hour in new[] { 0, 2, 3, 4, 12, 23 })
        {
            var now = Local(2026, 8, 22, hour, 30);
            var prev = SchedulePlanner.PreviousFire(s, now)!.Value;
            var next = SchedulePlanner.NextFire(s, now)!.Value;
            Assert.True(prev <= now, $"at {hour}:30 the previous slot is behind us");
            Assert.True(next > now, $"at {hour}:30 the next slot is ahead of us");
            Assert.True(prev < next, $"at {hour}:30 they are in order");
            Assert.True((next - prev) <= TimeSpan.FromHours(25),
                $"at {hour}:30 a daily schedule leaves no more than a day between them (DST makes it 23h or 25h)");
        }
    }
}

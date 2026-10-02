// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using Grog.Core.Scheduling;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

/// <summary>
/// "Last run result": the schedule's memory of how a run ENDED, as opposed to when one last finished.
/// The rule these pin down is a honesty rule, not a formatting one -- a slot the user stopped, or one
/// skipped because nobody was signed in, must not be reported as a slot Grog slept through.
/// </summary>
[NewBatch]
[Trait("schedule")]
public class ScheduleRunOutcomeTests
{
    [Test]
    void A_finished_run_and_an_abandoned_one_are_different_facts()
    {
        // The whole reason for a second pair of fields. Before this, "did a run end here?" and "did a run
        // FINISH here?" were the same question, so a stopped run either lied about completing or vanished.
        var s = new BackupSchedule { Enabled = true };
        Assert.Null(s.LastRun, "nothing has finished");
        Assert.Null(s.LastAttemptOutcome, "and nothing has ended");

        var slot = new DateTimeOffset(2026, 8, 22, 3, 0, 0, TimeSpan.Zero);
        s.LastAttempt = slot;
        s.LastAttemptOutcome = ScheduleRunOutcome.Canceled;

        Assert.Null(s.LastRun, "a canceled run is an attempt, never a completion");
        Assert.Equal(ScheduleRunOutcome.Canceled, s.LastAttemptOutcome!.Value, "and the attempt remembers how it ended");
    }

    [Test]
    void Completed_is_reported_and_never_celebrated()
    {
        // MANAGE BY EXCEPTION, the same rule the health rows follow: green is the assumed state. A card
        // that paints "Completed" green teaches a reader that color means "a value exists", after which
        // the one amber word on the page has to compete with it.
        Assert.Equal(Severity.Quiet,
            ScheduleRunOutcomeInfo.SeverityOf(ScheduleRunOutcome.Completed),
            "Completed grades Quiet, so it draws in plain ink");
    }

    [Test]
    void Only_an_errored_run_is_a_fault()
    {
        // Canceled and Skipped left work outstanding; nothing broke and nothing was lost. Grading them
        // red would put a fault color on the user's own decision to press Stop.
        Assert.Equal(Severity.Fault,
            ScheduleRunOutcomeInfo.SeverityOf(ScheduleRunOutcome.Failed), "a run that errored is a fault");
        Assert.Equal(Severity.Attention,
            ScheduleRunOutcomeInfo.SeverityOf(ScheduleRunOutcome.Canceled), "the user stopping it is not a fault");
        Assert.Equal(Severity.Attention,
            ScheduleRunOutcomeInfo.SeverityOf(ScheduleRunOutcome.Skipped), "nor is being signed out");
        Assert.Equal(Severity.Attention,
            ScheduleRunOutcomeInfo.SeverityOf(ScheduleRunOutcome.Partial), "nor are files left behind");
    }

    [Test]
    void A_stamped_outcome_survives_a_round_trip_as_its_word()
    {
        // The manifest writes enums as strings (readable states in the file). A schedule written by this
        // build must still read back on the next one, and must not be positionally coupled to the enum.
        var s = new BackupSchedule
        {
            Enabled = true,
            LastAttempt = new DateTimeOffset(2026, 8, 22, 0, 4, 0, TimeSpan.Zero),
            LastAttemptOutcome = ScheduleRunOutcome.Skipped,
        };
        var opts = new System.Text.Json.JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var json = System.Text.Json.JsonSerializer.Serialize(s, opts);
        Assert.Contains("\"Skipped\"", json, "the file says the word, not a number");

        var back = System.Text.Json.JsonSerializer.Deserialize<BackupSchedule>(json, opts)!;
        Assert.Equal(ScheduleRunOutcome.Skipped, back.LastAttemptOutcome!.Value, "and it reads back the same");
        Assert.Equal(s.LastAttempt!.Value, back.LastAttempt!.Value, "with its time intact");
    }

    [Test]
    void A_schedule_written_before_this_feature_still_loads()
    {
        // Every existing install has a Schedule with neither field. Null must mean "no run has ended yet",
        // not a crash and not a fabricated Completed.
        var opts = new System.Text.Json.JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var old = "{\"Enabled\":true,\"Frequency\":\"Daily\",\"TimeOfDayMinutes\":180,\"LastRun\":\"2026-08-20T03:11:00+00:00\"}";
        var s = System.Text.Json.JsonSerializer.Deserialize<BackupSchedule>(old, opts)!;

        Assert.True(s.Enabled, "the schedule still loads");
        Assert.Null(s.LastAttemptOutcome, "and reports no ending rather than inventing one");
        Assert.Null(s.LastAttempt, "with no attempt time either");
    }
}

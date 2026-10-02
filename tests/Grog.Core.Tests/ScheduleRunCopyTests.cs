// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using Grog.App.ViewModels;
using Grog.Core.Scheduling;
using Grog.Core.Tests.Framework;

/// <summary>
/// The run outcome's user-facing words, which live in the App (presentation) while the grade stays in
/// Core. Same honesty rules as the Core half: one word per concept, and an aborted slot never claims
/// Grog was not running.
/// </summary>
[NewBatch]
[Trait("schedule")]
public class ScheduleRunCopyTests
{
    [Test]
    void Every_outcome_has_a_word_and_no_two_share_one()
    {
        // One word per concept. A synonym creeping in ("Stopped" beside the queue's "Canceled") is how a
        // vocabulary drifts, and the label is the ONLY text the card shows.
        var seen = new System.Collections.Generic.HashSet<string>();
        foreach (ScheduleRunOutcome o in Enum.GetValues<ScheduleRunOutcome>())
        {
            var label = ScheduleRunCopy.Label(o);
            Assert.True(label.Length > 0, $"{o} has a word");
            Assert.True(seen.Add(label), $"{o}'s word is its own: {label}");
        }
        Assert.Equal("Canceled", ScheduleRunCopy.Label(ScheduleRunOutcome.Canceled),
            "and it is the queue's word for a user stop, not a new one");
    }

    [Test]
    void An_aborted_slot_never_claims_Grog_wasnt_running()
    {
        // The bug this feature exists to kill (seen live): a user CLEARED a scheduled run, and
        // the next launch's overdue banner blamed the app being closed for a slot the user aborted with the
        // window open. Canceled and Skipped must each supply their own reason.
        Assert.Equal("but you stopped it before it finished.",
            ScheduleRunCopy.OverdueBecause(ScheduleRunOutcome.Canceled)!,
            "a stop is reported as a stop");
        Assert.Equal("but no account was signed in, so it was skipped.",
            ScheduleRunCopy.OverdueBecause(ScheduleRunOutcome.Skipped)!,
            "a signed-out slot names the real obstacle");
        Assert.True(ScheduleRunCopy.OverdueBecause(ScheduleRunOutcome.Failed)!.Length > 0,
            "a failed run says so too");
    }

    [Test]
    void A_completed_slot_offers_no_excuse_because_it_cannot_be_overdue()
    {
        // Completed stamps LastRun, and the overdue check returns before it ever reaches the wording. Null
        // is the honest answer here: there is no sentence to write, so the caller keeps its default.
        Assert.Null(ScheduleRunCopy.OverdueBecause(ScheduleRunOutcome.Completed),
            "a completed run is not an overdue one");
    }

}

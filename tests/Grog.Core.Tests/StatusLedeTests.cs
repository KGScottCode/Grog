// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

[NewBatch]
[Trait("lede")]
public class StatusLedeTests
{
    // A library that is entirely backed up, checked moments ago: the "nothing to report" baseline.
    static StatusLede.Snapshot Clean() => new(
        OfflineFolders: 0, MissingFiles: 0,
        UnprotectedBytes: 0, UnprotectedFiles: 0,
        TotalFiles: 694, ProtectedFiles: 694, ProtectedBytes: 468_000_000_000,
        OutdatedGames: 0,
        SinceCatalogCheck: TimeSpan.FromMinutes(2), StaleAfter: TimeSpan.FromDays(1));

    [Test]
    void FailedRun_outranks_the_plain_gap_it_leaves_behind()
    {
        // A run that failed for a transient reason (no space, drive pulled) can leave every file merely
        // not-downloaded, so the manifest alone reports an ordinary gap -- as though nothing had been tried.
        // That is how a backup tool silently stops backing up, so the attempt outranks the gap.
        var lede = StatusLede.Pick(new(
            OfflineFolders: 0, MissingFiles: 0,
            UnprotectedBytes: 900_000_000, UnprotectedFiles: 12,
            TotalFiles: 100, ProtectedFiles: 88, ProtectedBytes: 1,
            OutdatedGames: 0, SinceCatalogCheck: TimeSpan.FromHours(1), StaleAfter: TimeSpan.FromDays(7),
            FailedLastRun: 12, FailedReason: "There is not enough space on the disk"));

        Assert.Equal(StatusLede.Kind.RunFailed, lede.Kind, "a failed run leads over the gap");
        Assert.Equal("12", lede.Value, "the count is the value");
        Assert.Contains("couldn't be backed up", lede.Headline);
        Assert.Contains("not enough space", lede.Detail, "the reason is carried through, not swallowed");
    }

    [Test]
    void A_lost_file_still_outranks_a_failed_run()
    {
        // Losing something you HAD is worse news than failing to fetch something you never held.
        var lede = StatusLede.Pick(new(
            OfflineFolders: 0, MissingFiles: 3,
            UnprotectedBytes: 0, UnprotectedFiles: 0,
            TotalFiles: 100, ProtectedFiles: 97, ProtectedBytes: 1,
            OutdatedGames: 0, SinceCatalogCheck: TimeSpan.FromHours(1), StaleAfter: TimeSpan.FromDays(7),
            FailedLastRun: 12, FailedReason: "disk full"));

        Assert.Equal(StatusLede.Kind.MissingFiles, lede.Kind, "missing files still lead");
    }

    [Test]
 void AllClear_WhenNothingIsWrong()
    {
        var l = StatusLede.Pick(Clean());
        Assert.Equal(StatusLede.Kind.AllClear, l.Kind, "clean library reports all clear");
        Assert.True(l.Value.Contains("694"), "leads with the file count");
    }

    [Test] void Gap_LeadsWithBytesNotPercent()
    {
        var s = Clean() with { UnprotectedFiles = 636, UnprotectedBytes = 468_000_000_000, ProtectedFiles = 58 };
        var l = StatusLede.Pick(s);
        Assert.Equal(StatusLede.Kind.Gap, l.Kind, "an unfetched library leads with the gap");
        Assert.Equal("to back up", l.Headline, "the gap headline completes the number, it does not restate it");
        // The detail line was DELIBERATELY dropped: the file counts live on the completeness bars and the
        // size is the lede's own value, so restating both underneath told the same story twice.
        Assert.Equal("", l.Detail, "the gap lede carries no detail line");
    }

    [Test] void MissingFiles_OutrankTheGap()
    {
        var s = Clean() with { UnprotectedFiles = 636, UnprotectedBytes = 1, MissingFiles = 3 };
        Assert.Equal(StatusLede.Kind.MissingFiles, StatusLede.Pick(s).Kind,
            "losing what you had beats not having fetched yet");
    }

    [Test] void OfflineFolder_OutranksEverything()
    {
        var s = Clean() with { UnprotectedFiles = 636, MissingFiles = 3, OfflineFolders = 1 };
        var l = StatusLede.Pick(s);
        Assert.Equal(StatusLede.Kind.FolderOffline, l.Kind, "an absent folder is always the headline");
        Assert.True(l.Value.Contains("1 storage location"), "singular reads naturally");
    }

    [Test] void Outdated_OnlySurfacesOnceTheLibraryIsHeld()
    {
        var held = Clean() with { OutdatedGames = 12 };
        Assert.Equal(StatusLede.Kind.Outdated, StatusLede.Pick(held).Kind, "held + outdated -> outdated leads");

        var gap = held with { UnprotectedFiles = 5, UnprotectedBytes = 100 };
        Assert.Equal(StatusLede.Kind.Gap, StatusLede.Pick(gap).Kind, "an open gap still outranks staleness");
    }

    // The count comes from a version-string comparison made during a catalog sync, so it is only as fresh as
    // that sync. Once it ages out we must NOT assert it as current -- we say what we actually know.
    [Test] void StaleCheck_BeatsAssertingAnAgeingCount()
    {
        var s = Clean() with { OutdatedGames = 12, SinceCatalogCheck = TimeSpan.FromDays(4) };
        var l = StatusLede.Pick(s);
        Assert.Equal(StatusLede.Kind.Stale, l.Kind, "an old check is the headline, not the count it produced");
        Assert.True(l.Value.Contains("4 days"), "leads with how old the knowledge is");
        Assert.True(l.Detail.Contains("Rescan"), "offers the action that makes it true again");
    }

    [Test] void StaleCheck_SaysSoEvenWhenNothingWasOutOfDate()
    {
        var s = Clean() with { SinceCatalogCheck = TimeSpan.FromDays(9) };
        var l = StatusLede.Pick(s);
        Assert.Equal(StatusLede.Kind.Stale, l.Kind, "stale knowledge is reported even when the news was good");
        Assert.True(l.Detail.Contains("matched then"), "is honest that the all-clear is historical");
    }

    [Test] void NeverChecked_IsNotTreatedAsStale()
    {
        var s = Clean() with { SinceCatalogCheck = null, OutdatedGames = 0 };
        Assert.Equal(StatusLede.Kind.AllClear, StatusLede.Pick(s).Kind,
            "no check yet is not the same as an old check");
        Assert.True(!StatusLede.IsStale(null, TimeSpan.FromDays(1)), "null is never stale");
    }

    [Test] void EmptyLibrary_ReadsAsSetupNotSuccess()
    {
        var s = Clean() with { TotalFiles = 0, ProtectedFiles = 0, ProtectedBytes = 0 };
        var l = StatusLede.Pick(s);
        // The KIND is what the page colors from, so "reads as setup, not success" has to be true of the kind
        // and not only of the words. Sharing AllClear here painted "Nothing yet" in SuccessGreen, telling a user
        // with an empty library that they were protected.
        Assert.Equal(StatusLede.Kind.NotStarted, l.Kind, "an empty library has not succeeded at anything yet");
        Assert.True(l.Headline.Contains("scan your GOG library"), "an empty library is told to scan, not congratulated");
        Assert.Equal("", l.Detail, "this lede carries no detail line either");
    }

    // SEVERITY IS GRADED ONCE, and this is the pin. Every surface that colors a status asks SeverityOf; if a
    // new Kind is added and forgotten, it falls into the Attention default silently, so the fault and quiet sets
    // are asserted by NAME here rather than by "not the others".
    [Test] void Severity_FaultIsExactlyLossAndAbsence()
    {
        Assert.Equal(Severity.Fault, StatusLede.SeverityOf(StatusLede.Kind.MissingFiles),
                     "a file you had and no longer have is a fault");
        Assert.Equal(Severity.Fault, StatusLede.SeverityOf(StatusLede.Kind.FolderOffline),
                     "a folder that should be present and isn't is a fault");
    }

    [Test] void Severity_NothingDoneYetIsQuietNotRed()
    {
        // The defect this pins: an unread library was graded the same as files gone from disk, so the first
        // thing a new user saw was an error about not having started.
        Assert.Equal(Severity.Quiet, StatusLede.SeverityOf(StatusLede.Kind.NotStarted),
                     "not having looked yet is not a finding");
        Assert.Equal(Severity.Quiet, StatusLede.SeverityOf(StatusLede.Kind.AllClear),
                     "green is the assumed state and is never reported as a problem");
    }

    [Test] void Severity_OutstandingWorkIsAttention()
    {
        foreach (var k in new[] { StatusLede.Kind.Gap, StatusLede.Kind.Stale,
                                  StatusLede.Kind.Outdated, StatusLede.Kind.RunFailed })
            Assert.Equal(Severity.Attention, StatusLede.SeverityOf(k),
                         $"{k} is work left to do, not damage");
    }

    [Test] void Severity_EveryKindIsGraded()
    {
        foreach (StatusLede.Kind k in System.Enum.GetValues<StatusLede.Kind>())
        {
            var sev = StatusLede.SeverityOf(k);
            Assert.True(System.Enum.IsDefined(sev), $"{k} grades to a real severity");
        }
    }

    [Test] void Describe_PicksASensibleUnit()
    {
        Assert.Equal("4 days", StatusLede.Describe(TimeSpan.FromDays(4)), "days");
        Assert.Equal("1 day", StatusLede.Describe(TimeSpan.FromHours(30)), "singular day");
        Assert.Equal("6 hours", StatusLede.Describe(TimeSpan.FromHours(6)), "hours");
        Assert.Equal("1 minute", StatusLede.Describe(TimeSpan.FromSeconds(20)), "floors to a minute, never zero");
    }
}

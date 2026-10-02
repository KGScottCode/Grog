namespace Grog.Core.Tests;

using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

// Locks the source-of-truth invariants from the stats-drift cleanup: displayed status is a pure
// DERIVATION of files + scope (never a persisted/independent number), the tri-state survives, and byte
// tallies honor the FULL scope (language + platform), so no two surfaces can disagree about one game.
public class StatsSourceOfTruthTests
{
    private static GameFile Inst(string os, string lang, FileState state, long size = 100) =>
        new() { Kind = FileKind.Installer, Os = os, Language = lang, State = state, ExpectedSizeBytes = size };

    private static LibraryItem Game(params GameFile[] files)
    {
        var it = new LibraryItem { GogId = 1, Title = "Game", Slug = "g" };
        it.Files.AddRange(files);
        return it;
    }

    private static Scope ScopeOf(IEnumerable<string>? langs, IEnumerable<string>? plats) =>
        new(true, true, langs?.ToList(), plats?.ToList());

    // The derive-not-persist guard: the SAME item object yields two different verdicts under two scopes, with
    // no mutation and no re-sync. This is exactly the case a persisted scope-relative status would get wrong.
    [Test] void ScopeChange_FlipsStatus_WithoutMutatingItem()
    {
        var it = Game(
            Inst("windows", "English", FileState.Verified),
            Inst("windows", "Polish",  FileState.NotBackedUp));

        var en   = BackupScope.Status(it, ScopeOf(new[] { "English" }, new[] { "windows" }));
        var enpl = BackupScope.Status(it, ScopeOf(new[] { "English", "Polish" }, new[] { "windows" }));

        Assert.Equal(BackupStatus.Complete, en, "English-only: the English build is all you chose → complete");
        Assert.True(enpl is BackupStatus.Partial or BackupStatus.Missing, "add Polish and the SAME item is now incomplete");
    }

    // Language-scoped games reach 100%: excluded-language bytes must leave the denominator (the row-bar bug).
    [Test] void LanguageScope_ReachesComplete()
    {
        var it = Game(
            Inst("windows", "English", FileState.Verified, 100),
            Inst("windows", "Polish",  FileState.NotBackedUp, 900));
        var s = ScopeOf(new[] { "English" }, new[] { "windows" });

        Assert.Equal(BackupStatus.Complete, BackupScope.Status(it, s), "complete for your language");
        Assert.Equal(BackupScope.ScopedTotalBytes(it, s), BackupScope.ScopedDoneBytes(it, s), "done == total in scope → 100%");
    }

    // Out-of-platform installers don't drag a platform-scoped game (the detail-pane / Mac-showing bug's twin).
    [Test] void PlatformScope_IgnoresOtherOs()
    {
        var it = Game(
            Inst("windows", "English", FileState.Verified),
            Inst("mac",     "English", FileState.NotBackedUp));
        Assert.Equal(BackupStatus.Complete,
            BackupScope.Status(it, ScopeOf(new[] { "English" }, new[] { "windows" })),
            "Windows build is present; the Mac build isn't in your scope");
    }

    // Outdated is its OWN tri-state -- not Complete (a gap) and not Missing (you have SOMETHING). Realistic
    // shape: a present build plus a stale file pending an update.
    [Test] void UpdateAvailable_IsOutdated_NotComplete()
    {
        var it = Game(
            Inst("windows", "English", FileState.Verified),         // you have this build
            Inst("windows", "English", FileState.Outdated)); // and a stale file pending an update
        Assert.Equal(BackupStatus.Outdated,
            BackupScope.Status(it, ScopeOf(new[] { "English" }, new[] { "windows" })),
            "present + a stale file → outdated, distinct from Complete and Missing");
    }

    // P1: the per-tick optimization replaces a full-library walk with "cached base (present-full + static
    // partials, EXCLUDING in-flight files) + a live sum over the in-flight files". That must equal a full
    // Rollups.Library walk to the byte -- this locks the decomposition the fast path relies on.
    [Test] void Rollup_BasePlusInflight_EqualsFullWalk()
    {
        var it = Game(
            Inst("windows", "English", FileState.Verified, 100),        // present → counts full in the base
            Inst("windows", "English", FileState.NotBackedUp, 400));  // in-flight, 250 received so far
        var f2 = it.Files[1];
        f2.PartialBytes = 250;
        var m = new LibraryManifest(); m.Items.Add(it);
        var scope = ScopeOf(new[] { "English" }, new[] { "windows" });
        Grog.Core.Sync.Rollups.InFlightBytes live = f => ReferenceEquals(f, f2) ? 250 : 0;

        var full = Grog.Core.Sync.Rollups.Library(m, scope, live).Whole;

        long baseDone = 100;   // present file only (in-flight file excluded from the base)
        long baseTotal = 500;
        long inflightDone = Grog.Core.Sync.Rollups.DoneBytesOf(f2, live);   // 250
        Assert.Equal(baseTotal, full.TotalBytes, "total is stable (no in-flight effect)");
        Assert.Equal(baseDone + inflightDone, full.DoneBytes, "base(present) + in-flight sum == full walk");
    }

    // PendingBytes counts only in-scope missing bytes -- the 464-vs-285 GB class of bug.
    [Test] void PendingBytes_ExcludesOutOfScope()
    {
        var m = new LibraryManifest();
        m.Items.Add(Game(
            Inst("windows", "English", FileState.Verified, 100),
            Inst("windows", "Polish",  FileState.NotBackedUp, 200),
            Inst("mac",     "English", FileState.NotBackedUp, 400)));

        Assert.Equal(0L, StorageReport.PendingBytes(m, ScopeOf(new[] { "English" }, new[] { "windows" })),
            "English/Windows is fully present → nothing pending in scope");
        Assert.Equal(600L, StorageReport.PendingBytes(m, Scope.Both),
            "unscoped counts the Polish + Mac builds you didn't choose");
    }
}

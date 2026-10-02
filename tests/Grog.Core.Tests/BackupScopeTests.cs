namespace Grog.Core.Tests;

using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

public class BackupScopeTests
{
    private static LibraryItem Item(params (FileKind kind, FileState state, long size)[] files)
    {
        var it = new LibraryItem { GogId = 1, Title = "Game", Slug = "game" };
        foreach (var (kind, state, size) in files)
            it.Files.Add(new GameFile { Kind = kind, State = state, ExpectedSizeBytes = size });
        return it;
    }

    // ---- Platform is SCOPE, exactly like language ----
    private static GameFile Installer(string os) =>
        new() { Kind = FileKind.Installer, Os = os, State = FileState.NotBackedUp, ExpectedSizeBytes = 100 };

    [Test] void Platform_NoNarrowing_KeepsEverything()
    {
        var s = Scope.Both;
        Assert.True(s.AllPlatforms, "no platforms set means no narrowing");
        Assert.True(s.Includes(Installer("windows")), "windows kept");
        Assert.True(s.Includes(Installer("linux")), "linux kept");
    }

    [Test] void Platform_NarrowingDropsOthers()
    {
        var s = Scope.Both.WithPlatforms(new[] { "windows" });
        Assert.True(s.Includes(Installer("windows")), "chosen platform kept");
        Assert.False(s.Includes(Installer("mac")), "unchosen platform leaves scope entirely");
        Assert.False(s.Includes(Installer("linux")), "unchosen platform leaves scope entirely");
    }

    [Test] void Platform_UntaggedFilesAreNeutralAndAlwaysKept()
    {
        var s = Scope.Both.WithPlatforms(new[] { "linux" });
        Assert.True(s.Includes(Installer("")), "an untagged installer is a shared payload, never dropped");
        var extra = new GameFile { Kind = FileKind.Extra, Os = "", State = FileState.NotBackedUp };
        Assert.True(s.Includes(extra), "a soundtrack is not a Windows soundtrack");
    }

    [Test] void Platform_OsxNormalizesToMac()
    {
        Assert.Equal("mac", Scope.PlatformOf(Installer("osx")), "GOG's osx is our mac");
        Assert.True(Scope.Both.WithPlatforms(new[] { "mac" }).Includes(Installer("osx")), "and matches");
    }

    [Test] void Platform_LeavesTheDenominator()
    {
        // Out-of-scope platforms must not sit in the total, or 100% becomes unreachable.
        var it = new LibraryItem { GogId = 1, Title = "Game", Slug = "game" };
        it.Files.Add(Installer("windows"));
        it.Files.Add(Installer("mac"));
        var s = Scope.Both.WithPlatforms(new[] { "windows" });
        Assert.Equal(100L, BackupScope.ScopedTotalBytes(it, s), "only the chosen platform is the job");
    }

    // ---- Superseded files are OUTSTANDING WORK, not backed up ----
    // The queue selects on !IsPresent. While UpdateAvailable counted as present, an updated file could never
    // be queued: Grog could report a game as out of date and had no path to fetch it. These tests pin the
    // rule that made it actionable.

    [Test] void UpdateAvailable_IsNotPresent()
    {
        var f = new GameFile { Kind = FileKind.Installer, State = FileState.Outdated };
        Assert.False(BackupScope.IsPresent(f), "a superseded build is not the copy GOG ships");
        Assert.True(BackupScope.HasLocalCopy(f), "but its bytes are still on disk");
    }

    [Test] void Downloaded_IsBothPresentAndOnDisk()
    {
        var f = new GameFile { Kind = FileKind.Installer, State = FileState.Present };
        Assert.True(BackupScope.IsPresent(f), "downloaded is present");
        Assert.True(BackupScope.HasLocalCopy(f), "downloaded is on disk");
    }

    [Test] void NotDownloaded_IsNeither()
    {
        var f = new GameFile { Kind = FileKind.Installer, State = FileState.NotBackedUp };
        Assert.False(BackupScope.IsPresent(f), "never fetched is not present");
        Assert.False(BackupScope.HasLocalCopy(f), "never fetched holds no bytes");
    }

    [Test] void UpdateAvailable_CountsAsOutstandingBytes()
    {
        // 100 held + 400 superseded: the superseded bytes are work to do, so done is 100 of 500.
        var it = Item((FileKind.Installer, FileState.Present, 100),
                      (FileKind.Installer, FileState.Outdated, 400));
        Assert.Equal(500L, BackupScope.ScopedTotalBytes(it, Scope.Both), "total counts both files");
        Assert.Equal(100L, BackupScope.ScopedDoneBytes(it, Scope.Both),
            "a superseded file is not done -- it is the gap");
    }

    [Test] void UpdateAvailable_KeepsItsOwnProductStatus()
    {
        var it = Item((FileKind.Installer, FileState.Present, 100),
                      (FileKind.Installer, FileState.Outdated, 400));
        Assert.Equal(BackupStatus.Outdated, BackupScope.Status(it, Scope.Both),
            "the grid still says WHICH game changed, even though the file is simply outstanding");
    }

    [Test] void FullyUpdated_IsNotComplete()
    {
        var it = Item((FileKind.Installer, FileState.Outdated, 400));
        Assert.False(BackupScope.IsComplete(it, Scope.Both),
            "holding only a superseded build is not a complete backup");
    }

    // ---- Scope value type ----
    [Test] void Scope_Includes_ByKind()
    {
        Assert.True(Scope.GamesOnly.Includes(FileKind.Installer), "games includes installer");
        Assert.False(Scope.GamesOnly.Includes(FileKind.Extra), "games excludes extra");
        Assert.True(Scope.ExtrasOnly.Includes(FileKind.Extra), "extras includes extra");
        Assert.False(Scope.ExtrasOnly.Includes(FileKind.Installer), "extras excludes installer");
        Assert.True(Scope.Both.Includes(FileKind.DlcInstaller), "DLC installer counts as game");
    }

    [Test] void Scope_KeyRoundTrips()
    {
        Assert.Equal("both", Scope.Both.Key, "both key");
        Assert.Equal("games", Scope.GamesOnly.Key, "games key");
        Assert.Equal("extras", Scope.ExtrasOnly.Key, "extras key");
        Assert.Equal(Scope.GamesOnly, Scope.FromKey("games"), "parse games");
        Assert.Equal(Scope.ExtrasOnly, Scope.FromKey("extras"), "parse extras");
        Assert.Equal(Scope.Both, Scope.FromKey(null), "null -> both (default)");
    }

    // ---- Status truth table ----
    [Test] void GamesOnly_InstallerPresent_ExtraMissing_IsComplete()
        => Assert.Equal(BackupStatus.Complete,
            BackupScope.Status(Item((FileKind.Installer, FileState.Present, 100), (FileKind.Extra, FileState.NotBackedUp, 50)), Scope.GamesOnly),
            "games-only ignores the missing extra");

    [Test] void GamesAndExtras_ExtraMissing_IsPartial()
        => Assert.Equal(BackupStatus.Partial,
            BackupScope.Status(Item((FileKind.Installer, FileState.Present, 100), (FileKind.Extra, FileState.NotBackedUp, 50)), Scope.Both),
            "the missing extra now counts");

    [Test] void ExtrasOnly_ExtraPresent_InstallerMissing_IsComplete()
        => Assert.Equal(BackupStatus.Complete,
            BackupScope.Status(Item((FileKind.Installer, FileState.NotBackedUp, 100), (FileKind.Extra, FileState.Present, 50)), Scope.ExtrasOnly),
            "extras-only ignores the missing installer");

    [Test] void NothingPresent_IsNotDownloaded()
        => Assert.Equal(BackupStatus.NotBackedUp,
            BackupScope.Status(Item((FileKind.Installer, FileState.NotBackedUp, 100)), Scope.Both), "none present");

    [Test] void AllPresent_IsComplete()
        => Assert.Equal(BackupStatus.Complete,
            BackupScope.Status(Item((FileKind.Installer, FileState.Verified, 100), (FileKind.Extra, FileState.Present, 50)), Scope.Both), "all present");

    [Test] void Corrupt_IsError()
        => Assert.Equal(BackupStatus.Corrupt,
            BackupScope.Status(Item((FileKind.Installer, FileState.Corrupt, 100)), Scope.Both), "corrupt -> error");

    // Deleted: Downloading_IsDownloadingStatus. It asserted a branch that could never run in production --
    // FileState.Downloading was never assigned by any code path, so BackupScope could not observe it. The test
    // passed only because the test itself set the impossible value. Live transfer state is the download
    // queue's to report, not a rollup persisted in the manifest.

    [Test] void MissingLocally_IsMissing()
        => Assert.Equal(BackupStatus.Missing,
            BackupScope.Status(Item((FileKind.Installer, FileState.Missing, 100)), Scope.Both), "missing-locally -> missing");

    [Test] void UpdateAvailable_WhenAllPresentButOneOutdated()
        => Assert.Equal(BackupStatus.Outdated,
            BackupScope.Status(Item((FileKind.Installer, FileState.Verified, 100), (FileKind.Patch, FileState.Outdated, 20)), Scope.Both), "update available");

    [Test] void OutOfScopeOnly_IsComplete_NothingToDo()
        => Assert.Equal(BackupStatus.Complete,
            BackupScope.Status(Item((FileKind.Extra, FileState.NotBackedUp, 10)), Scope.GamesOnly), "nothing in scope -> nothing to do -> complete");

    [Test] void ErrorBeatsUpdate_WhenBothPresent()
        => Assert.Equal(BackupStatus.Corrupt,
            BackupScope.Status(Item((FileKind.Installer, FileState.Corrupt, 100), (FileKind.Patch, FileState.Outdated, 20)), Scope.Both), "error takes precedence");

    // ---- Byte-weighted progress helpers ----
    [Test] void ScopedBytes_CountOnlyInScope()
    {
        var it = Item((FileKind.Installer, FileState.Present, 100), (FileKind.Extra, FileState.Present, 40));
        Assert.Equal(140L, BackupScope.ScopedTotalBytes(it, Scope.Both), "both total");
        Assert.Equal(100L, BackupScope.ScopedTotalBytes(it, Scope.GamesOnly), "games-only total is the installer");
        Assert.Equal(100L, BackupScope.ScopedDoneBytes(it, Scope.GamesOnly), "games-only done is the installer");
    }

    [Test] void ScopedDoneBytes_ExcludesNotPresent()
    {
        var it = Item((FileKind.Installer, FileState.Present, 100), (FileKind.Patch, FileState.NotBackedUp, 30));
        Assert.Equal(130L, BackupScope.ScopedTotalBytes(it, Scope.Both), "total counts both");
        Assert.Equal(100L, BackupScope.ScopedDoneBytes(it, Scope.Both), "done counts only the present file");
    }
}

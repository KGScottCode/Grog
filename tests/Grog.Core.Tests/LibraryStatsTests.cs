namespace Grog.Core.Tests;

using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

public class LibraryStatsTests
{
    private static LibraryItem BackupEligible(long id, params (FileKind kind, FileState state, long size)[] files)
    {
        var it = new LibraryItem { GogId = id, Title = $"Game {id}", Slug = $"g{id}" };
        foreach (var (kind, state, size) in files)
            it.Files.Add(new GameFile { Kind = kind, State = state, ExpectedSizeBytes = size });
        return it;
    }
    private static LibraryItem NothingToBackUp(long id)   // no files (bundle shell / folded DLC / stub)
        => new() { GogId = id, Title = $"Shell {id}", Slug = $"s{id}" };

    private static LibraryManifest Manifest(params LibraryItem[] items)
    {
        var m = new LibraryManifest();
        m.Items.AddRange(items);
        return m;
    }

    [Test] void CountsSplitBackableFromNonDownloadable()
    {
        var m = Manifest(
            BackupEligible(1, (FileKind.Installer, FileState.Verified, 100)),
            BackupEligible(2, (FileKind.Installer, FileState.NotBackedUp, 100)),
            NothingToBackUp(3), NothingToBackUp(4));
        var s = LibraryStats.From(m, Scope.Both);
        Assert.Equal(4, s.TotalProducts, "total products");
        Assert.Equal(2, s.BackupEligible, "backable");
        Assert.Equal(2, s.NothingToBackUp, "non-downloadable (the two shells)");
    }

    [Test] void UnavailableFilesAreOutOfEveryCompletenessCount()
    {
        // A file GOG refuses to serve can never be fetched, so it is not a gap: it leaves the denominator
        // exactly like an Old Version. Left in, the extras row read "303 / 305" beside its own 100.0% bar.
        var withUnavail = BackupEligible(1, (FileKind.Extra, FileState.Verified, 100));
        withUnavail.Files.Add(new GameFile { Kind = FileKind.Extra, State = FileState.Unavailable, ExpectedSizeBytes = 0 });
        var without = BackupEligible(1, (FileKind.Extra, FileState.Verified, 100));

        var a = LibraryStats.From(Manifest(withUnavail), Scope.Both);
        var b = LibraryStats.From(Manifest(without), Scope.Both);
        Assert.Equal(b.ExtraFileCount, a.ExtraFileCount, "unavailable file stays out of the extras denominator");
        Assert.Equal(b.ExtraDoneFileCount, a.ExtraDoneFileCount, "and out of the numerator");
        Assert.Equal(1, a.ExtraFileCount, "only the servable extra is counted");
        Assert.Equal(b.ExtraSizeBytes, a.ExtraSizeBytes, "and out of the byte denominator");
    }

    [Test] void UnavailableInstallersLeaveTheGamesCountToo()
    {
        var m = Manifest(BackupEligible(1,
            (FileKind.Installer, FileState.Verified, 100),
            (FileKind.Installer, FileState.Unavailable, 0)));
        var s = LibraryStats.From(m, Scope.Both);
        Assert.Equal(1, s.GameFileCount, "the refused installer is not a gap in the games row");
        Assert.Equal(1, s.GameDoneFileCount, "so the row reads 1 / 1, not 1 / 2");
    }

    [Test] void OldVersionsAreOutOfEveryCompletenessNumber()
    {
        // A retained old version is real disk, but lives in OldVersionFiles (not Files), so it must move
        // NO completeness/byte/count stat. Same game with and without a big old version = identical stats.
        var withOld = BackupEligible(1, (FileKind.Installer, FileState.Verified, 100));
        withOld.OldVersionFiles.Add(new GameFile
        { Kind = FileKind.Installer, State = FileState.Verified, ExpectedSizeBytes = 999_999, LocalSizeBytes = 999_999, IsOldVersion = true });
        var without = BackupEligible(1, (FileKind.Installer, FileState.Verified, 100));

        var a = LibraryStats.From(Manifest(withOld), Scope.Both);
        var b = LibraryStats.From(Manifest(without), Scope.Both);
        Assert.Equal(b.GameSizeBytes, a.GameSizeBytes, "old version stays out of the denominator");
        Assert.Equal(b.GameDoneBytes, a.GameDoneBytes, "old version bytes stay out of backed-up total");
        Assert.Equal(b.GameDoneFileCount, a.GameDoneFileCount, "old version stays out of the file count");
    }

    [Test] void StatusBucketsAreScopeAware()
    {
        // game 1 complete; game 2 has installer only (missing its extra)
        var m = Manifest(
            BackupEligible(1, (FileKind.Installer, FileState.Verified, 100)),
            BackupEligible(2, (FileKind.Installer, FileState.Verified, 100), (FileKind.Extra, FileState.NotBackedUp, 40)));
        var both = LibraryStats.From(m, Scope.Both);
        Assert.Equal(1, both.BackedUp, "both-scope: only game 1 complete");
        Assert.Equal(1, both.Missing, "both-scope: game 2 partial -> missing bucket");

        var games = LibraryStats.From(m, Scope.GamesOnly);
        Assert.Equal(2, games.BackedUp, "games-only: both complete (extra ignored)");
        Assert.Equal(0, games.Missing, "games-only: nothing missing");
    }

    [Test] void CategoryMagnitudes_SplitGamesAndExtras_FullLibrary()
    {
        // Two backable games; game 2 has an extra.
        var m = Manifest(
            BackupEligible(1, (FileKind.Installer, FileState.Verified, 300), (FileKind.Patch, FileState.NotBackedUp, 50)),
            BackupEligible(2, (FileKind.Installer, FileState.NotBackedUp, 200), (FileKind.Extra, FileState.NotBackedUp, 40)));
        var s = LibraryStats.From(m, Scope.GamesOnly);   // scope must NOT change the magnitudes

        Assert.Equal(3, s.GameFileCount, "installer+patch+installer");
        Assert.Equal(550L, s.GameSizeBytes, "300+50+200");
        Assert.Equal(1, s.ExtraFileCount, "one extra");
        Assert.Equal(40L, s.ExtraSizeBytes, "extra bytes");
        Assert.Equal(4, s.AllFileCount, "games + extras, no cloud save");
        Assert.Equal(590L, s.AllSizeBytes, "550 + 40");
        // Raw backed-up: only game 1's installer is present (Verified); scope is GamesOnly but the
        // extra's done-bytes are still reported raw (here 0, since the extra is NotDownloaded).
        Assert.Equal(300L, s.GameDoneBytes, "only the verified installer is on disk");
        Assert.Equal(0L, s.ExtraDoneBytes, "extra not downloaded");
        Assert.Equal(300L, s.AllDoneBytes, "raw done across categories");
    }

    [Test] void ByteWeightedPercent()
    {
        var m = Manifest(
            BackupEligible(1, (FileKind.Installer, FileState.Present, 300)),   // done
            BackupEligible(2, (FileKind.Installer, FileState.NotBackedUp, 100))); // not
        var s = LibraryStats.From(m, Scope.Both);
        Assert.Equal(300L, s.DoneBytes, "done bytes");
        Assert.Equal(400L, s.TotalBytes, "total bytes");
        Assert.True(s.CompletePercent > 74.9 && s.CompletePercent < 75.1, "75% by size, not 50% by count");
    }

    [Test] void EmptyManifest_AllZero()
    {
        var s = LibraryStats.From(new LibraryManifest(), Scope.Both);
        Assert.Equal(0, s.TotalProducts, "no products");
        Assert.Equal(0.0, s.CompletePercent, "no percent");
        Assert.Equal(0, s.Pending, "nothing pending");
    }

    [Test] void UpdateAvailableFoldsIntoMissing()
    {
        var m = Manifest(BackupEligible(1, (FileKind.Installer, FileState.Verified, 100), (FileKind.Patch, FileState.Outdated, 20)));
        var s = LibraryStats.From(m, Scope.Both);
        Assert.Equal(0, s.BackedUp, "update-available is not backed-up");
        Assert.Equal(1, s.Missing, "update folds into missing (same action)");
        Assert.Equal(1, s.Pending, "pending = missing");
    }
}

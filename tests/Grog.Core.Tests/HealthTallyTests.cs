// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

/// <summary>
/// The health tally, folded into LibraryStats from the GUI's RecomputeHealthTally (step 5 of the
/// architecture plan). These pin the rules the verdict and vitals tiles depend on: full scope
/// gating, Unavailable out of every completeness number, hidden legacy movies out of the health figures
/// (but NOT out of the category magnitudes), verified counting, gap bytes, and the staleness stamp.
/// </summary>
[NewBatch]
[Trait("health")]
public class HealthTallyTests
{
    private static GameFile File(FileKind kind, FileState state, long size, long? localSize = null)
        => new() { Kind = kind, State = state, ExpectedSizeBytes = size, LocalSizeBytes = localSize };

    private static LibraryItem Game(long id, params GameFile[] files)
    {
        var it = new LibraryItem { GogId = id, Title = $"Game {id}", Slug = $"g{id}" };
        it.Files.AddRange(files);
        return it;
    }

    private static LibraryManifest Manifest(params LibraryItem[] items)
    {
        var m = new LibraryManifest();
        m.Items.AddRange(items);
        return m;
    }

    [Test] void CountsPresentVerifiedAndGap()
    {
        var m = Manifest(Game(1,
            File(FileKind.Installer, FileState.Verified, 100, localSize: 100),
            File(FileKind.Installer, FileState.Present, 200, localSize: 200),
            File(FileKind.Installer, FileState.NotBackedUp, 400)));
        var s = LibraryStats.From(m, Scope.Both);
        Assert.Equal(3, s.InScopeFiles);
        Assert.Equal(2, s.InScopePresentFiles);
        Assert.Equal(1, s.VerifiedFiles);
        Assert.Equal(300L, s.PresentBytes);
        Assert.Equal(400L, s.GapBytes);
    }

    [Test] void PresentBytesPreferTheLocalSize()
    {
        // The health figure reports what is actually HELD, so a file whose on-disk size differs from
        // GOG's advertised size counts at its local weight.
        var m = Manifest(Game(1, File(FileKind.Installer, FileState.Present, 100, localSize: 90)));
        var s = LibraryStats.From(m, Scope.Both);
        Assert.Equal(90L, s.PresentBytes);
    }

    [Test] void UnavailableNeverEntersTheTally()
    {
        var m = Manifest(Game(1,
            File(FileKind.Installer, FileState.Verified, 100, localSize: 100),
            File(FileKind.Installer, FileState.Unavailable, 500)));
        var s = LibraryStats.From(m, Scope.Both);
        Assert.Equal(1, s.InScopeFiles);
        Assert.Equal(0L, s.GapBytes);
    }

    [Test] void OutOfScopeFilesLeaveNumeratorAndDenominator()
    {
        // Games-only scope: the extra is not a gap and not a success - it is not part of the job at all,
        // which is what lets the percentage reach 100.
        var m = Manifest(Game(1,
            File(FileKind.Installer, FileState.Verified, 100, localSize: 100),
            File(FileKind.Extra, FileState.NotBackedUp, 900)));
        var s = LibraryStats.From(m, Scope.GamesOnly);
        Assert.Equal(1, s.InScopeFiles);
        Assert.Equal(1, s.InScopePresentFiles);
        Assert.Equal(0L, s.GapBytes);
    }

    [Test] void HiddenLegacyMoviesLeaveHealthButNotMagnitudes()
    {
        var movie = Game(2, File(FileKind.Installer, FileState.NotBackedUp, 700));
        movie.Type = ProductType.Movie;
        var m = Manifest(Game(1, File(FileKind.Installer, FileState.Verified, 100, localSize: 100)), movie);

        var hidden = LibraryStats.From(m, Scope.Both, includeLegacyMovies: false);
        // Health never advertises a gap the grid does not show...
        Assert.Equal(1, hidden.InScopeFiles);
        Assert.Equal(0L, hidden.GapBytes);
        // ...but the category magnitudes describe the whole backup and stay truthful regardless.
        Assert.Equal(2, hidden.GameFileCount);

        var shown = LibraryStats.From(m, Scope.Both, includeLegacyMovies: true);
        Assert.Equal(2, shown.InScopeFiles);
        Assert.Equal(700L, shown.GapBytes);
    }

    [Test] void LastApiRefreshIsTheNewestStampAcrossTheCatalog()
    {
        var older = Game(1, File(FileKind.Installer, FileState.Verified, 100));
        older.LastApiRefresh = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = Game(2, File(FileKind.Installer, FileState.Verified, 100));
        newer.LastApiRefresh = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);
        var s = LibraryStats.From(Manifest(older, newer), Scope.Both);
        Assert.Equal(newer.LastApiRefresh, s.LastApiRefresh!.Value);
    }
}

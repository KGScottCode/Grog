// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

[NewBatch]
[Trait("rollups")]
public class RollupsTests
{
    private static GameFile F(FileKind kind, FileState state, long size, string? rootId = null,
        string key = "")
        => new()
        {
            Kind = kind, State = state, ExpectedSizeBytes = size, RootId = rootId,
            LocalSizeBytes = state is FileState.Present or FileState.Verified or FileState.Outdated ? size : (long?)null,
            FileKey = key.Length > 0 ? key : $"{kind}-{state}-{size}",
        };

    private static LibraryItem Game(long id, params GameFile[] files)
    {
        var it = new LibraryItem { GogId = id, Title = $"Game {id}", Slug = $"g{id}" };
        it.Files.AddRange(files);
        return it;
    }

    private static LibraryManifest Manifest(string? primary, params LibraryItem[] items)
    {
        var m = new LibraryManifest { PrimaryRootId = primary };
        m.Items.AddRange(items);
        return m;
    }

    // Sweep 2 #18: the row bar's live figure is in the bar's own unit (files), never bytes.
    [Test]
    void GameFilePercent_IsFileWeighted_WithTheInFlightFraction()
    {
        var it = Game(1,
            F(FileKind.Installer, FileState.Verified, 9000, key: "big"),
            F(FileKind.Installer, FileState.NotBackedUp, 100, key: "dl"),
            F(FileKind.Installer, FileState.NotBackedUp, 100, key: "c"),
            F(FileKind.Installer, FileState.NotBackedUp, 100, key: "d"),
            F(FileKind.Installer, FileState.Unavailable, 100, key: "refused"));
        Assert.Equal(25.0, Rollups.GameFilePercent(it, Scope.Both), "1 of 4: the refused file is not in scope, bytes do not weigh");
        Assert.Equal(37.5, Rollups.GameFilePercent(it, Scope.Both, f => f.FileKey == "dl" ? 50 : 0), "plus half a file in flight");
    }

    // ---- File level -------------------------------------------------------------------------

    private static ProgressSnapshot FileProgress(GameFile f, Rollups.InFlightBytes? inFlight = null)
        => new(Rollups.DoneBytesOf(f, inFlight), Rollups.TotalBytesOf(f));

    [Test] void PresentFileIsFullyDone()
    {
        var p = FileProgress(F(FileKind.Installer, FileState.Verified, 1000));
        Assert.Equal(1000L, p.DoneBytes, "verified = all bytes done");
        Assert.Equal(1000L, p.TotalBytes, "total");
        Assert.True(p.IsComplete, "complete");
    }

    [Test] void NotDownloadedFileIsZeroDone()
    {
        var p = FileProgress(F(FileKind.Installer, FileState.NotBackedUp, 1000));
        Assert.Equal(0L, p.DoneBytes, "nothing on disk, nothing in flight");
    }

    [Test] void InFlightBytesCountTowardDone()
    {
        var f = F(FileKind.Installer, FileState.NotBackedUp, 1000);
        var p = FileProgress(f, _ => 400);
        Assert.Equal(400L, p.DoneBytes, "received bytes count while transferring");
    }

    [Test] void InFlightBytesClampToFileSize()
    {
        // A stale/oversized live number must never push a bar past the file's own size.
        var f = F(FileKind.Installer, FileState.NotBackedUp, 1000);
        var p = FileProgress(f, _ => 5000);
        Assert.Equal(1000L, p.DoneBytes, "clamped to total");
    }

    [Test] void PersistedPartialBytesCountWithNoLiveEngine()
    {
        // A paused / idle partial shows its on-disk progress immediately, no engine running.
        var f = F(FileKind.Installer, FileState.NotBackedUp, 1000);
        f.PartialBytes = 400;
        var p = FileProgress(f);
        Assert.Equal(400L, p.DoneBytes, "persisted partial floor counts without a live transfer");
    }

    [Test] void LiveBytesOverridePersistedPartialWhenHigher()
    {
        // While streaming, the live number climbs above the persisted floor.
        var f = F(FileKind.Installer, FileState.NotBackedUp, 1000);
        f.PartialBytes = 400;
        var p = FileProgress(f, _ => 650);
        Assert.Equal(650L, p.DoneBytes, "live progress rises above the partial floor");
    }

    // ---- Game level -------------------------------------------------------------------------

    [Test] void GameProgressSumsScopedFilesWithInFlight()
    {
        var dl = F(FileKind.Installer, FileState.NotBackedUp, 1000, key: "dl");
        var it = Game(1,
            F(FileKind.Installer, FileState.Verified, 1000, key: "done"),
            dl,
            F(FileKind.Extra, FileState.NotBackedUp, 500, key: "extra"));
        var p = Rollups.Game(it, Scope.Both, f => f.FileKey == "dl" ? 250 : 0);
        Assert.Equal(1250L, p.DoneBytes, "1000 present + 250 in flight; extra not started");
        Assert.Equal(2500L, p.TotalBytes, "1000 + 1000 + 500");
    }

    [Test] void GameProgressHonorsScope()
    {
        var it = Game(1,
            F(FileKind.Installer, FileState.Verified, 1000, key: "g"),
            F(FileKind.Extra, FileState.Verified, 500, key: "x"));
        var p = Rollups.Game(it, Scope.GamesOnly);
        Assert.Equal(1000L, p.TotalBytes, "extras excluded from games-only scope");
    }

    // ---- Library level ----------------------------------------------------------------------

    [Test] void LibrarySplitsGamesAndExtrasAndCombines()
    {
        var m = Manifest(null,
            Game(1, F(FileKind.Installer, FileState.Verified, 1000, key: "a"),
                    F(FileKind.Extra, FileState.NotBackedUp, 400, key: "b")),
            Game(2, F(FileKind.Installer, FileState.NotBackedUp, 1000, key: "c")));
        var r = Rollups.Library(m, Scope.Both, f => f.FileKey == "c" ? 500 : 0);
        Assert.Equal(2000L, r.Games.TotalBytes, "two 1000-byte installers");
        Assert.Equal(1500L, r.Games.DoneBytes, "1000 present + 500 in flight");
        Assert.Equal(400L, r.Extras.TotalBytes, "one 400-byte extra");
        Assert.Equal(0L, r.Extras.DoneBytes, "extra not started");
        Assert.Equal(2400L, r.Whole.TotalBytes, "games + extras total");
        Assert.Equal(1500L, r.Whole.DoneBytes, "games + extras done");
    }

    [Test] void NonBackableContributesNothing()
    {
        var m = Manifest(null,
            new LibraryItem { GogId = 2, Title = "shell" });   // no files -> non-backable
        var r = Rollups.Library(m, Scope.Both);
        Assert.Equal(0L, r.Whole.TotalBytes, "shells contribute nothing");
    }

    // ---- Device level -----------------------------------------------------------------------

}

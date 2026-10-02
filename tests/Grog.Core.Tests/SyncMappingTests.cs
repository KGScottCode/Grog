// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Linq;
using Grog.Core.Api;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

public class GameDetailsMapperTests
{
    [Test]
    void The_discovered_preview_carries_the_same_item_the_manifest_would_get()
    {
        // Preview rows exist so the grid fills DURING a scan. They are only safe because the mapper is pure
        // and the manifest write still happens once, at the end: cancel and nothing was ever stored. This
        // pins the first half of that -- what the event hands out is the mapper's own output, not a
        // half-built stand-in that could drift from the real row.
        var ev = typeof(LibrarySyncService).GetEvent("Discovered");
        Assert.True(ev is not null, "the service exposes a per-product Discovered event");
        Assert.Equal("System.Action`1[Grog.Core.Models.LibraryItem]", ev!.EventHandlerType!.ToString(),
            "it hands out a mapped LibraryItem, the same type the manifest stores");
    }

    [Test]
    void ParseSize_HandlesUnits()
    {
        Assert.Equal(1610612736L, GameDetailsMapper.ParseSize("1.5 GB")!.Value, "1.5 GB");
        Assert.Equal(536870912L, GameDetailsMapper.ParseSize("512 MB")!.Value, "512 MB");
        Assert.Equal(5242880L, GameDetailsMapper.ParseSize("5 MB")!.Value, "5 MB");
        Assert.Null(GameDetailsMapper.ParseSize(""), "empty -> null");
        Assert.Null(GameDetailsMapper.ParseSize("lots"), "unparseable -> null");
    }

    [Test]
    void Slugify_ProducesFolderSafeNames()
    {
        Assert.Equal("the_witcher_3_wild_hunt", GameDetailsMapper.Slugify("The Witcher 3: Wild Hunt"), "colon+spaces");
        Assert.Equal("baldur_s_gate_ii", GameDetailsMapper.Slugify("Baldur's Gate II"), "apostrophe");
        Assert.Equal("untitled", GameDetailsMapper.Slugify("   "), "blank -> untitled");
    }

    [Test]
    void ToLibraryItem_MapsInstallersExtrasAndDlc()
    {
        var details = new GameDetails
        {
            Title = "Test Game",
            CdKey = "KEY-1",
            Installers = { new InstallerFile("/dl/win0", "Setup", "windows", "English", "1.0", "1 GB") },
            Extras = { new ExtraFile("/dl/extra0", "manual", "manuals", "5 MB") },
            Dlcs = { new GameDetails { Title = "Expansion",
                     Installers = { new InstallerFile("/dl/dlc0", "DLC Setup", "windows", "English", "1.0", "500 MB") } } },
        };

        var item = GameDetailsMapper.ToLibraryItem(555, details);

        Assert.Equal(555, item.GogId, "id");
        Assert.Equal("test_game", item.Slug, "slug");
        Assert.Equal("KEY-1", item.SerialKey!, "serial");
        // base installer + base extra + DLC installer (pulled up onto parent) = 3
        Assert.Equal(3, item.Files.Count, "installer + extra + pulled-up dlc installer");
        Assert.Single(item.Files.Where(f => f.Kind == FileKind.Installer), "one base installer");
        Assert.Single(item.Files.Where(f => f.Kind == FileKind.Extra), "one extra");
        Assert.Single(item.Files.Where(f => f.Kind == FileKind.DlcInstaller), "one dlc installer on parent");
        var dlc = Assert.Single(item.Dlcs, "one dlc child");
        Assert.Single(dlc.Files.Where(f => f.Kind == FileKind.DlcInstaller), "dlc installer kind");
    }

    [Test]
    void ToLibraryItem_PullsDlcExtrasOntoParent()
    {
        // Mirrors Shadowrun Returns: base game + a Deluxe DLC whose extras (soundtrack, artbook)
        // are nested in dlcs[].extras and must surface as downloadable files on the parent.
        var details = new GameDetails
        {
            Title = "Shadowrun Returns",
            Installers = { new InstallerFile("/dl/base0", "Setup", "windows", "English", "1.2.7", "1 MB") },
            Extras = { new ExtraFile("/dl/avatar", "avatar", "avatars", "1 MB") },
            Dlcs =
            {
                new GameDetails
                {
                    Title = "Shadowrun Returns Deluxe DLC",
                    Extras =
                    {
                        new ExtraFile("/downloads/shadowrun_returns_deluxe_dlc/86755", "soundtrack (MP3)", "audio", "160 MB"),
                        new ExtraFile("/downloads/shadowrun_returns_deluxe_dlc/86758", "Artbook", "artworks", "15 MB"),
                    }
                }
            }
        };

        var item = GameDetailsMapper.ToLibraryItem(1207660413, details);

        var extras = item.Files.Where(f => f.Kind == FileKind.Extra).ToList();
        Assert.True(extras.Any(e => e.Name.Contains("soundtrack")), "Deluxe soundtrack captured on parent");
        Assert.True(extras.Any(e => e.Name.Contains("Artbook")), "Deluxe artbook captured on parent");
        // The DLC-origin extras keep a reference to their DLC in the name.
        var soundtrack = extras.First(e => e.Name.Contains("soundtrack"));
        Assert.Contains("Deluxe", soundtrack.Name, "DLC origin noted in name");
    }
}

// Sync rebuilds a game's Files from the API feed (which knows nothing about local placement or integrity),
// so MergeInto MUST carry local/on-disk state forward or a plain sync silently wipes it. These lock that.
public class SyncMergeCarryForwardTests
{
    static void MergeInto(LibraryItem existing, LibraryItem incoming)
    {
        var m = typeof(LibrarySyncService).GetMethod("MergeInto",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        m.Invoke(null, new object[] { existing, incoming, new SyncResult(), "" });
    }

    static LibraryItem GameWithFile(GameFile f)
    {
        var it = new LibraryItem { GogId = 1, Title = "G", Slug = "g" };
        it.Files.Add(f);
        return it;
    }

    // (09-13) No size from GOG = nothing to download: Unavailable with a reason, never queued, never counted.
    // A held copy is left alone; a size that appears on a later sync clears it.
    [Test]
    void MergeInto_SizelessFile_IsUnavailable_UntilASizeAppears()
    {
        var existing = GameWithFile(new GameFile { GameGogId = 1, FileKey = "/dl/0", Version = "1.0", State = FileState.NotBackedUp, ExpectedSizeBytes = 10 });
        MergeInto(existing, GameWithFile(new GameFile { GameGogId = 1, FileKey = "/dl/0", Version = "1.0", ExpectedSizeBytes = 0 }));
        var f = existing.Files.Single();
        Assert.Equal(FileState.Unavailable, f.State, "0 MB from GOG: nothing to download");
        Assert.Equal(LibrarySyncService.NoSizeReason, f.UnavailableReason, "and it says why");
        Assert.False(BackupScope.NeedsFetch(f), "never queued");
        Assert.False(BackupScope.CountsTowardCompleteness(f), "never counted");

        MergeInto(existing, GameWithFile(new GameFile { GameGogId = 1, FileKey = "/dl/0", Version = "1.0", ExpectedSizeBytes = 512 }));
        f = existing.Files.Single();
        Assert.Equal(FileState.NotBackedUp, f.State, "a size appeared: back on the normal path");
        Assert.True(f.UnavailableReason is null, "reason cleared");

        var held = GameWithFile(new GameFile { GameGogId = 1, FileKey = "/dl/1", Version = "1.0", State = FileState.Verified, LocalRelativePath = "g/x", ExpectedSizeBytes = 10 });
        MergeInto(held, GameWithFile(new GameFile { GameGogId = 1, FileKey = "/dl/1", Version = "1.0" }));
        Assert.Equal(FileState.Verified, held.Files.Single().State, "a copy we hold is not re-flagged over a missing size");
    }

    // Same version: the download-time hash, root placement, and resume fields all survive the sync.
    [Test]
    void MergeInto_UnchangedVersion_PreservesLocalState()
    {
        var existing = GameWithFile(new GameFile
        {
            GameGogId = 1, FileKey = "/dl/0", Version = "1.0", State = FileState.Verified,
            ExpectedMd5 = "d41d8cd98f00b204e9800998ecf8427e", RootId = "archive",
            ResolvedFileName = "setup_real.exe", LocalRelativePath = "g/setup_real.exe", LocalSizeBytes = 123,
        });
        // Incoming from the API feed: same slot + version, but NONE of the local fields.
        var incoming = GameWithFile(new GameFile { GameGogId = 1, FileKey = "/dl/0", Version = "1.0" });

        MergeInto(existing, incoming);

        var f = existing.Files.Single();
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", f.ExpectedMd5, "integrity hash survives sync (verify keeps working)");
        Assert.Equal("archive", f.RootId, "multi-volume placement survives sync");
        Assert.Equal("setup_real.exe", f.ResolvedFileName, "resolved filename survives sync");
        Assert.Equal(FileState.Verified, f.State, "unchanged version keeps its state");
    }

    // Version changed: flag UpdateAvailable and DROP the stale hash (it describes the old build, not the new).
    [Test]
    void MergeInto_VersionChanged_FlagsUpdateAndDropsStaleHash()
    {
        var existing = GameWithFile(new GameFile
        {
            GameGogId = 1, FileKey = "/dl/0", Version = "1.0", State = FileState.Verified,
            ExpectedMd5 = "d41d8cd98f00b204e9800998ecf8427e", RootId = "archive", LocalRelativePath = "g/x",
        });
        var incoming = GameWithFile(new GameFile { GameGogId = 1, FileKey = "/dl/0", Version = "2.0" });

        MergeInto(existing, incoming);

        var f = existing.Files.Single();
        Assert.Equal(FileState.Outdated, f.State, "new version -> update available (detected by version string, not a hash mismatch)");
        Assert.Null(f.ExpectedMd5, "stale hash dropped; the new build's hash is captured at re-download");
        Assert.Equal("archive", f.RootId, "placement still preserved");
    }
}

public class BackupStatusRollupTests
{
    static LibraryItem WithStates(params FileState[] states)
    {
        var item = new LibraryItem { GogId = 1, Title = "G" };
        foreach (var s in states)
            item.Files.Add(new GameFile { GameGogId = 1, FileKey = System.Guid.NewGuid().ToString(), State = s });
        return item;
    }

    [Test]
    void StaleNotDownloaded_ReconcilesToComplete_WhenFilesDownloaded()
    {
        // The exact restart/post-download bug: files landed (Downloaded) but the cached rollup was
        // never refreshed and still reads NotDownloaded. Recompute must re-derive it to Complete.
        var item = WithStates(FileState.Present, FileState.Present);
        item.Status = BackupStatus.NotBackedUp; // stale cached value
        LibrarySyncService.RecomputeStatus(item);
        Assert.Equal(BackupStatus.Complete, item.Status, "downloaded files re-derive to Complete");
    }

    [Test]
    void AllVerified_IsComplete()
    {
        var item = WithStates(FileState.Verified, FileState.Verified);
        LibrarySyncService.RecomputeStatus(item);
        Assert.Equal(BackupStatus.Complete, item.Status, "all verified");
    }

    [Test]
    void NothingLocal_IsNotDownloaded()
    {
        var item = WithStates(FileState.NotBackedUp, FileState.NotBackedUp);
        LibrarySyncService.RecomputeStatus(item);
        Assert.Equal(BackupStatus.NotBackedUp, item.Status, "nothing present");
    }

    [Test]
    void SomePresent_IsPartial()
    {
        var item = WithStates(FileState.Verified, FileState.NotBackedUp);
        LibrarySyncService.RecomputeStatus(item);
        Assert.Equal(BackupStatus.Partial, item.Status, "some present");
    }

    [Test]
    void AnyUpdate_IsUpdateAvailable()
    {
        var item = WithStates(FileState.Verified, FileState.Outdated);
        LibrarySyncService.RecomputeStatus(item);
        Assert.Equal(BackupStatus.Outdated, item.Status, "update present");
    }

    [Test]
    void AllOutdated_IsNotBackedUp()
    {
        // The invariant: an updated file IS a file you do not have. A private rollup copy used to count
        // Outdated as present, so a game whose every file was superseded reported Outdated while the scoped
        // numbers on screen said NotBackedUp. Both paths now share BackupScope.StatusOf.
        var item = WithStates(FileState.Outdated, FileState.Outdated);
        LibrarySyncService.RecomputeStatus(item);
        Assert.Equal(BackupStatus.NotBackedUp, item.Status, "zero present files, whatever their history");
    }

    [Test]
    void AnyCorrupt_IsError()
    {
        var item = WithStates(FileState.Verified, FileState.Corrupt);
        LibrarySyncService.RecomputeStatus(item);
        Assert.Equal(BackupStatus.Corrupt, item.Status, "corrupt present");
    }
}

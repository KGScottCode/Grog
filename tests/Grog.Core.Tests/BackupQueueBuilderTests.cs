// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Grog.Core.Download;
using Grog.Core.Volumes;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Runs;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

/// <summary>
/// The ONE selection rule both hosts and the App's three counts (scoped label, submenu, enqueue) build from.
/// Each option is pinned on its own so a host wiring one wrongly fails here, not in a user's queue.
/// </summary>
public sealed class BackupQueueBuilderTests
{
    private static GameFile F(long game, string key, FileKind kind = FileKind.Installer, FileState state = FileState.NotBackedUp,
                              long size = 10, string? rootId = null)
        => new() { GameGogId = game, FileKey = key, Name = key, Kind = kind, State = state, ExpectedSizeBytes = size, RootId = rootId,
                   Os = kind == FileKind.Installer ? "windows" : "", Language = kind == FileKind.Installer ? "English" : "" };

    private static LibraryManifest Lib()
    {
        var m = new LibraryManifest();
        m.Items.Add(new LibraryItem { GogId = 1, Title = "One", Slug = "one", Type = ProductType.Game, Files =
            { F(1, "one-setup"), F(1, "one-manual", FileKind.Extra, size: 1), F(1, "one-done", state: FileState.Verified) } });
        m.Items.Add(new LibraryItem { GogId = 2, Title = "Two", Slug = "two", Type = ProductType.Game, Files =
            { F(2, "two-setup", size: 50), F(2, "two-old", state: FileState.Outdated, size: 7) } });
        m.Items.Add(new LibraryItem { GogId = 3, Title = "Film", Slug = "film", Type = ProductType.Movie, Files = { F(3, "film-mp4") } });
        return m;
    }

    private static List<string> Keys(LibraryManifest m, BackupRunOptions o)
        => BackupQueueBuilder.Build(m, null, o).Select(x => x.File.FileKey).OrderBy(k => k).ToList();

    // (09-19, walk pass 3) "Back up this game" with a queue already standing: two of its three files ran, the third
    // stayed at position 46 because the mid-run re-plan admits the whole queue in PERSISTED order.
    [Test]
    public void A_picked_games_files_go_to_the_front_of_a_standing_queue_in_their_own_order()
    {
        var m = Lib();
        foreach (var k in new[] { "one-setup", "one-manual" }) m.Downloads.Enqueue(1, k);
        m.Downloads.Enqueue(3, "film-mp4");
        m.Downloads.Enqueue(2, "two-setup"); m.Downloads.Enqueue(2, "two-old");   // game Two sits LAST

        var picked = BackupQueueBuilder.Build(m, null, new BackupRunOptions(OnlyGameIds: new HashSet<long> { 2 }));
        BackupQueueBuilder.PromoteToFront(m, picked);

        Assert.Equal("two-setup,two-old,one-setup,one-manual,film-mp4",
                     string.Join(",", m.Downloads.Snapshot().Select(q => q.FileKey)), "picked game first, the rest as they were");
    }

    [Test]
    public void Default_picks_every_gap_and_nothing_present()
        => Assert.Equal("film-mp4,one-manual,one-setup,two-old,two-setup", string.Join(",", Keys(Lib(), new BackupRunOptions())));

    private sealed class OfflineAware : IBackupLayout
    {
        public string? ResolvePath(GameFile f) => null;
        public string? ResolveTargetDir(long gogId, string slug, FileKind kind, out string rootId, GameFile? f = null) { rootId = "p"; return null; }
        public string? RootPath(string? rootId) => null;
        public IReadOnlyCollection<string> OnlineRootPaths => System.Array.Empty<string>();
        public bool IsOnline(string? rootId) => rootId != "offline";
        public string SubPathFor(string slug, FileKind kind, GameFile? f = null, long gogId = 0, string? rootId = null) => "";
        public string? ResolveCloudDir(string slug, out string rootId) { rootId = "p"; return null; }
    }

    /// <summary>(QA 09-18) The resume drain applies the unplugged-home-drive rule the fresh path has: an Outdated
    /// file whose drive is away is not fetched onto another root (that splits one file across two roots and
    /// strands its Old Versions step). It stays queued and waits.</summary>
    [Test]
    public void Resume_drain_leaves_a_file_whose_home_drive_is_unplugged_queued_and_unfetched()
    {
        var m = Lib();
        m.ItemById(2)!.Files.First(f => f.FileKey == "two-old").RootId = "offline";
        m.Downloads.Enqueue(2, "two-old"); m.Downloads.Enqueue(2, "two-setup");
        var picked = BackupQueueBuilder.Build(m, new OfflineAware(), new BackupRunOptions(FromPersistedQueue: true));
        Assert.Equal("two-setup", string.Join(",", picked.Select(x => x.File.FileKey)), "the file on the absent drive is not fetched");
        Assert.True(m.Downloads.Contains(2, "two-old"), "and it is still queued for when the drive is back");
    }

    [Test]
    public void Updates_only_picks_outdated_alone()
        => Assert.Equal("two-old", string.Join(",", Keys(Lib(), new BackupRunOptions(UpdatesOnly: true))));

    [Test]
    public void Only_game_ids_narrows_to_those_games()
        => Assert.Equal("two-old,two-setup", string.Join(",", Keys(Lib(), new BackupRunOptions(OnlyGameIds: new HashSet<long> { 2 }))));

    [Test]
    public void View_filter_is_the_hosts_say_movies_and_chips()
    {
        var o = new BackupRunOptions(ViewFilter: (item, f) => item.Type != ProductType.Movie && f.Kind == FileKind.Installer);
        Assert.Equal("one-setup,two-old,two-setup", string.Join(",", Keys(Lib(), o)));
    }

    [Test]
    public void Scope_override_wins_over_the_saved_scope()
    {
        var m = Lib();
        m.Scope = new ScopeSettings { IncludeGames = false, IncludeExtras = true };   // saved: extras only
        Assert.Equal("one-manual", string.Join(",", Keys(m, new BackupRunOptions())), "saved scope applies by default");
        Assert.Equal("film-mp4,one-setup,two-old,two-setup", string.Join(",", Keys(m, new BackupRunOptions(ScopeOverride: new Scope(true, false)))), "override replaces it");
        Assert.Equal("film-mp4,one-manual,one-setup,two-old,two-setup", string.Join(",", Keys(m, new BackupRunOptions(IgnoreSavedScope: true))), "lifted");
    }

    [Test]
    public void Keep_largest_keeps_the_biggest_n()
        => Assert.Equal("two-setup", string.Join(",", Keys(Lib(), new BackupRunOptions(KeepLargest: 1))));

    [Test]
    public void Retry_failed_readmits_corrupt_files()
    {
        var m = Lib();
        m.Items[0].Files[2].State = FileState.Corrupt;
        Assert.False(Keys(m, new BackupRunOptions()).Contains("one-done"), "condemned stays out");
        Assert.True(Keys(m, new BackupRunOptions(RetryFailed: true)).Contains("one-done"), "explicit retry readmits");
        Assert.Equal(FileState.NotBackedUp, m.Items[0].Files[2].State);
    }

    [Test]
    public void Persisted_queue_drains_in_order_and_drops_present()
    {
        var m = Lib();
        m.Downloads.Enqueue(2, "two-setup", null);
        m.Downloads.Enqueue(1, "one-done", null);    // present: leaves the queue
        m.Downloads.Enqueue(1, "one-setup", null);
        var picked = BackupQueueBuilder.Build(m, null, new BackupRunOptions(FromPersistedQueue: true)).Select(x => x.File.FileKey).ToList();
        Assert.Equal("two-setup,one-setup", string.Join(",", picked));
        Assert.Equal(2, m.Downloads.Snapshot().Count, "the present file was removed from the queue");
    }

    [Test]
    public void Replan_moves_a_pending_whole_game_onto_the_primary_once_room_appears()
    {
        // Owner, 09-05: 1.09 GB left on the primary at the end of a run, three games under that size queued for the
        // secondary. The first plan reserved with rounded-up sizes; a re-plan against real free space fixes it.
        var m = Lib();
        m.Roots.Add(new BackupRoot { Id = "p", Label = "Primary" }); m.Roots.Add(new BackupRoot { Id = "s", Label = "Secondary" });
        m.PrimaryRootId = "p";
        var one = new DownloadTask { File = m.Items[0].Files[0], GameTitle = "One", GameSlug = "one", TargetRootId = "s" };   // 10 bytes, planned on S
        var two = new DownloadTask { File = m.Items[1].Files[0], GameTitle = "Two", GameSlug = "two", TargetRootId = "s" };   // 50 bytes
        var engine = new DownloadEngine(null!, new System.Net.Http.HttpClient()) { BackupRoot = System.IO.Path.GetTempPath() };
        engine.EnqueueRange(new[] { one, two });
        m.Downloads.Enqueue(1, "one-setup", "s"); m.Downloads.Enqueue(2, "two-setup", "s");

        var devices = new[] { new DeviceSpace("p", 20, true), new DeviceSpace("s", 1000, true) };   // room for game One now
        int moved = BackupQueueBuilder.Replan(m, devices, engine);

        Assert.Equal(1, moved);
        Assert.Equal("p", one.TargetRootId, "the whole 10-byte game moves to the primary");
        Assert.Equal("s", two.TargetRootId, "50 bytes still does not fit");
        Assert.Equal("p", m.Downloads.Snapshot().First(q => q.FileKey == "one-setup").TargetRootId, "the record follows the plan");
    }
}

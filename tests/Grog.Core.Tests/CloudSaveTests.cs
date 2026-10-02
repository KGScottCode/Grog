// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Linq;
using Grog.Core.CloudSaves;
using Grog.Core.Tests.Framework;

[Trait("cloud")]
public class CloudSaveTests
{
    [Test]
    void The_local_save_count_still_reads_from_the_OLD_json_name()
    {
        // "Snapshot" became "local save" throughout the code. This field is PERSISTED, so the wire name was
        // pinned rather than renamed: without the pin, every existing manifest would read back zero and a
        // game with a shelf full of saves would report "never backed up". Renaming a serialized member is a
        // data change wearing a refactor's clothes.
        var json = "{\"CloudLastBackup\":null,\"CloudSnapshotCount\":7}";
        var item = System.Text.Json.JsonSerializer.Deserialize<Grog.Core.Models.LibraryItem>(json);
        Assert.True(item is not null, "the item deserializes");
        Assert.Equal(7, item!.CloudLocalSaveCount, "the old JSON name still populates the renamed property");

        var round = System.Text.Json.JsonSerializer.Serialize(item);
        Assert.True(round.Contains("CloudSnapshotCount"), "and it writes back under the old name too");
        Assert.True(!round.Contains("CloudLocalSaveCount"), "the C# name must never reach the file");
    }

    [Test]
    void ParseSaveList_BareArray()
    {
        var json = """
        [
          {"name":"saves/Orc 02 - 001.sav","size":15360,"last_modified":"2019-06-15T15:17:00Z","download_url":"https://x/1"},
          {"name":"saves/Orc 02 - Start.sav","size":15000,"last_modified":"2019-06-15T15:17:00Z","download_url":"https://x/2"}
        ]
        """;
        var list = CloudSaveService.ParseSaveList(json);
        Assert.Equal(2, list.Count, "two saves parsed");
        Assert.Equal("saves/Orc 02 - 001.sav", list[0].Name, "name");
        Assert.Equal(15360L, list[0].SizeBytes, "size");
        Assert.True(list[0].Modified is not null, "timestamp parsed");
        Assert.Equal("https://x/1", list[0].DownloadUrl, "download url");
    }

    // ---- Retention: PruneToKeep enforces the newest-N cap; keep <= 0 keeps everything ----

    static string SeedLocalSaves(params string[] stamps)
    {
        var gameDir = System.IO.Directory.CreateTempSubdirectory("grog-snap-").FullName;
        var root = System.IO.Path.Combine(gameDir, "cloud-saves");
        foreach (var s in stamps)
        {
            var d = System.IO.Path.Combine(root, s);
            System.IO.Directory.CreateDirectory(d);
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "slot1.sav"), "x");
        }
        return gameDir;
    }

    [Test]
    void PruneToKeep_KeepsNewestN_DeletesOlder()
    {
        var gameDir = SeedLocalSaves("20260101-000000", "20260201-000000", "20260301-000000", "20260401-000000");
        var deleted = LocalSaveArchive.PruneToKeep(gameDir, 2);
        Assert.Equal(2, deleted, "two oldest pruned");
        var left = LocalSaveArchive.List(gameDir);
        Assert.Equal(2, left.Count, "two newest remain");
        Assert.Equal("20260401-000000", System.IO.Path.GetFileName(left[0].Dir), "newest kept");
        Assert.Equal("20260301-000000", System.IO.Path.GetFileName(left[1].Dir), "second-newest kept");
    }

    [Test]
    void PruneToKeep_KeepAll_IsNoOp()
    {
        var gameDir = SeedLocalSaves("20260101-000000", "20260201-000000", "20260301-000000");
        Assert.Equal(0, LocalSaveArchive.PruneToKeep(gameDir, 0), "keep 0 = keep all, nothing deleted");
        Assert.Equal(3, LocalSaveArchive.List(gameDir).Count, "all three remain");
    }

    [Test]
    void PruneToKeep_LatestOnly_KeepsOne()
    {
        var gameDir = SeedLocalSaves("20260101-000000", "20260201-000000", "20260301-000000");
        Assert.Equal(2, LocalSaveArchive.PruneToKeep(gameDir, 1), "latest-only prunes the other two");
        var left = LocalSaveArchive.List(gameDir);
        Assert.Equal(1, left.Count, "one remains");
        Assert.Equal("20260301-000000", System.IO.Path.GetFileName(left[0].Dir), "the newest");
    }

    [Test]
    void PruneToKeep_FewerThanCap_KeepsAll()
    {
        var gameDir = SeedLocalSaves("20260101-000000", "20260201-000000");
        Assert.Equal(0, LocalSaveArchive.PruneToKeep(gameDir, 5), "under the cap, nothing pruned");
        Assert.Equal(2, LocalSaveArchive.List(gameDir).Count, "both remain");
    }

    [Test]
    void ParseSaveList_ObjectWrappedUnderItems()
    {
        var json = """
        {"items":[{"path":"a.sav","content_length":"512","url":"https://y/a"}]}
        """;
        var list = CloudSaveService.ParseSaveList(json);
        Assert.Equal(1, list.Count, "one save under items[]");
        Assert.Equal("a.sav", list[0].Name, "path field used as name");
        Assert.Equal(512L, list[0].SizeBytes, "string size coerced");
        Assert.Equal("https://y/a", list[0].DownloadUrl, "url field");
    }

    [Test]
    void ParseSaveList_ToleratesMissingFields()
    {
        var json = """[{"name":"only-name.sav"}]""";
        var list = CloudSaveService.ParseSaveList(json);
        Assert.Equal(1, list.Count, "entry kept even with just a name");
        Assert.Equal(0L, list[0].SizeBytes, "missing size → 0");
        Assert.Equal("", list[0].DownloadUrl, "missing url → empty");
    }

    [Test]
    void ParseSaveList_MalformedYieldsEmpty()
    {
        Assert.Empty(CloudSaveService.ParseSaveList("not json"), "garbage → empty, no throw");
        Assert.Empty(CloudSaveService.ParseSaveList("{}"), "object with no array → empty");
    }

    [Test]
    void ParseSaveList_SkipsEntriesWithoutName()
    {
        var json = """[{"size":100},{"name":"real.sav","size":200}]""";
        var list = CloudSaveService.ParseSaveList(json);
        Assert.Equal(1, list.Count, "nameless entry skipped");
        Assert.Equal("real.sav", list[0].Name, "only the named one survives");
    }

    [Test]
    void ParseContainers_ReadsLiveMasterList()
    {
        // Verbatim from a live /v2/users/{id}/containers capture (6 cloud-save games).
        const string json = """
        { "pagination": { "page": 1, "limit": 50, "pages": 1 },
          "items": [
            { "games": [ { "id": 1612002354, "quota": 209715200 } ], "container": { "size": 341060, "files": 1, "space_id": "58342960733154355", "quota": 209715200 } },
            { "games": [ { "id": 1418669891, "quota": 209715200 } ], "container": { "size": 58573, "files": 4, "space_id": "52025746126771922", "quota": 209715200 } },
            { "games": [ { "id": 1158113613, "quota": 209715200 } ], "container": { "size": 5062, "files": 2, "space_id": "58812467486876255", "quota": 209715200 } },
            { "games": [ { "id": 1207659066, "quota": 209715200 } ], "container": { "size": 793, "files": 1, "space_id": "49903592519473549", "quota": 209715200 } },
            { "games": [ { "id": 1706049527, "quota": 209715200 } ], "container": { "size": 29, "files": 1, "space_id": "51909715004502048", "quota": 209715200 } },
            { "games": [ { "id": 1207658959, "quota": 209715200 } ], "container": { "size": 21, "files": 1, "space_id": "49903590898113770", "quota": 209715200 } }
          ] }
        """;
        var list = Grog.Core.CloudSaves.CloudSaveService.ParseContainers(json);
        Assert.Equal(6, list.Count, "six cloud-save games");
        var re0 = list[0];
        Assert.Equal(1612002354L, re0.ProductId, "product id maps to library GogId");
        Assert.Equal(341060L, re0.SizeBytes, "container size");
        Assert.Equal(1, re0.Files, "file count");
        Assert.Equal("58342960733154355", re0.SpaceId, "space id");
        Assert.Equal(209715200L, re0.QuotaBytes, "200 MB quota");
    }

    // The container's change-timestamp is the primary change signal (size can hide a same-size overwrite).
    [Test]
    void ParseContainers_ReadsChangeTimestampWhenPresent()
    {
        const string json = """
        { "items": [
            { "games": [ { "id": 42 } ], "container": { "size": 100, "files": 2, "space_id": "s", "date_updated": "2026-07-01T12:00:00Z" } }
        ] }
        """;
        var list = Grog.Core.CloudSaves.CloudSaveService.ParseContainers(json);
        var c = Assert.Single(list, "one container");
        Assert.True(c.UpdatedUtc is not null, "change timestamp parsed");
        Assert.Equal(2026, c.UpdatedUtc!.Value.Year, "timestamp value");
    }

    // Reconcile carries file count + change-timestamp onto the item so change-detection has its inputs.
    [Test]
    void Apply_CarriesFileCountAndChangeTimestamp()
    {
        var it = Item(100, "Alpha");
        var m = Manifest(it);
        var when = new System.DateTimeOffset(2026, 7, 1, 0, 0, 0, System.TimeSpan.Zero);
        var container = new CloudContainer(100, 4096, 3, "sp", 209715200, when);
        var (rows, _) = CloudSaveReconciler.Apply(m, new[] { container });
        Assert.Equal(3, it.CloudSaveFiles, "file count cached onto item");
        Assert.Equal(when, it.CloudLatestChangeUtc, "change timestamp cached onto item");
        Assert.Equal(3, rows[0].Files, "row carries file count");
        Assert.Equal(when, rows[0].LatestChangeUtc, "row carries change timestamp");
    }

    // ---- CloudSaveReconciler.Apply (pure discovery reconcile) ----

    static Grog.Core.Manifest.LibraryManifest Manifest(params Grog.Core.Models.LibraryItem[] items)
    {
        var m = new Grog.Core.Manifest.LibraryManifest();
        m.Items.AddRange(items);
        return m;
    }
    static Grog.Core.Models.LibraryItem Item(long id, string title) =>
        new() { GogId = id, Title = title, Slug = title.ToLowerInvariant() };
    static CloudContainer Container(long id, long size, string space = "sp") =>
        new(id, size, 1, space, 209715200);

    [Test]
    void Apply_MatchedContainer_FlagsAndCachesOnItem()
    {
        var it = Item(100, "Alpha");
        var m = Manifest(it);
        var (rows, unmatched) = CloudSaveReconciler.Apply(m, new[] { Container(100, 4096, "space-x") });

        Assert.Equal(0, unmatched.Count, "no unmatched");
        Assert.Equal(1, rows.Count, "one row");
        Assert.True(it.HasCloudSaves == true, "item flagged has-cloud-saves");
        Assert.Equal(4096L, it.CloudSaveSize, "size cached onto item");
        Assert.Equal("space-x", it.CloudSpaceId, "space id cached onto item");
        Assert.Equal("Alpha", rows[0].Title, "row reuses library title");
        Assert.Equal(4096L, rows[0].SizeBytes, "row size");
    }

    [Test]
    void Apply_ClearsFlagOnItemsWithoutContainers()
    {
        var withSaves = Item(1, "Keep");
        var stale = Item(2, "Stale");
        stale.HasCloudSaves = true;   // left over from a previous discovery
        var m = Manifest(withSaves, stale);
        CloudSaveReconciler.Apply(m, new[] { Container(1, 10) });

        Assert.True(withSaves.HasCloudSaves == true, "still flagged");
        Assert.True(stale.HasCloudSaves == false, "flag cleared when no container");
    }

    [Test]
    void Apply_ClearsStaleCloudIdentityOnItemsWithoutContainers()
    {
        // A game left with a cached client id / size / space id from testing, but no container any more.
        var stale = Item(2, "Witcher");
        stale.HasCloudSaves = true;
        stale.CloudClientId = "leftover-client-id";
        stale.CloudSaveSize = 4096;
        stale.CloudSpaceId = "leftover-space";
        var m = Manifest(Item(1, "Real"), stale);
        CloudSaveReconciler.Apply(m, new[] { Container(1, 10) });

        Assert.True(stale.HasCloudSaves == false, "flag cleared");
        Assert.Equal("", stale.CloudClientId, "stale client id scrubbed");
        Assert.Equal(0L, stale.CloudSaveSize, "stale size scrubbed");
        Assert.Equal("", stale.CloudSpaceId, "stale space id scrubbed");
    }

    [Test]
    void Apply_UnmatchedContainer_ReturnedForTitleFetch()
    {
        var m = Manifest(Item(1, "Owned"));
        var (rows, unmatched) = CloudSaveReconciler.Apply(m, new[] { Container(1, 10), Container(999, 20) });

        Assert.Equal(1, rows.Count, "only the matched item yields a row here");
        Assert.Equal(1, unmatched.Count, "the cold-start id is unmatched");
        Assert.Equal(999L, unmatched[0], "unmatched product id surfaced");
    }
}

/// <summary>A save body cut short by the connection was renamed into place as the save (sweep: F7).</summary>
[Trait("cloud")]
public class CloudSaveDownloadLengthTests
{
    sealed class Body : System.Net.Http.HttpMessageHandler
    {
        public byte[] Bytes = new byte[10];
        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage req, System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.ByteArrayContent(Bytes) });
    }
    sealed class NoAuth : Grog.Core.Auth.IAuthService
    {
        public bool HasStoredSession => true;
        public System.Threading.Tasks.Task<Grog.Core.Auth.AuthSession> EnsureAuthenticatedAsync(System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult(new Grog.Core.Auth.AuthSession("at", "rt", System.DateTimeOffset.UtcNow.AddHours(1), "u"));
        public System.Threading.Tasks.Task SignOutAsync() => System.Threading.Tasks.Task.CompletedTask;
    }

    static string Dir() { var d = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "grog-csl-" + System.Guid.NewGuid().ToString("N")[..8]); System.IO.Directory.CreateDirectory(d); return d; }

    [Test]
    async System.Threading.Tasks.Task A_short_body_is_thrown_away_not_kept_as_the_save()
    {
        var dir = Dir(); var dest = System.IO.Path.Combine(dir, "slot1.sav");
        var svc = new CloudSaveService(new System.Net.Http.HttpClient(new Body()), new NoAuth());
        bool threw = false;
        try { await svc.DownloadToAsync("https://cloudstorage.gog.com/x", "t", dest, 20, default); }
        catch (System.IO.IOException) { threw = true; }
        Assert.True(threw, "10 of 20 bytes is a failure");
        Assert.False(System.IO.File.Exists(dest), "nothing under the final name");
        Assert.False(System.IO.File.Exists(dest + ".part"), "the .part is gone too");
    }

    [Test]
    async System.Threading.Tasks.Task A_body_of_the_listed_size_or_an_unlisted_size_lands()
    {
        var dir = Dir(); var svc = new CloudSaveService(new System.Net.Http.HttpClient(new Body()), new NoAuth());
        Assert.True(await svc.DownloadToAsync("https://cloudstorage.gog.com/x", "t", System.IO.Path.Combine(dir, "a.sav"), 10, default), "exact size");
        Assert.True(await svc.DownloadToAsync("https://cloudstorage.gog.com/x", "t", System.IO.Path.Combine(dir, "b.sav"), 0, default), "no listed size: accepted as is");
        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(dir, "a.sav")) && System.IO.File.Exists(System.IO.Path.Combine(dir, "b.sav")), "both saves are on disk");
    }
}

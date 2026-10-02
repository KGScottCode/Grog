// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

/// <summary>
/// (New items 09-09) The New flag end to end: the first scan of an EMPTY library is the baseline and
/// flags nothing, whatever GOG's isNew says; every later scan flags what the manifest never had and ignores isNew entirely; a rescan never
/// touches an existing item's flag; the auto-clear fires only on real in-scope completeness; the explicit
/// clears work; and a manifest written before the field existed reads back as not-new.
/// </summary>
[NewBatch]
[Trait("sync")]
public class NewItemsTests
{
    /// <summary>A fake GOG whose getFilteredProducts carries `isNew` per product, so the seeding rule can be
    /// tested at the level it actually operates: the listing.</summary>
    private sealed class FakeGog : HttpMessageHandler
    {
        public List<long> Owned = new() { 1, 2 };
        public HashSet<long> GogSaysNew = new() { 2 };
        public HashSet<long> Sizeless = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            string body;
            if (url.EndsWith("/user/data/games")) body = "{\"owned\":[" + string.Join(",", Owned) + "]}";
            else if (url.Contains("getFilteredProducts"))
                body = "{\"page\":1,\"totalPages\":1,\"totalProducts\":" + Owned.Count + ",\"products\":["
                     + string.Join(",", Owned.Select(id =>
                         $"{{\"id\":{id},\"title\":\"Game {id}\",\"slug\":\"game_{id}\",\"category\":\"RPG\",\"isGame\":true,"
                       + $"\"isNew\":{(GogSaysNew.Contains(id) ? "true" : "false")},\"url\":\"/game/game_{id}\"}}"))
                     + "]}";
            else if (url.Contains("catalog.gog.com")) body = "{\"pages\":1,\"products\":[]}";
            else if (url.Contains("/account/gameDetails/"))
            {
                var id = long.Parse(url.Split('/').Last().Replace(".json", ""));
                body = $"{{\"title\":\"Game {id}\",\"downloads\":[[\"English\",{{\"windows\":[{{\"manualUrl\":\"/downlink/game_{id}/en1installer0\","
                     + $"\"name\":\"Setup\",\"version\":\"1.0\",\"size\":\"{(Sizeless.Contains(id) ? "0 MB" : "10 MB")}\"}}]}}]],\"extras\":[]}}";
            }
            else return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class NoAuth : IAuthService
    {
        public bool HasStoredSession => true;
        public Task<AuthSession> EnsureAuthenticatedAsync(CancellationToken ct = default)
            => Task.FromResult(new AuthSession("at", "rt", DateTimeOffset.UtcNow.AddHours(1), "u"));
        public Task SignOutAsync() => Task.CompletedTask;
    }

    /// <summary>A real temp dir + JsonManifestStore, like ImportRunTests: the flag has to survive a save.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly string Dir;
        public readonly JsonManifestStore Store;
        public readonly FakeGog Gog = new();
        public readonly GogApiClient Api;
        public Rig()
        {
            Dir = Directory.CreateTempSubdirectory("grog-newitems-").FullName;
            Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(Dir, "_cfg"));
            Store = new JsonManifestStore(GrogPaths.Resolve(Dir));
            Store.LoadAsync().GetAwaiter().GetResult();
            Api = new GogApiClient(new HttpClient(Gog), new NoAuth());
        }
        public LibrarySyncService Sync(bool baseline)
            => new(Api, Store) { BaselineScan = baseline };
        public LibraryItem Item(long id) => Store.Current.Items.First(i => i.GogId == id);
        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    [Test]
    public async Task First_scan_of_an_empty_library_is_the_baseline_and_flags_nothing()
    {
        using var rig = new Rig();
        await rig.Sync(baseline: true).RunAsync();

        Assert.Equal(2, rig.Store.Current.Items.Count, "items added");
        Assert.False(rig.Item(1).IsNew, "the baseline flags nothing");
        Assert.False(rig.Item(2).IsNew, "GOG flagged product 2 isNew, and the baseline ignores it");
        Assert.Equal(0, NewItems.Count(rig.Store.Current), "flagged count");
    }

    // (09-19) The sizeless rule lived only in MergeInto, so a brand-new item skipped it: on a first scan the
    // file was queued as "Size unknown" until GOG refused it mid-run.
    [Test]
    public async Task A_brand_new_items_sizeless_file_is_Unavailable_from_its_first_scan()
    {
        using var rig = new Rig();
        rig.Gog.Sizeless.Add(2);
        await rig.Sync(baseline: true).RunAsync();

        var f = rig.Item(2).Files.Single();
        Assert.Equal(FileState.Unavailable, f.State, "0 MB from GOG: nothing to download");
        Assert.Equal(LibrarySyncService.NoSizeReason, f.UnavailableReason, "and it says why");
        Assert.False(BackupScope.NeedsFetch(f), "never queued");
        Assert.Equal(FileState.NotBackedUp, rig.Item(1).Files.Single().State, "a sized file is untouched");
    }

    // (09-19, walk pass 3) The art hook runs BEFORE the merge makes a new item, so the host had nowhere to put a
    // new game's age ratings and dropped them: after a first scan no game had any and Hide mature content hid nothing.
    [Test]
    public async Task Metadata_fetched_for_a_brand_new_item_lands_on_it_at_the_merge()
    {
        using var rig = new Rig();
        var svc = rig.Sync(baseline: true);
        svc.FetchArtAsync = (id, _) =>
        {
            if (id == 2) svc.StashMeta(id, new[] { "English" }, new Dictionary<string, int> { ["PEGI"] = 18 });
            return Task.CompletedTask;
        };
        await svc.RunAsync();

        Assert.Equal(18, rig.Item(2).AgeRatings["PEGI"], "the rating survives the first scan");
        Assert.True(new HiddenPolicy(true, 18, null, null).IsHidden(rig.Item(2)), "so Hide mature content can act on it");
        Assert.Equal(0, rig.Item(1).AgeRatings.Count, "a game with no stashed metadata is untouched");
    }

    [Test]
    public async Task A_later_scan_flags_what_the_manifest_lacked_and_ignores_isNew()
    {
        using var rig = new Rig();
        await rig.Sync(baseline: true).RunAsync();
        NewItems.ClearAll(rig.Store.Current);            // start the second scan from a clean slate

        // Product 3 appears, and GOG says it is NOT new. A later scan must not care.
        rig.Gog.Owned.Add(3);
        await rig.Sync(baseline: false).RunAsync();

        Assert.Equal(3, rig.Store.Current.Items.Count, "the new product was added");
        Assert.True(rig.Item(3).IsNew, "absent from the manifest = New, whatever isNew says");
        Assert.False(rig.Item(1).IsNew, "an item that was already here is not new");
        Assert.False(rig.Item(2).IsNew, "an item that was already here is not new");
    }

    [Test]
    public async Task A_rescan_never_touches_an_existing_items_flag()
    {
        using var rig = new Rig();
        await rig.Sync(baseline: true).RunAsync();
        rig.Item(1).IsNew = true;                        // set by hand: the merge must leave it alone
        rig.Item(2).IsNew = false;

        await rig.Sync(baseline: false).RunAsync();

        Assert.True(rig.Item(1).IsNew, "kept flagged across a rescan");
        Assert.False(rig.Item(2).IsNew, "kept unflagged across a rescan");
    }

    [Test]
    public async Task ClearIfComplete_clears_only_when_every_in_scope_file_is_present()
    {
        using var rig = new Rig();
        await rig.Sync(baseline: false).RunAsync();
        var item = rig.Item(1);
        item.Files.Add(new GameFile
        {
            GameGogId = item.GogId, FileKey = "/downlink/game_1/extra0", Name = "Soundtrack",
            Kind = FileKind.Extra, ExpectedSizeBytes = 5,
        });
        Assert.True(item.IsNew, "the scan flagged it");

        // Installer present, extra still missing: incomplete against a games+extras scope.
        item.Files.First(f => f.Kind == FileKind.Installer).State = FileState.Present;
        Assert.False(NewItems.ClearIfComplete(rig.Store.Current, item.GogId, Scope.Both), "still has a gap");
        Assert.True(item.IsNew, "flag survives an incomplete item");

        // The SAME item is complete against a games-only scope: the scope decides, not the file list.
        Assert.True(NewItems.ClearIfComplete(rig.Store.Current, item.GogId, Scope.GamesOnly), "complete in scope");
        Assert.False(item.IsNew, "flag cleared when the item is fully backed up in scope");
        Assert.False(NewItems.ClearIfComplete(rig.Store.Current, item.GogId, Scope.GamesOnly), "second call changes nothing");
    }

    [Test]
    public async Task Clear_and_ClearAll_retire_the_flag()
    {
        using var rig = new Rig();
        await rig.Sync(baseline: false).RunAsync();
        Assert.Equal(2, NewItems.Count(rig.Store.Current), "both items flagged");
        Assert.Equal(2, NewItems.Flagged(rig.Store.Current).Count, "Flagged returns the items");

        Assert.Equal(1, NewItems.Clear(rig.Store.Current, new long[] { 1 }), "one changed");
        Assert.Equal(0, NewItems.Clear(rig.Store.Current, new long[] { 1 }), "already clear");
        Assert.Equal(1, NewItems.Count(rig.Store.Current), "one left");

        Assert.Equal(1, NewItems.ClearAll(rig.Store.Current), "the last one");
        Assert.Equal(0, NewItems.Count(rig.Store.Current), "nothing flagged");
    }

    [Test]
    public void A_manifest_without_the_field_deserialises_as_not_new()
    {
        // Exactly what an existing library looks like: no IsNew anywhere. Zero migration code, and the
        // whole library must read as not-new.
        const string json = "{\"Items\":[{\"GogId\":42,\"Title\":\"Old Game\",\"Slug\":\"old_game\"}]}";
        var m = JsonSerializer.Deserialize<LibraryManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(m);
        Assert.Equal(1, m!.Items.Count, "one item read back");
        Assert.False(m.Items[0].IsNew, "a missing bool is false");
        Assert.Equal(0, NewItems.Count(m), "nothing flagged");
    }
}

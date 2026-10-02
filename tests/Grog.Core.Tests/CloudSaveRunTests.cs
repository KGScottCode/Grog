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
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Auth;
using Grog.Core.CloudSaves;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Runs;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

/// <summary>The shared cloud-save orchestrator (S2.1): the App's DownloadLocalSaveForAsync / Download All /
/// retention rules and the CLI's `cloudsaves --all` / `cloudsaves prune`, over a temp root + JsonManifestStore
/// and a fake GOG cloud (build meta -> game token -> save list -> per-file download).</summary>
[NewBatch]
[Trait("cloud")]
public class CloudSaveRunTests
{
    // ---- fake GOG cloud ------------------------------------------------------------------------

    /// <summary>Serves the whole per-game cloud flow for clientId "cid1": builds -> meta (plain JSON; the zlib
    /// path falls back) -> scoped token -> v1 save list -> two save files. Counts requests per host.</summary>
    sealed class FakeCloud : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        public bool RefuseList;   // GOG "refuses": the list 403s -> 0 files written
        public CancellationTokenSource? CancelOnSecondFile;   // the user cancels while file 2 is in flight
        public bool RefuseSecondFile;   // a partial capture: file 2 answers 403

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            Urls.Add(url);
            string? body = null; byte[]? raw = null;
            if (url.Contains("content-system.gog.com/products/") && url.Contains("/builds"))
                body = "{\"items\":[{\"link\":\"https://meta.test/build1\"}]}";
            else if (url.StartsWith("https://meta.test/"))
                body = "{\"clientId\":\"cid1\",\"clientSecret\":\"sec1\"}";
            else if (url.StartsWith("https://auth.gog.com/token"))
                body = "{\"access_token\":\"game-token\"}";
            else if (url.Contains("cloudstorage.gog.com/v1/"))
            {
                if (RefuseList) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                body = "[{\"name\":\"saves/slot1.sav\",\"size\":4,\"download_url\":\"https://dl.gog.com/1\"}," +
                       " {\"name\":\"saves/slot2.sav\",\"size\":5,\"download_url\":\"https://dl.gog.com/2\"}]";
            }
            else if (url == "https://dl.gog.com/1") raw = Encoding.UTF8.GetBytes("one!");
            else if (url == "https://dl.gog.com/2")
            {
                if (CancelOnSecondFile is { } cts) { cts.Cancel(); return Task.FromCanceled<HttpResponseMessage>(cts.Token); }
                if (RefuseSecondFile) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                raw = Encoding.UTF8.GetBytes("two!!");
            }
            else return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = raw is not null ? new ByteArrayContent(raw) : new StringContent(body!, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(res);
        }
    }

    sealed class NoAuth : IAuthService
    {
        public bool HasStoredSession => true;
        public Task<AuthSession> EnsureAuthenticatedAsync(CancellationToken ct = default)
            => Task.FromResult(new AuthSession("at", "rt", DateTimeOffset.UtcNow.AddHours(1), "u"));
        public Task SignOutAsync() => Task.CompletedTask;
    }

    sealed class LogHost : NullBackupHost
    {
        public List<(string Line, bool Error)> Lines { get; } = new();
        public override void Log(string message, bool isError = false) => Lines.Add((message, isError));
    }

    // ---- harness -------------------------------------------------------------------------------

    sealed record Env(JsonManifestStore Store, string Root, BackupLayout Layout, FakeCloud Cloud, HttpClient Http, LogHost Host)
    {
        public CloudSaveRun Run(DateTimeOffset? now = null) =>
            new(Store, Http, new NoAuth(), null, Root, Host)
            {
                ServiceFor = _ => new CloudSaveService(Http, new NoAuth()),
                Now = () => now ?? DateTimeOffset.Now,
            };

        /// <summary>The per-account per-game archive dir, resolved the way the run resolves it.</summary>
        public string GameDir(string accountId, string slug)
        {
            var cloudBase = Layout.ResolveCloudBaseDir(out _) ?? Path.Combine(Root, "Cloud Saves");
            return CloudSaveReconciler.GameArchiveDir(Store.Current, cloudBase, accountId, slug);
        }
    }

    static Env NewSetup()
    {
        var root = Directory.CreateTempSubdirectory("grog-cloudrun-").FullName;
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(root, "_cfg"));
        var store = new JsonManifestStore(GrogPaths.Resolve(root));
        store.LoadAsync().GetAwaiter().GetResult();
        var layout = new BackupLayout(store.Current, root);
        store.Current.Accounts.Add(new GrogAccount { Id = "acc1", Username = "Bob" });
        var cloud = new FakeCloud();
        return new Env(store, root, layout, cloud, new HttpClient(cloud), new LogHost());
    }

    /// <summary>A game GOG's discovery confirmed has saves for account acc1 (the shape ApplyAccounts leaves).</summary>
    static (LibraryItem Item, CloudAccountSave Entry) AddCloudGame(Env e, long gogId, string slug, string title, string clientId = "cid1")
    {
        var item = new LibraryItem { GogId = gogId, Title = title, Slug = slug, HasCloudSaves = true, CloudClientId = clientId };
        var entry = new CloudAccountSave
        {
            AccountId = "acc1", SizeBytes = 9, Files = 2, UpdatedUtc = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), SpaceId = "sp",
        };
        item.CloudByAccount.Add(entry);
        e.Store.Current.Items.Add(item);
        return (item, entry);
    }

    static string AddLocalSave(string gameDir, string stamp, string content = "x")
    {
        var dir = Path.Combine(gameDir, "cloud-saves", stamp);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "slot.sav"), content);
        return dir;
    }

    // ---- download ------------------------------------------------------------------------------

    // Sweep 2 #6: a cancel after file 1 left a partial dated folder, and retention then pruned the complete
    // older snapshot to make room for it.
    [Test]
    async Task ACancelledCapture_LeavesNoFolder_AndPrunesNothing()
    {
        var e = NewSetup();
        try
        {
            AddCloudGame(e, 10, "orc-quest", "Orc Quest");
            var gameDir = e.GameDir("acc1", "orc-quest");
            var old = AddLocalSave(gameDir, "20260101-000000");
            using var cts = new CancellationTokenSource();
            e.Cloud.CancelOnSecondFile = cts;

            var r = await e.Run().DownloadAsync(CloudSaveSelection.All, keepPerGame: 1, cts.Token);

            Assert.True(r.Canceled, "the run reports the cancel");
            Assert.True(Directory.Exists(old), "the complete older snapshot survives");
            Assert.Equal(1, LocalSaveArchive.List(gameDir).Count, "the cancelled capture left no dated folder");
        }
        finally { try { Directory.Delete(e.Root, true); } catch { } }
    }

    [Test]
    async Task APartialCapture_NeverPrunesACompleteOne()
    {
        var e = NewSetup();
        try
        {
            AddCloudGame(e, 10, "orc-quest", "Orc Quest");
            var gameDir = e.GameDir("acc1", "orc-quest");
            var old = AddLocalSave(gameDir, "20260101-000000");
            e.Cloud.RefuseSecondFile = true;

            var r = await e.Run().DownloadAsync(CloudSaveSelection.All, keepPerGame: 1, CancellationToken.None);

            var one = Assert.Single(r.Entries);
            Assert.True(one.Partial, "1 of 2 files");
            Assert.True(Directory.Exists(old), "retention waits for a complete capture");
        }
        finally { try { Directory.Delete(e.Root, true); } catch { } }
    }

    [Test]
    async Task Download_WritesOneDatedLocalSave_AndRecordsBookkeeping()
    {
        var e = NewSetup();
        try
        {
            var (item, entry) = AddCloudGame(e, 10, "orc-quest", "Orc Quest", clientId: "");   // no cached clientId: resolved once
            var when = new DateTimeOffset(2026, 9, 8, 9, 30, 0, TimeSpan.Zero);
            var r = await e.Run(when).DownloadAsync(CloudSaveSelection.All, keepPerGame: 0, CancellationToken.None);

            Assert.False(r.Canceled);
            var one = Assert.Single(r.Entries);
            Assert.True(one.Ok, one.Error ?? "ok");
            Assert.Equal(2, one.FilesWritten); Assert.Equal(2, one.FilesExpected); Assert.False(one.Partial);
            Assert.Equal("acc1", one.AccountId);

            // The archive: <cloud base>/<account>/<slug>/cloud-saves/<stamp>/saves/slotN.sav, per account.
            var gameDir = e.GameDir("acc1", "orc-quest");
            Assert.Contains(Path.Combine("bob", "orc-quest"), gameDir, "this ACCOUNT's folder first, game inside it");
            Assert.True(one.LocalSaveDir!.StartsWith(Path.Combine(gameDir, "cloud-saves")), one.LocalSaveDir);
            Assert.Equal("one!", File.ReadAllText(Path.Combine(one.LocalSaveDir!, "saves", "slot1.sav")));
            Assert.Equal("two!!", File.ReadAllText(Path.Combine(one.LocalSaveDir!, "saves", "slot2.sav")));
            var saves = LocalSaveArchive.List(gameDir);
            Assert.Equal(1, saves.Count, "exactly one dated local save");

            // Bookkeeping: per-account entry first, legacy single-slot fields mirrored from it.
            Assert.Equal("cid1", item.CloudClientId, "clientId resolved from build meta and cached");
            Assert.Equal(when, entry.LastBackup);
            Assert.Equal(1, entry.LocalSaveCount, "recounted from disk");
            Assert.Equal(2, entry.BackedUpFiles);
            Assert.Equal(entry.SizeBytes, entry.BackedUpSize, "complete capture records the cloud fingerprint");
            Assert.Equal(entry.UpdatedUtc, entry.BackedUpChangeUtc);
            Assert.True(CloudSaveRun.IsUpToDate(entry), "now up to date");
            Assert.Equal(entry.LastBackup, item.CloudLastBackup, "legacy mirror: last backup");
            Assert.Equal(1, item.CloudLocalSaveCount, "legacy mirror: count");
            Assert.Equal(2, item.CloudBackedUpFiles, "legacy mirror: files");
            Assert.Equal(entry.SizeBytes, item.CloudBackedUpSize, "legacy mirror: size");
            Assert.Equal(entry.UpdatedUtc, item.CloudBackedUpChangeUtc, "legacy mirror: change stamp");

            // Persisted.
            var reloaded = new JsonManifestStore(GrogPaths.Resolve(e.Root));
            await reloaded.LoadAsync();
            var back = reloaded.Current.ItemById(10)!;
            Assert.Equal(1, back.CloudByAccount[0].LocalSaveCount, "entry saved");
            Assert.Equal("cid1", back.CloudClientId, "clientId saved");
            Assert.True(e.Host.Lines.Any(l => !l.Error && l.Line.Contains("backed up: Orc Quest (2 files)")), "host log line");

            // A second Download All skips the now up-to-date game and fetches nothing.
            e.Cloud.Urls.Clear();
            var again = await e.Run().DownloadAsync(CloudSaveSelection.All, 0, CancellationToken.None);
            Assert.Equal(1, again.Skipped);
            Assert.Empty(e.Cloud.Urls, "nothing fetched for an up-to-date game");
            Assert.Equal(1, LocalSaveArchive.List(gameDir).Count, "no second local save");
        }
        finally { e.Http.Dispose(); Directory.Delete(e.Root, true); }
    }

    [Test]
    async Task Download_Refused_WritesNothing_AndMarksNothing()
    {
        var e = NewSetup();
        try
        {
            var (item, entry) = AddCloudGame(e, 11, "refused", "Refused");
            e.Cloud.RefuseList = true;
            var r = await e.Run().DownloadAsync(CloudSaveSelection.ForGames(new long[] { 11 }), 0, CancellationToken.None);
            var one = Assert.Single(r.Entries);
            Assert.True(one.Failed); Assert.Equal(0, one.FilesWritten);
            Assert.NotNull(one.Error, "a 403 on the save list is an error, never 'no saves'");
            Assert.Equal(0, entry.LocalSaveCount, "not marked as backed up");
            Assert.Null(entry.LastBackup);
            Assert.Null(item.CloudLastBackup);
            Assert.Empty(LocalSaveArchive.List(e.GameDir("acc1", "refused")), "no empty dated folder left behind");
            Assert.True(e.Host.Lines.Any(l => l.Error && l.Line.Contains("FAILED: Refused")), "host told");
        }
        finally { e.Http.Dispose(); Directory.Delete(e.Root, true); }
    }

    [Test]
    async Task Download_UnknownGameId_IsReportedNotIgnored()
    {
        var e = NewSetup();
        try
        {
            AddCloudGame(e, 12, "known", "Known");
            var r = await e.Run().DownloadAsync(CloudSaveSelection.ForGames(new long[] { 999 }), 0, CancellationToken.None);
            var one = Assert.Single(r.Entries);
            Assert.Equal(999L, one.GogId); Assert.True(one.Failed); Assert.NotNull(one.Error);
            Assert.Empty(e.Cloud.Urls, "the known game was not selected");
        }
        finally { e.Http.Dispose(); Directory.Delete(e.Root, true); }
    }

    // ---- retention -----------------------------------------------------------------------------

    [Test]
    async Task Download_WithKeep_PrunesOlderLocalSaves_AfterTheNewOneLands()
    {
        var e = NewSetup();
        try
        {
            var (_, entry) = AddCloudGame(e, 13, "keeper", "Keeper");
            var gameDir = e.GameDir("acc1", "keeper");
            AddLocalSave(gameDir, "20240101-000000");
            AddLocalSave(gameDir, "20240102-000000");
            entry.LocalSaveCount = 2;

            var r = await e.Run().DownloadAsync(CloudSaveSelection.All, keepPerGame: 2, CancellationToken.None, skipUpToDate: false);
            var one = Assert.Single(r.Entries);
            Assert.True(one.Ok, one.Error ?? "ok");
            Assert.Equal(1, one.Pruned, "three on disk, keep two -> the oldest goes");
            var left = LocalSaveArchive.List(gameDir);
            Assert.Equal(2, left.Count);
            Assert.False(Directory.Exists(Path.Combine(gameDir, "cloud-saves", "20240101-000000")), "oldest deleted");
            Assert.True(Directory.Exists(Path.Combine(gameDir, "cloud-saves", "20240102-000000")), "newer kept");
            Assert.Equal(2, entry.LocalSaveCount, "count recomputed from disk");
        }
        finally { e.Http.Dispose(); Directory.Delete(e.Root, true); }
    }

    [Test]
    async Task Prune_KeepsNewestN_DeletesTheRest_AndRederivesBookkeeping()
    {
        var e = NewSetup();
        try
        {
            var (item, entry) = AddCloudGame(e, 14, "pruned", "Pruned");
            var gameDir = e.GameDir("acc1", "pruned");
            AddLocalSave(gameDir, "20240101-000000");
            AddLocalSave(gameDir, "20240103-000000");
            AddLocalSave(gameDir, "20240102-000000");
            entry.LocalSaveCount = 3; entry.LastBackup = DateTimeOffset.Now; entry.BackedUpFiles = 2; entry.BackedUpSize = 9;
            CloudSaveReconciler.MirrorLegacy(item);

            // A second game under the cap is untouched.
            var (_, small) = AddCloudGame(e, 15, "small", "Small");
            AddLocalSave(e.GameDir("acc1", "small"), "20240101-000000");
            small.LocalSaveCount = 1;

            var r = await e.Run().PruneAsync(keepPerGame: 2, CancellationToken.None);
            Assert.Equal(1, r.Deleted); Assert.Equal(1, r.Touched); Assert.Equal(2, r.KeepPerGame);
            Assert.False(Directory.Exists(Path.Combine(gameDir, "cloud-saves", "20240101-000000")), "oldest deleted");
            Assert.True(Directory.Exists(Path.Combine(gameDir, "cloud-saves", "20240102-000000")));
            Assert.True(Directory.Exists(Path.Combine(gameDir, "cloud-saves", "20240103-000000")));
            Assert.Equal(2, entry.LocalSaveCount);
            Assert.Equal(new DateTimeOffset(2024, 1, 3, 0, 0, 0, TimeSpan.Zero), entry.LastBackup, "last backup = newest remaining stamp");
            Assert.Equal(2, entry.BackedUpFiles, "an OLD local save going does not change what the latest captured");
            Assert.Equal(2, item.CloudLocalSaveCount, "legacy mirror follows");
            Assert.Equal(1, small.LocalSaveCount, "under the cap: untouched");
            Assert.Empty(e.Cloud.Urls, "retention never talks to GOG");

            var reloaded = new JsonManifestStore(GrogPaths.Resolve(e.Root));
            await reloaded.LoadAsync();
            Assert.Equal(2, reloaded.Current.ItemById(14)!.CloudByAccount[0].LocalSaveCount, "saved");

            // keep <= 0 is a no-op.
            var none = await e.Run().PruneAsync(0, CancellationToken.None);
            Assert.Equal(0, none.Deleted);
            Assert.Equal(2, LocalSaveArchive.List(gameDir).Count);
        }
        finally { e.Http.Dispose(); Directory.Delete(e.Root, true); }
    }

    [Test]
    async Task Prune_KeepOne_LeavesOnlyTheNewest()
    {
        var e = NewSetup();
        try
        {
            // keep=1 (the App's "Keep latest only"): one local save survives and the capture fingerprint stands.
            var (item, entry) = AddCloudGame(e, 16, "ghost", "Ghost");
            var gameDir = e.GameDir("acc1", "ghost");
            AddLocalSave(gameDir, "20240101-000000");
            AddLocalSave(gameDir, "20240102-000000");
            entry.LocalSaveCount = 2; entry.BackedUpFiles = 2; entry.BackedUpSize = 9;
            var r = await e.Run().PruneAsync(1, CancellationToken.None);
            Assert.Equal(1, r.Deleted);
            Assert.Equal(1, entry.LocalSaveCount);
            Assert.Equal(2, entry.BackedUpFiles, "one remains: the capture fingerprint stands");
            Assert.Equal(1, item.CloudLocalSaveCount);
        }
        finally { e.Http.Dispose(); Directory.Delete(e.Root, true); }
    }
}

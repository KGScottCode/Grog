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
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Runs;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

/// <summary>Core hardening pass: server-supplied save names stay inside the save folder and the bearer token
/// stays on gog.com (B4), a failed capture leaves no dated folder (B5), the save list treats only 404 as
/// empty (B6), and the settler reports a flush that outran its bound (E9).</summary>
[NewBatch]
[Trait("cloud")]
public sealed class CoreHardeningTests
{
    // ---- cloud rig ------------------------------------------------------------------------------

    /// <summary>The per-game cloud flow for clientId "cid1" with a configurable save list and file answers.</summary>
    sealed class FakeCloud : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        public string ListBody = "[]";
        public HttpStatusCode ListStatus = HttpStatusCode.OK;
        public Dictionary<string, Func<HttpResponseMessage>> Files { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            Urls.Add(url);
            string? body = null;
            if (url.Contains("content-system.gog.com/products/") && url.Contains("/builds"))
                body = "{\"items\":[{\"link\":\"https://meta.gog.com/build1\"}]}";
            else if (url.StartsWith("https://meta.gog.com/"))
                body = "{\"clientId\":\"cid1\",\"clientSecret\":\"sec1\"}";
            else if (url.StartsWith("https://auth.gog.com/token"))
                body = "{\"access_token\":\"game-token\"}";
            else if (url.Contains("cloudstorage.gog.com/v1/"))
            {
                if (ListStatus != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(ListStatus));
                body = ListBody;
            }
            else if (Files.TryGetValue(url, out var make))
                return Task.FromResult(make());
            else return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body!, Encoding.UTF8, "application/json"),
            });
        }

        public static HttpResponseMessage Bytes(string s)
            => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(s)) };
    }

    sealed class NoAuth : IAuthService
    {
        public bool HasStoredSession => true;
        public Task<AuthSession> EnsureAuthenticatedAsync(CancellationToken ct = default)
            => Task.FromResult(new AuthSession("at", "rt", DateTimeOffset.UtcNow.AddHours(1), "u"));
        public Task SignOutAsync() => Task.CompletedTask;
    }

    static (CloudSaveService Svc, FakeCloud Cloud, List<string> Log, string GameDir) Rig()
    {
        var cloud = new FakeCloud();
        var log = new List<string>();
        var svc = new CloudSaveService(new HttpClient(cloud), new NoAuth()) { Log = log.Add };
        var gameDir = Directory.CreateTempSubdirectory("grog-harden-").FullName;
        return (svc, cloud, log, gameDir);
    }

    static string Entry(string name, string url) => $"{{\"name\":\"{name}\",\"size\":4,\"download_url\":\"{url}\"}}";
    static CloudGame Game => new(10, "Orc Quest", "orc-quest", "cid1");
    static string[] DatedFolders(string gameDir)
    {
        var root = Path.Combine(gameDir, "cloud-saves");
        return Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>();
    }

    // ---- B4: names and hosts ------------------------------------------------------------------

    [Test]
    public async Task A_save_name_that_escapes_the_folder_is_skipped_and_a_nested_one_lands()
    {
        var (svc, cloud, log, gameDir) = Rig();
        try
        {
            cloud.ListBody = "[" + Entry("../../escape.sav", "https://dl.gog.com/1") + "," +
                                   Entry("saves/nested/ok.sav", "https://dl.gog.com/2") + "]";
            cloud.Files["https://dl.gog.com/1"] = () => FakeCloud.Bytes("bad!");
            cloud.Files["https://dl.gog.com/2"] = () => FakeCloud.Bytes("good");

            var (dir, written, expected) = await svc.DownloadLocalSaveAsync(Game, gameDir);

            Assert.Equal(1, written, "only the well-formed entry was written");
            Assert.Equal(2, expected, "both entries were offered");
            Assert.Equal("good", File.ReadAllText(Path.Combine(dir, "saves", "nested", "ok.sav")), "the nested save landed in place");
            Assert.False(File.Exists(Path.GetFullPath(Path.Combine(dir, "..", "..", "escape.sav"))), "nothing was written outside the dated folder");
            Assert.False(cloud.Urls.Contains("https://dl.gog.com/1"), "the escaping entry was never even fetched");
            Assert.True(log.Any(l => l.Contains("escape")), "the skip was logged");
        }
        finally { try { Directory.Delete(gameDir, true); } catch { } }
    }

    [Test]
    public void SafeDestination_rejects_rooted_and_parent_names_and_accepts_nested_ones()
    {
        var root = Path.Combine(Path.GetTempPath(), "grog-safe-root");
        Assert.Null(CloudSaveService.SafeDestination(root, "../x"), "parent escape");
        Assert.Null(CloudSaveService.SafeDestination(root, "a/../../x"), "parent escape after a hop");
        Assert.Null(CloudSaveService.SafeDestination(root, "..\\..\\x"), "backslash parent escape");
        Assert.Null(CloudSaveService.SafeDestination(root, ""), "empty name");
        var ok = CloudSaveService.SafeDestination(root, "saves/slot 1.sav");
        Assert.NotNull(ok, "a nested relative name is fine");
        Assert.True(ok!.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar), "and stays under the root");
        Assert.NotNull(CloudSaveService.SafeDestination(root, "/saves/lead.sav"), "a leading slash is trimmed, not treated as rooted");
    }

    [Test]
    public async Task A_download_url_on_a_foreign_host_is_refused_and_never_sees_the_token()
    {
        var (svc, cloud, log, gameDir) = Rig();
        try
        {
            cloud.ListBody = "[" + Entry("saves/slot1.sav", "https://evil.example/steal") + "]";
            cloud.Files["https://evil.example/steal"] = () => FakeCloud.Bytes("x");

            var (_, written, _) = await svc.DownloadLocalSaveAsync(Game, gameDir);

            Assert.Equal(0, written, "nothing written from a stranger");
            Assert.False(cloud.Urls.Any(u => u.StartsWith("https://evil.example/")), "the foreign host was never contacted");
            Assert.True(log.Any(l => l.Contains("gog.com")), "the refusal was logged");
            Assert.Empty(DatedFolders(gameDir), "an empty capture leaves no dated folder");
        }
        finally { try { Directory.Delete(gameDir, true); } catch { } }
    }

    [Test]
    public void Trusted_hosts_are_gog_com_and_its_subdomains_over_https()
    {
        Assert.True(CloudSaveService.IsTrustedDownloadHost("https://cloudstorage.gog.com/v1/u/c/f"), "subdomain");
        Assert.True(CloudSaveService.IsTrustedDownloadHost("https://gog.com/x"), "apex");
        Assert.False(CloudSaveService.IsTrustedDownloadHost("https://gog.com.evil.example/x"), "lookalike suffix");
        Assert.False(CloudSaveService.IsTrustedDownloadHost("https://notgog.com/x"), "lookalike apex");
        Assert.False(CloudSaveService.IsTrustedDownloadHost("http://cloudstorage.gog.com/x"), "plain http");
        Assert.False(CloudSaveService.IsTrustedDownloadHost("not a url"), "garbage");
    }

    // ---- B5: a failed capture leaves nothing --------------------------------------------------

    [Test]
    public async Task A_transport_failure_on_the_second_file_leaves_no_dated_folder()
    {
        var (svc, cloud, _, gameDir) = Rig();
        try
        {
            cloud.ListBody = "[" + Entry("saves/slot1.sav", "https://dl.gog.com/1") + "," +
                                   Entry("saves/slot2.sav", "https://dl.gog.com/2") + "]";
            cloud.Files["https://dl.gog.com/1"] = () => FakeCloud.Bytes("one!");
            cloud.Files["https://dl.gog.com/2"] = () => throw new HttpRequestException("connection reset");

            bool threw = false;
            try { await svc.DownloadLocalSaveAsync(Game, gameDir); }
            catch (HttpRequestException) { threw = true; }

            Assert.True(threw, "the failure surfaces to the run");
            Assert.Empty(DatedFolders(gameDir), "the half-written dated folder is gone, so retention cannot count it");
        }
        finally { try { Directory.Delete(gameDir, true); } catch { } }
    }

    [Test]
    public async Task A_complete_capture_leaves_no_part_files()
    {
        var (svc, cloud, _, gameDir) = Rig();
        try
        {
            cloud.ListBody = "[" + Entry("saves/slot1.sav", "https://dl.gog.com/1") + "]";
            cloud.Files["https://dl.gog.com/1"] = () => FakeCloud.Bytes("one!");
            var (dir, written, _) = await svc.DownloadLocalSaveAsync(Game, gameDir);
            Assert.Equal(1, written);
            Assert.Empty(Directory.GetFiles(dir, "*.part", SearchOption.AllDirectories), "the .part was renamed away");
            Assert.Equal("one!", File.ReadAllText(Path.Combine(dir, "saves", "slot1.sav")));
        }
        finally { try { Directory.Delete(gameDir, true); } catch { } }
    }

    // ---- B6: only 404 is "no saves" -----------------------------------------------------------

    [Test]
    public async Task List_404_is_empty_but_500_throws()
    {
        var (svc, cloud, _, gameDir) = Rig();
        try
        {
            cloud.ListStatus = HttpStatusCode.NotFound;
            Assert.Empty(await svc.ListSavesAsync("cid1", "game-token"), "404 = the game has no cloud saves");

            cloud.ListStatus = HttpStatusCode.InternalServerError;
            bool threw = false;
            try { await svc.ListSavesAsync("cid1", "game-token"); }
            catch (HttpRequestException) { threw = true; }
            Assert.True(threw, "a 500 is an error, never an empty capture");
        }
        finally { try { Directory.Delete(gameDir, true); } catch { } }
    }

    // ---- E9: the settler's flush bound --------------------------------------------------------

    sealed class MemoryStore : IManifestStore
    {
        public MemoryStore(LibraryManifest m) => Current = m;
        public LibraryManifest Current { get; }
        public ManifestGate Gate { get; } = new();
        public int Saves;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) { Saves++; return Task.CompletedTask; }
    }

    sealed class BlockingHost : NullBackupHost
    {
        public readonly ManualResetEventSlim Release = new(false);
        public readonly ManualResetEventSlim Entered = new(false);
        public override void TaskSettled(DownloadTask task, DownloadSettlement.Outcome outcome)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
        }
    }

    static (MemoryStore Store, DownloadEngine Engine) SettlerRig()
    {
        var m = new LibraryManifest();
        m.Roots.Add(new BackupRoot { Id = "p", Label = "Primary" });
        m.PrimaryRootId = "p";
        for (long g = 1; g <= 3; g++)
        {
            m.Items.Add(new LibraryItem { GogId = g, Title = "g" + g, Slug = "g" + g, Type = ProductType.Game,
                Files = { new GameFile { GameGogId = g, FileKey = "f" + g, Name = "f" + g,
                                         Kind = FileKind.Installer, State = FileState.NotBackedUp,
                                         ExpectedSizeBytes = 10, Os = "windows", Language = "English" } } });
            m.Downloads.Enqueue(g, "f" + g, "p");
        }
        var e = new DownloadEngine(null!, new HttpClient()) { BackupRoot = Path.GetTempPath() };
        return (new MemoryStore(m), e);
    }

    [Test]
    public async Task Flush_past_its_bound_saves_anyway_and_reports_unsettled_work()
    {
        var (store, engine) = SettlerRig();
        var host = new BlockingHost();
        using var settler = new RunSettler(store, engine, null, host) { FlushBound = TimeSpan.FromMilliseconds(200) };
        BackupQueueBuilder.ReplanWholeQueue(store, new[] { new DeviceSpace("p", 100_000, true) }, engine);

        Assert.False(settler.HasUnsettledWork, "nothing owed before any work");
        var first = engine.Snapshot.First();
        first.State = DownloadTaskState.Completed;
        engine.CancelRange(engine.Snapshot.Where(t => t.State == DownloadTaskState.Pending).Take(1).ToList());
        Assert.True(host.Entered.Wait(TimeSpan.FromSeconds(5)), "the settler reached the blocking host");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await settler.FlushAsync();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"the flush returned at the bound, not when the host let go ({sw.ElapsedMilliseconds} ms)");
        Assert.True(settler.HasUnsettledWork, "the bound expired, so the save may hold unsettled records");
        var savesAfterFirst = store.Saves;
        Assert.True(savesAfterFirst >= 1, "saved what was settled");

        // The host lets go: a second flush is safe, waits its turn, and clears the flag.
        host.Release.Set();
        settler.FlushBound = TimeSpan.FromSeconds(10);
        await settler.FlushAsync();
        Assert.False(settler.HasUnsettledWork, "once the worker caught up nothing is owed");
        Assert.True(store.Saves > savesAfterFirst, "and the second flush saved again");
    }

    [Test]
    public async Task Checkpoint_saves_once_per_window_then_once_more_at_the_window_end_and_the_default_window_is_ten_seconds()
    {
        var (store, engine) = SettlerRig();
        using (var dflt = new RunSettler(store, engine, null, new NullBackupHost()))
            Assert.Equal(TimeSpan.FromSeconds(10), dflt.CheckpointEvery, "the journal covers crash recovery; the manifest write is coarse");
        using var settler = new RunSettler(store, engine, null, new NullBackupHost(), checkpointEvery: TimeSpan.FromMilliseconds(600));
        BackupQueueBuilder.ReplanWholeQueue(store, new[] { new DeviceSpace("p", 100_000, true) }, engine);

        var tasks = engine.Snapshot.ToList();
        tasks[0].State = DownloadTaskState.Completed;   // SettledAsync forces a pass that sees it
        await settler.SettledAsync();
        for (int i = 0; i < 50 && store.Saves == 0; i++) await Task.Delay(20);
        Assert.Equal(1, store.Saves, "the first change checkpoints at once");

        tasks[1].State = DownloadTaskState.Completed;
        await settler.SettledAsync();
        tasks[2].State = DownloadTaskState.Completed;
        await settler.SettledAsync();
        await Task.Delay(150);
        Assert.Equal(1, store.Saves, "changes inside the window do not write again yet");

        for (int i = 0; i < 100 && store.Saves < 2; i++) await Task.Delay(20);
        Assert.Equal(2, store.Saves, "one trailing save at the window end covers both in-window changes");
        await Task.Delay(700);
        Assert.Equal(2, store.Saves, "and no further save without a further change");

        await settler.FlushAsync();
        Assert.Equal(3, store.Saves, "the final flush is immediate");
    }

    // ---- stall guard ------------------------------------------------------------------------------

    /// <summary>A body whose reads hang until told otherwise.</summary>
    sealed class HangingStream : Stream
    {
        public TaskCompletionSource<int> Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            using var reg = ct.Register(() => Next.TrySetCanceled(ct));
            var n = await Next.Task;
            Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return n;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) { } public override void Write(byte[] b, int o, int c) { }
    }

    [Test]
    public async Task Stall_guard_ignores_a_timer_callback_that_tripped_after_the_disarm()
    {
        var body = new HangingStream();
        using var guard = new StallGuard(CancellationToken.None, TimeSpan.FromSeconds(30));
        var buf = new byte[16];
        var first = guard.ReadAsync(body, buf); body.Next.TrySetResult(16);
        Assert.Equal(16, await first, "first read lands");
        guard.TripForTest();   // the callback that was already running when CancelAfter(Infinite) disarmed it
        var second = guard.ReadAsync(body, buf); body.Next.TrySetResult(8);
        Assert.Equal(8, await second, "the stale trip is not a stall; the next read still gets its bytes");
    }

    [Test]
    public async Task Stall_guard_reports_a_real_stall_and_rethrows_a_user_cancel()
    {
        using var guard = new StallGuard(CancellationToken.None, TimeSpan.FromMilliseconds(200));
        var buf = new byte[16];
        Exception? caught = null;
        try { await guard.ReadAsync(new HangingStream(), buf); } catch (Exception ex) { caught = ex; }
        Assert.True(caught is StallException, $"no data inside the window is a stall, got {caught?.GetType().Name ?? "nothing"}");

        using var user = new CancellationTokenSource();
        using var guard2 = new StallGuard(user.Token, TimeSpan.FromSeconds(30));
        var read = guard2.ReadAsync(new HangingStream(), buf);
        user.Cancel();
        caught = null;
        try { await read; } catch (Exception ex) { caught = ex; }
        Assert.True(caught is OperationCanceledException, "a user cancel is not a stall");
    }

    // ---- token protection readout ---------------------------------------------------------------

    [Test]
    public void An_encrypted_token_file_reads_as_encrypted_even_with_no_key_available()
    {
        if (FileTokenStore.PlainTextPreferred) return;   // the opt-out marker on this machine overrides every header
        var dir = Directory.CreateTempSubdirectory("grog-token-").FullName;
        var path = Path.Combine(dir, "tokens.bin");
        var wasKey = FileTokenStore.KeySource;
        FileTokenStore.KeySource = () => null;   // no keyring key reachable
        try
        {
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("GRGK1\n").Concat(new byte[40]).ToArray());
            var protection = FileTokenStore.ProtectionOf(path);
            Assert.True(protection is Grog.Core.Platform.TokenProtection.LinuxKeyring or Grog.Core.Platform.TokenProtection.MacKeychain,
                        $"the header says keyring-encrypted ({protection})");
            Assert.Equal(Grog.Core.Platform.TokenProtectionReason.Encrypted, FileTokenStore.ProtectionReasonOf(path),
                         "the reason agrees with the header, not with the keyring's mood");
        }
        finally { FileTokenStore.KeySource = wasKey; try { Directory.Delete(dir, true); } catch { } }
    }
}

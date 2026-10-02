// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Download;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

/// <summary>
/// The whole download path against a fake GOG: downlink JSON, the CDN GET (Content-Disposition names the
/// file, Range resumes it), and the checksum XML. Written 09-02 before DownloadOneAsync was split into
/// its stages, so the split had a net under it; keep it green through any engine change.
/// </summary>
[Trait("download")]
public sealed class DownloadEngineEndToEndTests
{
    /// <summary>A body whose stream dies after <c>_good</c> bytes: a dropped connection, not a short file.</summary>
    private sealed class DroppingContent : HttpContent
    {
        private readonly byte[] _bytes; private readonly int _good;
        public DroppingContent(byte[] bytes, int good) { _bytes = bytes; _good = good; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new DropStream(_bytes, _good));
        protected override bool TryComputeLength(out long length) { length = _bytes.Length; return true; }
        private sealed class DropStream : MemoryStream
        {
            private readonly int _good;
            public DropStream(byte[] b, int good) : base(b, writable: false) { _good = good; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Position >= _good) throw new IOException("connection reset by peer");
                return base.Read(buffer, offset, (int)Math.Min(count, _good - Position));
            }
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                var tmp = new byte[buffer.Length];
                int n = Read(tmp, 0, tmp.Length);
                tmp.AsSpan(0, n).CopyTo(buffer.Span);
                return new ValueTask<int>(n);
            }
        }
    }

    private sealed class FakeCdn : HttpMessageHandler
    {
        public byte[] Payload = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");   // 32 bytes
        public string RealName = "setup_game_1.0.exe";
        public bool ServeChecksum = true;
        public int Gets;
        public long? LastRangeFrom;
        public bool FailFirstGetMidway;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("embed.gog.com/downlink"))
            {
                var json = "{\"downlink\":\"https://cdn.test/file?token=1\",\"checksum\":\"https://cdn.test/file.xml?token=1\"}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            if (url.Contains("file.xml"))
            {
                if (!ServeChecksum) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                var md5 = Convert.ToHexString(MD5.HashData(Payload)).ToLowerInvariant();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent($"<file name=\"{RealName}\" md5=\"{md5}\"/>") });
            }
            // The CDN file itself (probe HEAD or the real GET).
            long from = req.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            LastRangeFrom = req.Headers.Range is null ? null : from;
            if (req.Method == HttpMethod.Get) Gets++;
            if (from >= Payload.Length)
            {
                // A real CDN: a Range at or past the end is 416 with the true length in Content-Range.
                var r416 = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent(System.Array.Empty<byte>()) };
                r416.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(Payload.Length);
                return Task.FromResult(r416);
            }
            var body = Payload.Skip((int)from).ToArray();
            bool drop = req.Method == HttpMethod.Get && FailFirstGetMidway && Gets == 1;
            if (drop) FailFirstGetMidway = false;
            var resp = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { Content = drop ? new DroppingContent(body, 10) : new ByteArrayContent(body) };   // the connection dies after 10 bytes
            resp.Content.Headers.ContentLength = body.Length;
            resp.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = RealName };
            return Task.FromResult(resp);
        }
    }

    private sealed class NoAuth : IAuthService
    {
        public bool HasStoredSession => true;
        public Task<AuthSession> EnsureAuthenticatedAsync(CancellationToken ct = default)
            => Task.FromResult(new AuthSession("at", "rt", DateTimeOffset.UtcNow.AddHours(1), "u"));
        public Task SignOutAsync() => Task.CompletedTask;
    }

    private static (DownloadEngine Engine, FakeCdn Cdn, string Root, DownloadTask Task) Rig(long? advertised = 32)
    {
        var cdn = new FakeCdn();
        var http = new HttpClient(cdn);
        var api = new GogApiClient(http, new NoAuth());
        var root = Path.Combine(Path.GetTempPath(), "grog-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var engine = new DownloadEngine(api, http) { BackupRoot = root, MaxConcurrent = 1, MaxBackoffRetries = 0, ClusterBytesProbe = _ => 1 };   // byte-exact room in these tests (1288)
        var file = new GameFile { GameGogId = 42, FileKey = "/downlink/game/1", Kind = FileKind.Installer, Name = "Setup (Part 1 of 1)", Os = "windows", ExpectedSizeBytes = advertised };
        var task = new DownloadTask { File = file, GameTitle = "My Game", GameSlug = "my_game" };
        return (engine, cdn, root, task);
    }

    private static void Cleanup(string root) { try { Directory.Delete(root, recursive: true); } catch { /* best effort */ } }

    private static async Task RunAsync(DownloadEngine e, DownloadTask t)
    {
        e.Enqueue(t);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await e.RunToCompletionAsync(cts.Token);
    }

    [Test]
    public async Task Concurrent_files_on_one_device_all_land_and_the_temp_folder_goes_with_the_last()
    {
        // Two workers, six files, one .grog-tmp: every finisher removes the folder only when it is empty, and
        // a sibling caught between creating the folder and opening its .part retries the open once (review
        // 09-06). A removal under a sibling's fresh .part failed that sibling with DirectoryNotFound (09-05).
        var rootDir = "";
        try
        {
            var (e, _, root, first) = Rig(); rootDir = root;
            e.MaxConcurrent = 2;
            var trace = new System.Text.StringBuilder(); e.DebugTrace += m => { lock (trace) trace.AppendLine(m); };
            var tasks = new List<DownloadTask> { first };
            for (int i = 2; i <= 6; i++)
                tasks.Add(new DownloadTask
                {
                    // The fake CDN names every file the same, so each task gets its own game folder: a real
                    // download each, not an adopt of the first file's bytes.
                    File = new GameFile { GameGogId = 40 + i, FileKey = $"/downlink/game/{i}", Kind = FileKind.Installer, Name = $"Setup {i}", ExpectedSizeBytes = 32 },
                    GameTitle = $"Game {i}", GameSlug = $"game_{i}",
                });
            foreach (var t in tasks) e.Enqueue(t);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await e.RunToCompletionAsync(cts.Token);

            foreach (var t in tasks) Assert.Equal(DownloadTaskState.Completed, t.State, (t.Error ?? $"{t.File.FileKey} state") + "\n" + trace);
            var tmp = Path.Combine(root, ".grog-tmp");
            // The last worker's tidy-up may retry once after a transient lock: give it up to 2 s.
            for (int i = 0; i < 40 && Directory.Exists(tmp); i++) await Task.Delay(50);
            var left = Directory.Exists(tmp) ? string.Join(",", Directory.EnumerateFileSystemEntries(tmp).Select(Path.GetFileName)) : "(gone)";
            Assert.False(Directory.Exists(tmp), $"the temp folder goes with the last file on the device; left: [{left}]");
        }
        finally { Cleanup(rootDir); }
    }

    [Test]
    public async Task A_file_lands_under_its_real_name_verified_and_recorded()
    {
        var rootDir = "";
        try
        {
            var (e, cdn, root, t) = Rig(); rootDir = root;
            await RunAsync(e, t);

            var final = Path.Combine(root, "my_game", "setup_game_1.0.exe");
            Assert.Equal(DownloadTaskState.Completed, t.State, t.Error ?? "state");
            Assert.True(File.Exists(final), "written under the CDN name, not the display label");
            Assert.Equal(32L, new FileInfo(final).Length, "all bytes");
            Assert.Equal(FileState.Verified, t.File.State, "MD5 matched -> Verified");
            Assert.Equal("my_game/setup_game_1.0.exe", t.File.LocalRelativePath, "manifest-relative, forward slashes");
            Assert.Equal("setup_game_1.0.exe", t.File.ResolvedFileName, "real name cached");
            Assert.False(Directory.Exists(Path.Combine(root, ".grog-tmp")), "no .part left behind, and the temp folder went with its last file (09-05)");
            Assert.Equal(1, cdn.Gets, "advertised size + name skip the probe: exactly one GET");
        }
        finally { Cleanup(rootDir); }
    }

    [Test]
    public async Task Without_a_checksum_the_file_is_Present_not_Verified()
    {
        var rootDir = "";
        try
        {
            var (e, cdn, root, t) = Rig(); rootDir = root;
            cdn.ServeChecksum = false;
            await RunAsync(e, t);
            Assert.Equal(DownloadTaskState.Completed, t.State, t.Error ?? "state");
            Assert.Equal(FileState.Present, t.File.State, "size-only settle");
        }
        finally { Cleanup(rootDir); }
    }

    [Test]
    public async Task A_dropped_transfer_resumes_from_the_partial()
    {
        var (e, cdn, root, t) = Rig();
        try
        {
            cdn.FailFirstGetMidway = true;
            await RunAsync(e, t);
            Assert.Equal(DownloadTaskState.Failed, t.State, "the drop fails the attempt");
            Assert.True(Directory.EnumerateFiles(Path.Combine(root, ".grog-tmp")).Any(), "the 10-byte .part is KEPT for resume");

            var t2 = new DownloadTask { File = t.File, GameTitle = t.GameTitle, GameSlug = t.GameSlug };
            var e2 = new DownloadEngine(new GogApiClient(new HttpClient(cdn), new NoAuth()), new HttpClient(cdn)) { BackupRoot = root, MaxConcurrent = 1, MaxBackoffRetries = 0 };
            await RunAsync(e2, t2);
            Assert.Equal(DownloadTaskState.Completed, t2.State, t2.Error ?? "state");
            Assert.Equal(10L, cdn.LastRangeFrom, "the second GET asked for bytes 10-: a resume, not a restart");
            var final = Path.Combine(root, "my_game", "setup_game_1.0.exe");
            Assert.Equal(32L, new FileInfo(final).Length, "whole file after resume");
            Assert.Equal(FileState.Verified, t2.File.State, "the resumed file still hashes clean");
        }
        finally { Cleanup(root); }
    }

    [Test]
    public async Task A_resumed_download_verifies_against_the_full_file_md5()
    {
        // The hash is built in the copy loop; on a resume the prefix on disk is folded in first, so the digest
        // compared with GOG's checksum covers every byte of the file, not only the ones this attempt streamed.
        var (e, cdn, root, t) = Rig();
        try
        {
            var part = DownloadEngine.PartialPathFor(root, t.File);
            Directory.CreateDirectory(Path.GetDirectoryName(part)!);
            File.WriteAllBytes(part, cdn.Payload.Take(12).ToArray());   // a correct 12-byte prefix from an earlier run
            await RunAsync(e, t);
            Assert.Equal(DownloadTaskState.Completed, t.State, t.Error ?? "state");
            Assert.Equal(12L, cdn.LastRangeFrom, "resumed at byte 12");
            Assert.Equal(FileState.Verified, t.File.State, "prefix + streamed tail hash to the full-file MD5");
            Assert.Equal(32L, new FileInfo(Path.Combine(root, "my_game", "setup_game_1.0.exe")).Length);
        }
        finally { Cleanup(root); }
    }

    [Test]
    public async Task A_corrupt_resumed_prefix_is_detected_and_the_partial_discarded()
    {
        var (e, cdn, root, t) = Rig();
        try
        {
            var part = DownloadEngine.PartialPathFor(root, t.File);
            Directory.CreateDirectory(Path.GetDirectoryName(part)!);
            File.WriteAllBytes(part, Encoding.ASCII.GetBytes("XXXXXXXXXXXX"));   // right length, wrong bytes
            await RunAsync(e, t);
            Assert.Equal(DownloadTaskState.Failed, t.State, "the wrong prefix fails the MD5 compare");
            Assert.Contains("MD5 mismatch", t.Error ?? "", "named as a checksum failure");
            Assert.False(File.Exists(part), "the corrupt partial is discarded so the retry starts clean");
            Assert.False(File.Exists(Path.Combine(root, "my_game", "setup_game_1.0.exe")), "nothing landed under the real name");
        }
        finally { Cleanup(root); }
    }

    [Test]
    public async Task A_partial_larger_than_the_rounded_label_is_resumed_not_discarded()
    {
        // Walk 09-19: "partial of 1.99 MB is not smaller than the file (1.00 MB); restarted from zero" on every
        // resume. The label is GOG's rounding, not a length: only a real size may condemn a partial.
        var (e, cdn, root, t) = Rig(advertised: 8);   // real file is 32, the label says 8
        try
        {
            cdn.FailFirstGetMidway = true;
            await RunAsync(e, t);   // leaves a 10-byte .part: larger than the label
            foreach (bool knowsRealSize in new[] { false })
            {
                if (!knowsRealSize) t.File.WireSizeBytes = null;   // a record from before the real size was kept
                var t2 = new DownloadTask { File = t.File, GameTitle = t.GameTitle, GameSlug = t.GameSlug };
                var e2 = new DownloadEngine(new GogApiClient(new HttpClient(cdn), new NoAuth()), new HttpClient(cdn)) { BackupRoot = root, MaxConcurrent = 1, MaxBackoffRetries = 0 };
                await RunAsync(e2, t2);
                Assert.Equal(DownloadTaskState.Completed, t2.State, t2.Error ?? "state");
                Assert.Equal(10L, cdn.LastRangeFrom, "resumed at byte 10, not restarted");
                Assert.Equal(32L, new FileInfo(Path.Combine(root, "my_game", "setup_game_1.0.exe")).Length);
            }
        }
        finally { Cleanup(root); }
    }

    [Test]
    public async Task A_complete_partial_under_a_stale_larger_advertised_size_finishes_instead_of_416()
    {
        // Owner-hit 09-04 (Dungeon Keeper 2, pause then resume): the .part held every byte, but the manifest's
        // advertised size was LARGER than the real file (GOG rounds), so the >= guard did not fire, the resume
        // asked for a Range past the end and the CDN answered 416. The 416 carries the true length: a .part
        // that IS that length is complete and goes to verify; one that is longer is bad and restarts.
        var (e, cdn, root, t) = Rig(advertised: 40);   // real file is 32
        try
        {
            var tmp = Path.Combine(root, ".grog-tmp"); Directory.CreateDirectory(tmp);
            // Seed the identity-keyed .part by letting one run drop after 10 bytes, then top it up to the full 32.
            cdn.FailFirstGetMidway = true;
            await RunAsync(e, t);
            var part = Directory.EnumerateFiles(tmp).Single();
            File.WriteAllBytes(part, cdn.Payload);

            var t2 = new DownloadTask { File = t.File, GameTitle = t.GameTitle, GameSlug = t.GameSlug };
            var e2 = new DownloadEngine(new GogApiClient(new HttpClient(cdn), new NoAuth()), new HttpClient(cdn)) { BackupRoot = root, MaxConcurrent = 1, MaxBackoffRetries = 0 };
            await RunAsync(e2, t2);
            Assert.Equal(DownloadTaskState.Completed, t2.State, t2.Error ?? "state");
            Assert.Equal(32L, new FileInfo(Path.Combine(root, "my_game", "setup_game_1.0.exe")).Length);
            Assert.Equal(FileState.Verified, t2.File.State, "hashed clean");
        }
        finally { Cleanup(root); }
    }

    [Test]
    public async Task A_slow_device_takes_one_file_at_a_time_and_the_run_still_drains()
    {
        // Two files bound to the primary, two workers, primary already judged slow: the second file must WAIT
        // (not run beside the first) and the worker that finds nothing eligible must not retire, or the run
        // would end with a file still Pending.
        var (e, cdn, root, t) = Rig();
        try
        {
            var mon = new DeviceWriteMonitor();
            mon.Record("", 5); mon.Record("", 5);
            e.DeviceMonitor = mon; e.MaxConcurrent = 2;
            var f2 = new GameFile { GameGogId = 42, FileKey = "/downlink/game/2", Kind = FileKind.Installer, Name = "Setup 2", Os = "windows", ExpectedSizeBytes = 32 };
            var t2 = new DownloadTask { File = f2, GameTitle = "My Game", GameSlug = "my_game" };
            int maxActive = 0;
            e.TaskChanged += _ => { int a = e.Snapshot.Count(x => x.State == DownloadTaskState.Active); if (a > maxActive) maxActive = a; };
            e.EnqueueRange(new[] { t, t2 });
            await e.RunToCompletionAsync();
            Assert.Equal(DownloadTaskState.Completed, t.State, t.Error ?? "t");
            Assert.Equal(DownloadTaskState.Completed, t2.State, t2.Error ?? "t2");
            Assert.Equal(1, maxActive, "never two in flight on a slow device");
        }
        finally { Cleanup(root); }
    }

    [Test]
    public async Task An_existing_complete_file_is_adopted_without_a_transfer()
    {
        var rootDir = "";
        try
        {
            var (e, cdn, root, t) = Rig(); rootDir = root;
            var dir = Path.Combine(root, "my_game"); Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "setup_game_1.0.exe"), cdn.Payload);
            await RunAsync(e, t);
            Assert.Equal(DownloadTaskState.Completed, t.State, t.Error ?? "state");
            Assert.Equal(FileState.Present, t.File.State, "adopted by size: Present, not Verified");
            Assert.Equal(32L, t.File.LocalSizeBytes, "size recorded");
        }
        finally { Cleanup(rootDir); }
    }

    // Sweep 2 #2: Fix It on an MD5-corrupt file re-adopted the same bytes, because adopt tests size only.
    [Test]
    public async Task A_refetch_never_adopts_the_copy_on_disk()
    {
        var rootDir = "";
        try
        {
            var (e, cdn, root, t) = Rig(); rootDir = root;
            var dir = Path.Combine(root, "my_game"); Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "setup_game_1.0.exe");
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX"));   // right size, wrong bytes
            t.File.RefetchRequested = true;
            await RunAsync(e, t);
            Assert.Equal(DownloadTaskState.Completed, t.State, t.Error ?? "state");
            Assert.True(cdn.Gets >= 1, "the file was transferred, not adopted");
            Assert.True(File.ReadAllBytes(path).SequenceEqual(cdn.Payload), "the corrupt bytes were replaced");
            Assert.Equal(FileState.Verified, t.File.State, "downloaded and hashed");
            Assert.False(t.File.RefetchRequested, "cleared once fresh bytes land");
        }
        finally { Cleanup(rootDir); }
    }

    /// <summary>(owner 09-19) The last gate: once the CDN has said how long the file REALLY is, and before a byte is
    /// written, the remainder is compared with the drive's live free space. No room: the task is turned away as a
    /// drive problem (no strike, held as won't-fit), nothing is written, and the real size is kept for the planner.</summary>
    [Test]
    public async Task A_file_the_drive_has_no_room_for_never_starts()
    {
        var rootDir = "";
        try
        {
            var (e, _, root, t) = Rig(advertised: 8); rootDir = root;   // GOG's label said 8; the CDN will say 32
            e.FreeBytesProbe = _ => 20 + DownloadEngine.FloorBytes;
            await RunAsync(e, t);

            Assert.Equal(DownloadTaskState.Failed, t.State, "turned away");
            Assert.True(t.DiskFull, "as a drive problem: " + t.Error);
            Assert.Equal(32L, t.File.WireSizeBytes ?? 0, "the real size is remembered, so the next plan prices it right");
            Assert.False(Directory.Exists(Path.Combine(root, "my_game")) && Directory.GetFiles(Path.Combine(root, "my_game")).Length > 0, "nothing landed");
            var tmp = Path.Combine(root, ".grog-tmp");
            Assert.True(!Directory.Exists(tmp) || Directory.GetFiles(tmp).All(p => new FileInfo(p).Length == 0), "no bytes were written");

            var (e2, _, root2, t2) = Rig(advertised: 8);
            try
            {
                e2.FreeBytesProbe = _ => 32 + DownloadEngine.FloorBytes;
                await RunAsync(e2, t2);
                Assert.Equal(DownloadTaskState.Completed, t2.State, "exactly enough room: it runs. " + t2.Error);
            }
            finally { Cleanup(root2); }
        }
        finally { Cleanup(rootDir); }
    }
}

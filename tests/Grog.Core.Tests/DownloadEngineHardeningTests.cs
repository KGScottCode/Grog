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
/// The engine against a CDN that misbehaves: a resolve timeout, a web page served as the file, a length far
/// from the label, a 206 from the wrong offset, and a same-name update under KeepOldVersions.
/// </summary>
[Trait("download")]
public sealed class DownloadEngineHardeningTests
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

    /// <summary>A body that delivers a prefix, then blocks until the read is cancelled: a stalled link mid-file.</summary>
    private sealed class HeldContent : HttpContent
    {
        private readonly byte[] _bytes; private readonly long _good; private readonly TaskCompletionSource _held;
        public HeldContent(byte[] bytes, long good, TaskCompletionSource held) { _bytes = bytes; _good = good; _held = held; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => stream.WriteAsync(_bytes, 0, _bytes.Length);
        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new HeldStream(_bytes, _good, _held));
        protected override bool TryComputeLength(out long length) { length = _bytes.Length; return true; }
        private sealed class HeldStream : MemoryStream
        {
            private readonly long _good; private readonly TaskCompletionSource _held;
            public HeldStream(byte[] b, long good, TaskCompletionSource held) : base(b, writable: false) { _good = good; _held = held; }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                if (Position >= _good)
                {
                    _held.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);   // blocks until the engine cancels the read
                }
                int n = Read(buffer.Span.Slice(0, (int)Math.Min(buffer.Length, _good - Position)));
                return n;
            }
        }
    }

    /// <summary>A body with no Content-Length: what a chunked 200 looks like to the engine.</summary>
    private sealed class ChunkedContent : HttpContent
    {
        private readonly byte[] _bytes;
        public ChunkedContent(byte[] bytes) { _bytes = bytes; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => stream.WriteAsync(_bytes, 0, _bytes.Length);
        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class FakeCdn : HttpMessageHandler
    {
        public byte[] Payload = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");   // 32 bytes
        public string RealName = "setup_game_1.0.exe";
        public bool ServeChecksum = true;
        public int Gets;
        public long? LastRangeFrom;
        public bool FailFirstGetMidway;
        /// <summary>The downlink resolve times out: what GogApiClient's 30 s CancelAfter surfaces as.</summary>
        public bool ResolveTimesOut;
        /// <summary>The GET answers 200 with an HTML page (a login or error page) instead of the file.</summary>
        public bool ServeHtmlPage;
        /// <summary>A Range request is answered 206 with Content-Range starting at 0 and the whole body.</summary>
        public bool WrongRangeFrom;
        /// <summary>The GET answers 200 with a chunked body: no Content-Length on the wire.</summary>
        public bool NoContentLength;
        /// <summary>After this many body bytes the read blocks until the token is cancelled (a stalled link).</summary>
        public long HoldBodyAfter;
        public TaskCompletionSource BodyHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("embed.gog.com/downlink"))
            {
                if (ResolveTimesOut) throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
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
            long from = req.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            LastRangeFrom = req.Headers.Range is null ? null : from;
            if (req.Method == HttpMethod.Get) Gets++;
            if (ServeHtmlPage)
            {
                var page = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("<html><body>Please sign in</body></html>", Encoding.UTF8, "text/html") };
                page.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = RealName };
                return Task.FromResult(page);
            }
            if (from >= Payload.Length)
            {
                var r416 = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                r416.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(Payload.Length);
                return Task.FromResult(r416);
            }
            bool badRange = WrongRangeFrom && from > 0;
            if (badRange) from = 0;
            var body = Payload.Skip((int)from).ToArray();
            bool drop = req.Method == HttpMethod.Get && FailFirstGetMidway && Gets == 1;
            if (drop) FailFirstGetMidway = false;
            var resp = new HttpResponseMessage(req.Headers.Range is not null && (from > 0 || badRange) ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { Content = drop ? new DroppingContent(body, 10) : HoldBodyAfter > 0 ? new HeldContent(body, HoldBodyAfter, BodyHeld) : NoContentLength ? new ChunkedContent(body) : new ByteArrayContent(body) };
            if (!NoContentLength) resp.Content.Headers.ContentLength = body.Length;
            if (resp.StatusCode == HttpStatusCode.PartialContent)
                resp.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, Payload.Length - 1, Payload.Length);
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
        var root = Path.Combine(Path.GetTempPath(), "grog-hard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var engine = new DownloadEngine(api, http) { BackupRoot = root, MaxConcurrent = 1, MaxBackoffRetries = 0 };
        var file = new GameFile { GameGogId = 42, FileKey = "/downlink/game/1", Kind = FileKind.Installer, Name = "Setup (Part 1 of 1)", Os = "windows", ExpectedSizeBytes = advertised };
        var task = new DownloadTask { File = file, GameTitle = "My Game", GameSlug = "my_game" };
        return (engine, cdn, root, task);
    }

    private static DownloadEngine Again(FakeCdn cdn, string root)
        => new(new GogApiClient(new HttpClient(cdn), new NoAuth()), new HttpClient(cdn)) { BackupRoot = root, MaxConcurrent = 1, MaxBackoffRetries = 0 };

    private static void Cleanup(string root) { try { Directory.Delete(root, recursive: true); } catch { /* best effort */ } }

    private static async Task RunAsync(DownloadEngine e, DownloadTask t)
    {
        e.Enqueue(t);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await e.RunToCompletionAsync(cts.Token);
    }

    // E1
    [Test]
    public async Task A_resolve_timeout_is_a_failure_not_a_cancel()
    {
        var (e, cdn, root, t) = Rig();
        try
        {
            cdn.ResolveTimesOut = true;
            await RunAsync(e, t);
            Assert.Equal(DownloadTaskState.Failed, t.State, "an internal timeout with nobody canceling is a failure");
            Assert.Equal(DownloadFailureKind.Network, t.FailureKind, "classified like a network error: strike, retry");
            Assert.Contains("did not answer", t.Error ?? "", "names the timeout, not 'operation was canceled'");
        }
        finally { Cleanup(root); }
    }

    // E2
    [Test]
    public async Task A_200_carrying_a_web_page_fails_the_file()
    {
        var (e, cdn, root, t) = Rig();
        try
        {
            cdn.ServeHtmlPage = true;
            await RunAsync(e, t);
            Assert.Equal(DownloadTaskState.Failed, t.State, "a text/html body is not the file");
            Assert.Contains("web page", t.Error ?? "", "says what came back");
            Assert.False(File.Exists(Path.Combine(root, "my_game", "setup_game_1.0.exe")), "nothing landed under the real name");
            Assert.True(t.File.State != FileState.Present && t.File.State != FileState.Verified, "not recorded as Present");
        }
        finally { Cleanup(root); }
    }

    // E2
    [Test]
    public async Task A_length_far_from_the_label_fails_the_file()
    {
        var (e, cdn, root, t) = Rig(advertised: 10_000_000);   // GOG lists 10 MB; the GET sends 32 bytes
        try
        {
            await RunAsync(e, t);
            Assert.Equal(DownloadTaskState.Failed, t.State, "32 bytes for a 10 MB file is not the file");
            Assert.Contains("lists as", t.Error ?? "", "says both figures");
            Assert.False(File.Exists(Path.Combine(root, "my_game", "setup_game_1.0.exe")), "nothing landed");
        }
        finally { Cleanup(root); }
    }

    // E2
    [Test]
    public async Task A_length_within_one_label_unit_is_accepted()
    {
        var (e, cdn, root, t) = Rig(advertised: 8);   // a label under the real 32 by less than a display unit
        try
        {
            await RunAsync(e, t);
            Assert.Equal(DownloadTaskState.Completed, t.State, t.Error ?? "state");
            Assert.Equal(FileState.Verified, t.File.State);
        }
        finally { Cleanup(root); }
    }

    // 1274: GOG's listed size can be off by several percent (941 MB listed, 897 MB sent, measured 09-29); that is the
    // checksum's call, not a refusal. Only a body of another order (an error page) is refused.
    [Test]
    public void A_body_a_few_percent_off_the_label_is_plausible_and_an_error_page_is_not()
    {
        Assert.True(DownloadEngine.PlausibleBodyLength(897_450_000, 941_000_000), "4.6% under the label");
        Assert.True(DownloadEngine.PlausibleBodyLength(32, 8), "bytes apart: display rounding");
        Assert.True(DownloadEngine.PlausibleBodyLength(1_990_000, 1_000_000), "a truncated label ('1 MB' for 1.99 MB)");
        Assert.False(DownloadEngine.PlausibleBodyLength(1_024, 941_000_000), "a 1 KB page for a 941 MB file");
        Assert.False(DownloadEngine.PlausibleBodyLength(300_000_000, 941_000_000), "under half the label");
    }

    // 1279: an extra's label is a placeholder (a 111.90 KB manual listed at 10.00 MB, measured 09-30): never refused on length.
    [Test]
    public void An_extras_body_is_never_refused_on_length_but_an_installers_still_is()
    {
        Assert.False(DownloadEngine.RefusesBodyLength(FileKind.Extra, 114_585, 10_485_760), "a manual at GOG's flat 10 MB label");
        Assert.True(DownloadEngine.RefusesBodyLength(FileKind.Installer, 114_585, 10_485_760), "an installer stub of the same size");
        Assert.False(DownloadEngine.RefusesBodyLength(FileKind.Installer, 897_450_000, 941_000_000), "a few percent off stays");
        Assert.False(DownloadEngine.RefusesBodyLength(FileKind.Installer, 1_024, 0), "no label: nothing to compare");
    }

    // E2
    [Test]
    public async Task A_sizeless_body_with_no_checksum_fails_the_file_after_the_transfer()
    {
        var (e, cdn, root, t) = Rig();
        try
        {
            cdn.NoContentLength = true;
            cdn.ServeChecksum = false;
            await RunAsync(e, t);
            Assert.Equal(DownloadTaskState.Failed, t.State, "nothing vouches for the body: not the file");
            Assert.Contains("neither a size nor a checksum", t.Error ?? "", "names what was missing");
            Assert.False(File.Exists(Path.Combine(root, "my_game", "setup_game_1.0.exe")), "nothing landed");
            Assert.False(Directory.Exists(Path.Combine(root, ".grog-tmp")) && Directory.EnumerateFiles(Path.Combine(root, ".grog-tmp"), "*.part", SearchOption.AllDirectories).Any(), "the .part was discarded");
        }
        finally { Cleanup(root); }
    }

    // E2
    [Test]
    public async Task A_sizeless_body_with_a_checksum_is_verified_and_accepted()
    {
        var (e, cdn, root, t) = Rig();
        try
        {
            cdn.NoContentLength = true;
            await RunAsync(e, t);
            Assert.Equal(DownloadTaskState.Completed, t.State, t.Error ?? "state");
            Assert.Equal(FileState.Verified, t.File.State, "the checksum vouched for the body");
        }
        finally { Cleanup(root); }
    }

    // E7
    [Test]
    public async Task An_intact_copy_off_the_rounded_label_is_adopted_not_refetched()
    {
        var (e, cdn, root, t) = Rig(advertised: 40);   // the label rounds up; the copy on disk is the real 32
        try
        {
            var dir = Path.Combine(root, "my_game"); Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "setup_game_1.0.exe"), cdn.Payload);
            long before = new FileInfo(Path.Combine(dir, "setup_game_1.0.exe")).LastWriteTimeUtc.Ticks;
            await RunAsync(e, t);
            Assert.Equal(DownloadTaskState.Completed, t.State, t.Error ?? "state");
            Assert.Equal(FileState.Present, t.File.State, "adopted by the GET's real length: Present, not Verified");
            Assert.Equal(32L, t.File.LocalSizeBytes, "the real size recorded, not the label");
            Assert.Equal(1, cdn.Gets, "the headers were read once; no second GET for a re-download");
            Assert.False(Directory.Exists(Path.Combine(root, ".grog-tmp")), "no transfer: the temp folder went");
            Assert.Equal(before, new FileInfo(Path.Combine(dir, "setup_game_1.0.exe")).LastWriteTimeUtc.Ticks, "the copy on disk was left as it was");
        }
        finally { Cleanup(root); }
    }

    // E12
    [Test]
    public async Task A_206_from_the_wrong_offset_restarts_from_zero()
    {
        var (e, cdn, root, t) = Rig();
        try
        {
            cdn.FailFirstGetMidway = true;
            await RunAsync(e, t);   // leaves a 10-byte .part
            Assert.Equal(DownloadTaskState.Failed, t.State, "the drop fails the first attempt");

            cdn.WrongRangeFrom = true;
            var notes = new List<string>();
            var t2 = new DownloadTask { File = t.File, GameTitle = t.GameTitle, GameSlug = t.GameSlug };
            var e2 = Again(cdn, root);
            e2.PartialNotice += m => { lock (notes) notes.Add(m); };
            int getsBefore = cdn.Gets;
            await RunAsync(e2, t2);
            Assert.Equal(DownloadTaskState.Completed, t2.State, t2.Error ?? "state");
            Assert.Equal(getsBefore + 2, cdn.Gets, "the bad 206 was dropped and a fresh GET made");
            Assert.Null(cdn.LastRangeFrom, "the fresh GET carried no Range: a restart, not a resume");
            Assert.Equal(1, notes.Count(n => n.Contains("instead of")), "logged once");
            Assert.Equal(32L, new FileInfo(Path.Combine(root, "my_game", "setup_game_1.0.exe")).Length);
            Assert.Equal(FileState.Verified, t2.File.State, "the restarted file hashes clean");
        }
        finally { Cleanup(root); }
    }

    // E3
    [Test]
    public async Task A_same_name_update_archives_the_old_build_when_old_versions_are_kept()
    {
        var (e, cdn, root, t) = Rig();
        try
        {
            var dir = Path.Combine(root, "my_game"); Directory.CreateDirectory(dir);
            var final = Path.Combine(dir, "setup_game_1.0.exe");
            var oldBytes = Encoding.ASCII.GetBytes("OLD BUILD");   // a different size, so it is not adopted
            File.WriteAllBytes(final, oldBytes);
            t.File.LocalRelativePath = "my_game/setup_game_1.0.exe";
            t.File.State = FileState.Verified;
            e.KeepOldVersions = true;
            var archived = new List<GameFile>();
            e.OldVersionArchived += f => { lock (archived) archived.Add(f); };
            await RunAsync(e, t);

            Assert.Equal(DownloadTaskState.Completed, t.State, t.Error ?? "state");
            Assert.True(File.ReadAllBytes(final).SequenceEqual(cdn.Payload), "the new build is under the name");
            var kept = Path.Combine(root, DownloadEngine.OldVersionsFolder, "my_game", "setup_game_1.0.exe");
            Assert.True(File.Exists(kept), "the old build went to Old Versions, not under the overwrite");
            Assert.True(File.ReadAllBytes(kept).SequenceEqual(oldBytes), "the archived bytes are the old build's");
            Assert.Equal(1, archived.Count, "one OldVersionArchived record");
            Assert.True(archived[0].IsOldVersion, "flagged as an old version");
            Assert.Equal("Old Versions/my_game/setup_game_1.0.exe", archived[0].LocalRelativePath);
            Assert.Equal("my_game/setup_game_1.0.exe", t.File.LocalRelativePath, "the record still points at the live copy");
        }
        finally { Cleanup(root); }
    }

    // E3
    [Test]
    public async Task A_failed_final_move_puts_the_old_build_back_and_archives_nothing()
    {
        var (e, cdn, root, t) = Rig();
        try
        {
            var dir = Path.Combine(root, "my_game"); Directory.CreateDirectory(dir);
            var final = Path.Combine(dir, "setup_game_1.0.exe");
            var oldBytes = Encoding.ASCII.GetBytes("OLD BUILD");
            File.WriteAllBytes(final, oldBytes);
            t.File.LocalRelativePath = "my_game/setup_game_1.0.exe";
            t.File.State = FileState.Verified;
            e.KeepOldVersions = true;
            var archived = 0;
            e.OldVersionArchived += _ => Interlocked.Increment(ref archived);
            // The .part vanishes under the engine (an antivirus quarantine): the real File.Move throws.
            e.BeforeFinalMove = (part, _) => File.Delete(part);
            await RunAsync(e, t);

            Assert.Equal(DownloadTaskState.Failed, t.State, "the move threw");
            Assert.True(File.Exists(final), "the old build is back under its name");
            Assert.True(File.ReadAllBytes(final).SequenceEqual(oldBytes), "with its bytes");
            Assert.False(File.Exists(final + DownloadEngine.HeldOldBuildSuffix), "nothing left under the sibling name");
            Assert.False(Directory.Exists(Path.Combine(root, DownloadEngine.OldVersionsFolder)), "nothing archived");
            Assert.Equal(0, archived, "no OldVersionArchived record");
        }
        finally { Cleanup(root); }
    }

    // E3
    [Test]
    public async Task A_same_name_update_overwrites_the_old_build_when_old_versions_are_not_kept()
    {
        var (e, cdn, root, t) = Rig();
        try
        {
            var dir = Path.Combine(root, "my_game"); Directory.CreateDirectory(dir);
            var final = Path.Combine(dir, "setup_game_1.0.exe");
            File.WriteAllBytes(final, Encoding.ASCII.GetBytes("OLD BUILD"));
            t.File.LocalRelativePath = "my_game/setup_game_1.0.exe";
            var archived = 0;
            e.OldVersionArchived += _ => Interlocked.Increment(ref archived);
            await RunAsync(e, t);

            Assert.Equal(DownloadTaskState.Completed, t.State, t.Error ?? "state");
            Assert.True(File.ReadAllBytes(final).SequenceEqual(cdn.Payload), "overwritten in place");
            Assert.False(Directory.Exists(Path.Combine(root, DownloadEngine.OldVersionsFolder)), "nothing archived");
            Assert.Equal(0, archived, "no OldVersionArchived record");
        }
        finally { Cleanup(root); }
    }

    // 1273: a quit waits on WhenStopped: complete with no run, and after a Stop mid-transfer it completes only once the
    // worker has closed the .part (no handle left open on the drive).
    [Test]
    public async Task WhenStopped_completes_after_a_stopped_worker_has_closed_its_part()
    {
        var (e, cdn, root, t) = Rig(advertised: null);
        try
        {
            Assert.True(e.WhenStopped.IsCompleted, "no run: nothing to wait for");
            cdn.Payload = new byte[8 * 1024 * 1024];
            cdn.HoldBodyAfter = 1024 * 1024;   // the body stalls after 1 MB until released
            e.Enqueue(t);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var run = e.RunToCompletionAsync(cts.Token);
            await cdn.BodyHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stopped = e.WhenStopped;
            Assert.False(stopped.IsCompleted, "mid-transfer the wait is pending");
            e.Stop();
            await stopped.WaitAsync(TimeSpan.FromSeconds(10));
            var part = Directory.GetFiles(root, "*.part", SearchOption.AllDirectories).Single();
            using (new FileStream(part, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }   // throws while a handle is open
            Assert.True(e.WhenStopped.IsCompleted, "after the stop: complete");
            await run;
        }
        finally { Cleanup(root); }
    }
}

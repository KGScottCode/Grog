// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Runs;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

/// <summary>The three engine/settler checks the 09-30 review deferred, written before the v0.1.0 tag: a held file
/// keeps its worker (B6), a late failure on a file already present is journaled as not counted (C6), and the
/// first byte written stamps where the .part lives (A3).</summary>
[Trait("runs")]
public sealed class ReleaseGateTests
{
    private sealed class Cdn : HttpMessageHandler
    {
        public byte[] Payload = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");
        public bool FailFirstGetMidway;
        public int Gets;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("embed.gog.com/downlink"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"downlink\":\"https://cdn.test/file?token=1\",\"checksum\":\"https://cdn.test/file.xml?token=1\"}", Encoding.UTF8, "application/json") });
            if (url.Contains("file.xml"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            long from = req.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            if (req.Method == HttpMethod.Get) Gets++;
            var body = Payload.Skip((int)from).ToArray();
            bool drop = req.Method == HttpMethod.Get && FailFirstGetMidway && Gets == 1;
            if (drop) FailFirstGetMidway = false;
            var resp = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { Content = drop ? new Dropping(body, 10) : new ByteArrayContent(body) };
            resp.Content.Headers.ContentLength = body.Length;
            resp.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = "setup.exe" };
            return Task.FromResult(resp);
        }
    }

    private sealed class Dropping : HttpContent
    {
        private readonly byte[] _b; private readonly int _after;
        public Dropping(byte[] b, int after) { _b = b; _after = after; }
        protected override Task SerializeToStreamAsync(Stream s, System.Net.TransportContext? c) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new DropStream(_b, _after));
        protected override bool TryComputeLength(out long l) { l = _b.Length; return true; }
        private sealed class DropStream : MemoryStream
        {
            private readonly int _after;
            public DropStream(byte[] b, int after) : base(b) { _after = after; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Position >= _after) throw new IOException("connection dropped");
                return base.Read(buffer, offset, (int)Math.Min(count, _after - Position));
            }
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                if (Position >= _after) throw new IOException("connection dropped");
                return base.ReadAsync(buffer.Slice(0, (int)Math.Min(buffer.Length, _after - Position)), ct);
            }
        }
    }

    private sealed class NoAuth : IAuthService
    {
        public bool HasStoredSession => true;
        public Task<AuthSession> EnsureAuthenticatedAsync(CancellationToken ct = default)
            => Task.FromResult(new AuthSession("at", "rt", DateTimeOffset.UtcNow.AddHours(1), "u"));
        public Task SignOutAsync() => Task.CompletedTask;
    }

    private sealed class MemStore : IManifestStore
    {
        public MemStore(LibraryManifest m) => Current = m;
        public LibraryManifest Current { get; }
        public ManifestGate Gate { get; } = new();
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static (DownloadEngine Engine, Cdn Cdn, string Root) Rig()
    {
        var cdn = new Cdn();
        var http = new HttpClient(cdn);
        var root = Path.Combine(Path.GetTempPath(), "grog-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var engine = new DownloadEngine(new GogApiClient(http, new NoAuth()), http) { BackupRoot = root, MaxConcurrent = 1, MaxBackoffRetries = 0, ClusterBytesProbe = _ => 1 };
        return (engine, cdn, root);
    }

    private static DownloadTask TaskFor(long gog, string slug, string? rootId = null)
        => new()
        {
            File = new GameFile { GameGogId = gog, FileKey = $"/downlink/{slug}/1", Kind = FileKind.Installer, Name = "Setup", Os = "windows", ExpectedSizeBytes = 32 },
            GameTitle = slug, GameSlug = slug, TargetRootId = rootId,
        };

    private static void Cleanup(string dir) { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }

    [Test]
    public async Task B6_a_held_file_waits_for_its_worker_and_lands_once_released()
    {
        var (e, _, root) = Rig();
        try
        {
            var held = TaskFor(1, "held"); var free = TaskFor(2, "free");
            e.HoldFiles(new[] { (1L, held.File.FileKey) });
            e.Enqueue(held); e.Enqueue(free);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var run = e.RunToCompletionAsync(cts.Token);

            for (int i = 0; i < 200 && free.State != DownloadTaskState.Completed; i++) await Task.Delay(25);
            Assert.Equal(DownloadTaskState.Completed, free.State, free.Error ?? "the unheld file lands while the other is held");
            await Task.Delay(300);
            Assert.Equal(DownloadTaskState.Pending, held.State, "the held file is still waiting, not started, not failed");
            Assert.False(run.IsCompleted, "the run has not drained: its worker waits for the held file instead of retiring");

            e.ReleaseFiles(new[] { (1L, held.File.FileKey) });
            await run;
            Assert.Equal(DownloadTaskState.Completed, held.State, held.Error ?? "released: the same worker takes it and it lands");
        }
        finally { Cleanup(root); }
    }

    [Test]
    public async Task C6_a_late_failure_on_a_present_file_is_journaled_as_not_counted_with_no_strike()
    {
        var cfg = Path.Combine(Path.GetTempPath(), "grog-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cfg);
        try
        {
            var m = new LibraryManifest { PrimaryRootId = "p" };
            m.Roots.Add(new BackupRoot { Id = "p", Label = "Primary" });
            var f = new GameFile { GameGogId = 1, FileKey = "f1", Name = "f1", Kind = FileKind.Installer, State = FileState.Verified,
                                   ExpectedSizeBytes = 10, Os = "windows", Language = "English", HasPartial = true, PartialBytes = 4 };
            m.Items.Add(new LibraryItem { GogId = 1, Title = "g1", Slug = "g1", Type = ProductType.Game, Files = { f } });
            m.Downloads.Enqueue(1, "f1", "p");
            var store = new MemStore(m);
            var engine = new DownloadEngine(null!, new HttpClient()) { BackupRoot = cfg };
            using var journal = RunJournal.Start(cfg, "backup", 1)!;
            using var settler = new RunSettler(store, engine, journal, new NullBackupHost());
            BackupQueueBuilder.ReplanWholeQueue(store, new[] { new Grog.Core.Volumes.DeviceSpace("p", 100_000, true) }, engine);

            var t = engine.Snapshot.Single();
            t.State = DownloadTaskState.Failed; t.Error = "late failure after the file had landed";
            await settler.FlushAsync();

            Assert.Equal(0, f.FailedAttempts, "no strike on a file that is present");
            Assert.False(f.HasPartial, "the partial flag the failed attempt raised is dropped");
            Assert.Equal(FileState.Verified, f.State, "the record keeps its state");
            journal.Finish(Grog.Core.Scheduling.ScheduleRunOutcome.Completed);
            var last = RunJournalReader.ReadLast(cfg)!;
            Assert.Equal(0, last.Failed, "the journal does not count it as failed (the Health card named a Verified file as failed, QA 09-30 C6)");
            var events = File.ReadAllText(Path.Combine(cfg, "runs", last.EventsFile!));
            Assert.True(events.Contains("already present"), events);
            Assert.Null(RunJournalReader.ReadLastFailure(cfg), "nothing to show as the last run's failure");
        }
        finally { Cleanup(cfg); }
    }

    [Test]
    public async Task A3_the_first_byte_written_stamps_where_the_partial_lives()
    {
        var (e, cdn, root) = Rig();
        try
        {
            var t = TaskFor(1, "game", rootId: "p");
            Assert.Null(t.File.PartialRootId, "nothing on disk yet: no root stamped");
            cdn.FailFirstGetMidway = true;
            e.Enqueue(t);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await e.RunToCompletionAsync(cts.Token);
            Assert.Equal(DownloadTaskState.Failed, t.State, "the drop fails the attempt");
            Assert.True(t.File.HasPartial, "a .part exists");
            Assert.Equal("p", t.File.PartialRootId, "and the record says which storage holds it (A3)");
            Assert.True(Directory.EnumerateFiles(Path.Combine(root, ".grog-tmp")).Any(), "the .part is on disk under that storage");
        }
        finally { Cleanup(root); }
    }
}

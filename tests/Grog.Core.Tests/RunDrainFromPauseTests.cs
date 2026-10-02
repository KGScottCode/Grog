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
using Grog.Core.Runs;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

/// <summary>"Finish current files" asked while PAUSED: the arm names the files that were in progress, the Resume's
/// run starts only those, and when the last of them lands the run ends with the rest still queued.</summary>
[Trait("download")]
public sealed class RunDrainFromPauseTests
{
    private sealed class FakeCdn : HttpMessageHandler
    {
        public readonly byte[] Payload = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("embed.gog.com/downlink"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"downlink\":\"https://cdn.test/file?token=1\",\"checksum\":\"https://cdn.test/file.xml?token=1\"}", Encoding.UTF8, "application/json") });
            if (url.Contains("file.xml"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent($"<file name=\"setup.exe\" md5=\"{Convert.ToHexString(MD5.HashData(Payload)).ToLowerInvariant()}\"/>") });
            long from = req.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            var body = Payload.Skip((int)from).ToArray();
            var resp = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            resp.Content.Headers.ContentLength = body.Length;
            resp.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = "setup.exe" };
            return Task.FromResult(resp);
        }
    }

    private sealed class NoAuth : IAuthService
    {
        public bool HasStoredSession => true;
        public Task<AuthSession> EnsureAuthenticatedAsync(CancellationToken ct = default) => Task.FromResult(new AuthSession("at", "rt", DateTimeOffset.UtcNow.AddHours(1), "u"));
        public Task SignOutAsync() => Task.CompletedTask;
    }

    private static (DownloadEngine Engine, string Root, DownloadTask Partial, DownloadTask Untouched) Rig()
    {
        var cdn = new FakeCdn();
        var http = new HttpClient(cdn);
        var root = Path.Combine(Path.GetTempPath(), "grog-drain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var engine = new DownloadEngine(new GogApiClient(http, new NoAuth()), http) { BackupRoot = root, MaxConcurrent = 2, MaxBackoffRetries = 0 };
        DownloadTask Make(int i) => new()
        {
            File = new GameFile { GameGogId = 40 + i, FileKey = $"/downlink/game/{i}", Kind = FileKind.Installer, Name = $"Setup {i}", Os = "windows", ExpectedSizeBytes = 32 },
            GameTitle = $"Game {i}", GameSlug = $"game_{i}",
        };
        var partial = Make(1); var untouched = Make(2);
        // The paused file: a real .part with the first ten bytes, as a pause leaves it.
        var part = DownloadEngine.PartialPathFor(root, partial.File);
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        File.WriteAllBytes(part, cdn.Payload.Take(10).ToArray());
        partial.File.HasPartial = true; partial.File.PartialBytes = 10;
        engine.Enqueue(partial); engine.Enqueue(untouched);
        return (engine, root, partial, untouched);
    }

    [Test]
    public async Task Only_the_file_in_progress_starts_and_the_drain_ends_paused_when_it_lands()
    {
        var (engine, root, partial, untouched) = Rig();
        try
        {
            var drain = new RunDrain();
            drain.ArmFinishOnly(new HashSet<(long, string)> { (partial.File.GameGogId, partial.File.FileKey) });
            drain.RunBegan();
            Assert.True(drain.Finishing, "the armed finish is the run's state from its first moment");
            drain.EngineReady(engine);
            Assert.True(engine.OnlyStart is { Count: 1 }, "the engine may start only the file that was in progress");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await engine.RunToCompletionAsync(cts.Token);

            Assert.Equal(DownloadTaskState.Completed, partial.State, partial.Error ?? "the paused file finished");
            Assert.Equal(DownloadTaskState.Pending, untouched.State, "the other file never started");
            Assert.True(engine.StartNothingNew, "with nothing allowed left the engine became a plain drain");
            Assert.Equal(1, engine.Snapshot.Count(t => t.State == DownloadTaskState.Pending), "the queue still holds the rest");

            drain.EndedPaused();   // what the host does when the run ends with files queued
            Assert.True(drain.PausedAfterFinish, "the drain ended paused, with the queue left for a later Resume");
            Assert.False(drain.Finishing, "and is no longer finishing");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Test]
    public void Disarm_after_a_bailed_resume_leaves_the_next_run_unrestricted()
    {
        var (engine, root, partial, _) = Rig();
        try
        {
            var drain = new RunDrain();
            drain.ArmFinishOnly(new HashSet<(long, string)> { (partial.File.GameGogId, partial.File.FileKey) });
            drain.Disarm();   // the Resume did not start (nothing fit, or an error)
            drain.RunBegan();
            drain.EngineReady(engine);
            Assert.Equal(DrainState.None, drain.State, "a dropped arm is forgotten");
            Assert.Null(engine.OnlyStart, "the whole queue is open to the run");
            Assert.False(engine.StartNothingNew, "and it is not a drain");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}

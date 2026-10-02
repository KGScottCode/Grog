// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
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
using Grog.Core.Scheduling;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

namespace Grog.Core.Tests;

/// <summary>
/// FILL SIMULATION (owner 09-19). Every fit rule has its own unit test; what nothing tested was the LOOP: plan,
/// download, the drive fills, re-plan, hold the rest, stop, resume, add a drive. The owner's 16 GB FAT32 stick was
/// the only thing that exercised it, at twenty minutes a fill, and on 09-19 it found a whole family of defects in
/// one afternoon (FAT32's per-file ceiling, a dead partial holding the drive full, packing to the byte, disk-full
/// strikes, orphaned partials).
///
/// This runs the REAL BackupRun, planner, queue builder, engine and settler against a fake GOG and a fake drive
/// with a hard capacity, from many random seeds, and checks after every phase what must always be true:
///   1. no transfer ever fails (a file is downloaded, or held as won't-fit; never "disk full");
///   2. the drive is never over-committed, at any sample, mid-run or after;
///   3. every file is either on disk at its real size, or queued and flagged won't-fit;
///   4. the run's "held" count equals the number of won't-fit rows;
///   5. no .part is left that no queued file's record owns;
///   6. nothing is held that would still fit in what is left (the drive is actually used).
/// GOG's size labels are rounded (both ways), some files are over the FAT ceiling, runs are cancelled mid-transfer
/// and resumed, and a second drive arrives late.
/// </summary>
[Trait("fillsim")]
public sealed class FillSimulationTests
{
    private const long KB = 1024, MB = 1024 * 1024;
    private const long Cluster = 32 * KB;

    /// <summary>The CDN: every file is zeros of its REAL length, resumable by Range.</summary>
    private sealed class SimGog : HttpMessageHandler
    {
        public readonly Dictionary<string, long> RealSize = new();   // "g{game}f{n}" -> bytes
        public readonly Dictionary<string, string> NameOf = new();   // a new build lands under a new CDN name
        public int Gets;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("embed.gog.com/downlink"))
            {
                // FileKey "/downlink/game_{g}/f{n}"
                var parts = url.Split("/downlink/")[1].Split('/');
                var id = "g" + parts[0].Replace("game_", "") + parts[1].Split('?')[0];
                var body = $"{{\"downlink\":\"https://cdn.test/{id}?token=1\"}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
            var key = url.Split("cdn.test/")[1].Split('?')[0];
            long size = RealSize[key];
            if (req.Method == HttpMethod.Get) Interlocked.Increment(ref Gets);
            long from = req.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            if (from >= size && from > 0)
            {
                var r416 = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                r416.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(size);
                return Task.FromResult(r416);
            }
            var resp = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ZeroContent(size - from) };
            resp.Content.Headers.ContentLength = size - from;
            if (from > 0) resp.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, size - 1, size);
            resp.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = NameOf.TryGetValue(key, out var nm) ? nm : key + ".bin" };
            return Task.FromResult(resp);
        }

        private sealed class ZeroContent : HttpContent
        {
            private readonly long _len;
            public ZeroContent(long len) { _len = len; }
            protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new ZeroStream(_len));
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            { using var z = new ZeroStream(_len); await z.CopyToAsync(stream); }
            protected override bool TryComputeLength(out long length) { length = _len; return true; }
        }

        /// <summary>Zeros in 64 KB reads with a yield between them, so transfers genuinely interleave and a cancel
        /// can land mid-file.</summary>
        private sealed class ZeroStream : Stream
        {
            private long _left;
            public ZeroStream(long len) { _left = len; }
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = (int)Math.Min(Math.Min(count, 64 * KB), _left);
                Array.Clear(buffer, offset, n); _left -= n; return n;
            }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(Math.Min(buffer.Length, 64 * KB), _left);
                buffer.Span[..n].Clear(); _left -= n; return n;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private sealed class NoAuth : IAuthService
    {
        public bool HasStoredSession => true;
        public Task<AuthSession> EnsureAuthenticatedAsync(CancellationToken ct = default)
            => Task.FromResult(new AuthSession("at", "rt", DateTimeOffset.UtcNow.AddHours(1), "u"));
        public Task SignOutAsync() => Task.CompletedTask;
    }

    private sealed class MemoryStore : IManifestStore
    {
        public LibraryManifest Current { get; } = new();
        public ManifestGate Gate { get; } = new();
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A drive with a hard capacity. "Used" is what is really under its folder, every file rounded up to
    /// its cluster plus one cluster of directory cost: what a filesystem would charge.</summary>
    private sealed class SimDrive
    {
        public string RootId = "", Path = "";
        public long Capacity, MaxFile = long.MaxValue;
        /// <summary>A second root on the SAME volume: it has no room of its own, it spends its host's.</summary>
        public SimDrive? Host; public readonly List<string> GuestPaths = new();
        public long Used()
        {
            if (Host is not null) return Host.Used();
            long used = 0;
            try
            {
                foreach (var f in GuestPaths.Prepend(Path).SelectMany(p => Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)))
                {
                    long len; try { len = new FileInfo(f).Length; } catch { continue; }
                    used += (len + Cluster - 1) / Cluster * Cluster + Cluster;
                }
            }
            catch { /* a folder vanished mid-walk (.grog-tmp): the next sample sees it */ }
            return used;
        }
        /// <summary>A real drive answers "free" atomically; a folder walk does not (a file renamed from .grog-tmp into
        /// its game folder mid-walk can be missed entirely, overstating the room by a whole file). Three walks, the
        /// fullest one wins.</summary>
        public long Free() => Host is not null ? Host.Free() : Math.Max(0, Capacity - Math.Max(Used(), Math.Max(Used(), Used())));
    }

    private sealed class SimHost : NullBackupHost
    {
        public readonly List<string> Logged = new();
        public readonly List<(string Key, DownloadSettlement.Outcome Outcome, string? Error)> Settled = new();
        public Action<DownloadEngine>? OnEngine;
        public int Workers = 3;
        public override void EngineReady(DownloadEngine engine) => OnEngine?.Invoke(engine);
        public override int? PickConcurrency(DownloadEngine engine) => Workers;
        public override void TaskSettled(DownloadTask task, DownloadSettlement.Outcome outcome)
        { lock (Settled) Settled.Add((task.File.FileKey, outcome, task.Error)); }
        public override void Log(string message, bool isError = false) { lock (Logged) Logged.Add(message); }
    }

    private sealed class Sim : IDisposable
    {
        public readonly PendingWrites Pending = new();   // the session's move ledger, one per sim
        public readonly SimGog Gog = new();
        public readonly MemoryStore Store = new();
        public readonly SimHost Host = new();
        public readonly List<SimDrive> Drives = new();
        public readonly string Base, ConfigDir;
        public readonly GogApiClient Api; public readonly HttpClient Http;
        public readonly List<string> Violations = new();
        public string? FirstViolationDetail; public DownloadEngine? Engine;
        public BackupLayout Layout;
        private readonly Random _rng;

        public Sim(int seed, long capacityA, long maxFileA)
        {
            _rng = new Random(seed);
            Base = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "grog-fill-" + Guid.NewGuid().ToString("N"));
            ConfigDir = System.IO.Path.Combine(Base, "cfg"); Directory.CreateDirectory(ConfigDir);
            var a = System.IO.Path.Combine(Base, "driveA"); Directory.CreateDirectory(a);
            Http = new HttpClient(Gog);
            Api = new GogApiClient(Http, new NoAuth()) { DelayAsync = (_, _) => Task.CompletedTask };
            Layout = new BackupLayout(Store.Current, a);
            Drives.Add(new SimDrive { RootId = Store.Current.PrimaryRootId!, Path = a, Capacity = capacityA, MaxFile = maxFileA });
            Host.OnEngine = e =>
            {
                Engine = e;
                e.DebugTrace = msg => { if (msg.StartsWith("room ") || msg.StartsWith("FAIL")) lock (Host.Logged) Host.Logged.Add(DateTime.Now.ToString("mm:ss.fff ") + msg + " | used=" + Drives[0].Used()); };
                e.FreeBytesProbe = p => DriveOf(p)?.Free();
                e.MaxFileBytesProbe = p => DriveOf(p)?.MaxFile ?? long.MaxValue;
                e.ClusterBytesProbe = _ => Cluster;   // the sim drive's own unit (1288)
            };
        }

        private SimDrive? DriveOf(string path) => Drives.FirstOrDefault(d => path.StartsWith(d.Path + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || path.Equals(d.Path, StringComparison.OrdinalIgnoreCase));

        public void AddDrive(long capacity)
        {
            var p = System.IO.Path.Combine(Base, "drive" + (char)('A' + Drives.Count)); Directory.CreateDirectory(p);
            var root = new VolumeService(Store).AddRoot(p);
            Layout = new BackupLayout(Store.Current, Drives[0].Path);
            Assert.True(Layout.IsOnline(root.Id), "the simulation's second drive must bind online");
            Drives.Add(new SimDrive { RootId = root.Id, Path = p, Capacity = capacity });
        }

        /// <summary>A library of games with installers and extras. Real sizes 150 KB - 6 MB with a few 9-12 MB "big"
        /// ones; the LABEL (what GOG advertises, all the planner knows at first) is the real size rounded to the
        /// nearest MB, minimum 1 MB: wrong in both directions, as GOG's are.</summary>
        public void BuildLibrary(int games)
        {
            var m = Store.Current;
            for (int g = 1; g <= games; g++)
            {
                var item = new LibraryItem { GogId = g, Title = "Game " + g, Slug = "game_" + g, Type = ProductType.Game };
                int files = 2 + _rng.Next(4);
                for (int n = 0; n < files; n++)
                {
                    long real = _rng.Next(10) == 0 ? 9 * MB + _rng.NextInt64(3 * MB) : 150 * KB + _rng.NextInt64(6 * MB);
                    long label = Math.Max(1, (real + MB / 2) / MB) * MB;
                    var key = $"/downlink/game_{g}/f{n}";
                    Gog.RealSize[$"g{g}f{n}"] = real;
                    item.Files.Add(new GameFile
                    {
                        GameGogId = g, FileKey = key, Name = $"Game {g} file {n}", Kind = n == 0 ? FileKind.Installer : FileKind.Extra,
                        Os = n == 0 ? "windows" : "", Language = n == 0 ? "English" : "", State = FileState.NotBackedUp,
                        ExpectedSizeBytes = label, ResolvedFileName = $"g{g}f{n}.bin",
                    });
                }
                m.Items.Add(item);
            }
        }

        /// <summary>A second backup folder on drive A's own volume (two roots, one ledger).</summary>
        public void AddRootOnDriveA()
        {
            var p = System.IO.Path.Combine(Base, "shareOfA"); Directory.CreateDirectory(p);
            var root = new VolumeService(Store).AddRoot(p);
            Layout = new BackupLayout(Store.Current, Drives[0].Path);
            Drives[0].GuestPaths.Add(p);
            Drives.Add(new SimDrive { RootId = root.Id, Path = p, Host = Drives[0], Capacity = Drives[0].Capacity, MaxFile = Drives[0].MaxFile });
        }

        /// <summary>The owner's move: the second drive becomes Primary and the first goes on the shelf (Detached),
        /// after its in-progress downloads were carried off it.</summary>
        public PartialCarry.Result ClearAndShelveDriveA()
        {
            var a = Drives[0]; var b = Drives[1];
            var carried = PartialCarry.Carry(Store, Layout, a.RootId, a.Path, b.RootId, Probe());
            var vol = new VolumeService(Store);
            vol.SetPrimary(b.RootId); vol.DetachRoot(a.RootId);
            Layout = new BackupLayout(Store.Current, b.Path);
            Drives.Remove(a); Shelved.Add(a);
            return carried;
        }
        public readonly List<SimDrive> Shelved = new();

        /// <summary>The stick came back under another letter: same bytes, new path.</summary>
        public void RebindDriveA()
        {
            var moved = System.IO.Path.Combine(Base, "driveA_replugged");
            Directory.Move(Drives[0].Path, moved);
            Drives[0].Path = moved;
            Layout = new BackupLayout(Store.Current, moved);
        }

        public IReadOnlyList<DeviceSpace> Probe()
        {
            Sample("plan");
            var primary = Store.Current.PrimaryRootId;
            return Drives.OrderByDescending(d => d.RootId == primary)
                .Select(d => new DeviceSpace(d.RootId, Math.Max(0, d.Free() - Pending.ForVolume(d.Path) - DownloadEngine.FloorBytes), true, (d.Host ?? d).RootId)   // as DeviceSpace.ForRoot does: the floor off the top (1288)
                { MaxFileBytes = d.MaxFile, Path = d.Path, ClusterBytes = Cluster }).ToList();
        }

        public void Sample(string when)
        {
            foreach (var d in Drives.Where(x => x.Host is null))
            {
                // A walk can count one file TWICE (seen in .grog-tmp, then again in its game folder after the save's
                // rename moved it mid-walk). An over-commit is real only if it is still there on a second and third look.
                long used = d.Used();
                for (int again = 0; again < 2 && used > d.Capacity; again++) used = Math.Min(used, d.Used());
                if (used > d.Capacity) lock (Violations)
                {
                    Violations.Add($"{when}: {System.IO.Path.GetFileName(d.Path)} holds {used} of {d.Capacity}");
                    if (FirstViolationDetail is null && Engine is { } e)
                    {
                        var sb = new StringBuilder();
                        foreach (var t in e.Snapshot.Where(t => t.State != DownloadTaskState.Completed))
                            sb.AppendLine($"  task {t.File.FileKey} {t.State} target={t.TargetRootId} total={t.BytesTotal} recv={t.BytesReceived} checked={t.RoomChecked} plan={t.File.PlanSizeBytes} err={t.Error}");
                        var tmp = System.IO.Path.Combine(d.Path, ".grog-tmp");
                        if (Directory.Exists(tmp)) foreach (var f in Directory.EnumerateFiles(tmp)) sb.AppendLine($"  part {System.IO.Path.GetFileName(f)} {new FileInfo(f).Length}");
                        FirstViolationDetail = sb.ToString();
                    }
                }
            }
        }

        public async Task<BackupRunResult> RunAsync(bool resume, int? cancelAfterMs = null, int? drainAfterMs = null, int? undoAfterMs = null)
        {
            if (drainAfterMs is { } dms) _ = Task.Run(async () => { await Task.Delay(dms); for (int i = 0; i < 200 && Engine is null; i++) await Task.Delay(5); if (Engine is { } en) { en.StartNothingNew = true; if (undoAfterMs is { } ums) { await Task.Delay(ums); en.StartNewAgain(); } } });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            if (cancelAfterMs is { } ms) cts.CancelAfter(ms);
            using var stopSampling = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                while (!stopSampling.IsCancellationRequested) { Sample("mid-run"); try { await Task.Delay(3, stopSampling.Token); } catch { } }
            });
            var run = new BackupRun(Store, Api, Http, null, Layout, Drives[0].Path, ConfigDir, Host) { DeviceProbe = Probe, PendingWrites = Pending };
            var result = await run.RunAsync(new BackupRunOptions(Scan: ScanPolicy.Never, FromPersistedQueue: resume), cts.Token);
            stopSampling.Cancel(); await sampler;
            Sample("after run");
            return result;
        }

        public void Dispose() { try { Directory.Delete(Base, true); } catch { } }
    }

    /// <summary>What must hold after a run that was allowed to finish.</summary>
    private static void AssertSettledState(Sim s, BackupRunResult result, string phase)
    {
        var m = s.Store.Current;
        string P(string msg) => $"[{phase}] {msg}";

        Assert.Equal("", string.Join(" | ", s.Violations), P("the drive was over-committed"));
        var failures = s.Host.Settled.Where(x => x.Outcome is DownloadSettlement.Outcome.WillRetry or DownloadSettlement.Outcome.GaveUp).ToList();
        Assert.Equal("", string.Join(" | ", failures.Select(f => $"{f.Key}: {f.Error}")), P("a transfer failed"));
        Assert.True(result.Error is null, P("the run threw: " + result.Error));

        var queue = m.Downloads.Snapshot();
        var queued = queue.Select(q => (q.GogId, q.FileKey)).ToHashSet();
        Assert.Equal("", string.Join(",", queue.Where(q => q.Fits).Select(q => q.FileKey)), P("a file is still flagged Fits after the run drained"));
        Assert.Equal(queue.Count, result.HeldNoSpace, P("the run's held count is the number of won't-fit rows"));

        foreach (var f in m.Items.SelectMany(i => i.Files))
        {
            bool isQueued = queued.Contains((f.GameGogId, f.FileKey));
            long real = s.Gog.RealSize[$"g{f.GameGogId}{f.FileKey.Split('/').Last()}"];
            if (isQueued) { Assert.True(f.State == FileState.NotBackedUp, P($"{f.FileKey} is queued but {f.State}")); continue; }
            Assert.True(f.State is FileState.Present or FileState.Verified, P($"{f.FileKey} left the queue as {f.State}"));
            var path = s.Layout.ResolvePath(f);
            // A shelved (Detached) drive is not bound by the layout: its files still count, read them where they are.
            if (s.Shelved.FirstOrDefault(d => d.RootId == f.RootId) is { } shelf && f.LocalRelativePath is { } rel)
                path = Path.Combine(shelf.Path, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(path is not null && File.Exists(path) && new FileInfo(path).Length == real, P($"{f.FileKey} is not on disk at its real size"));
            Assert.False(f.HasPartial, P($"{f.FileKey} is complete and still claims a partial"));
        }

        // No .part that no queued record owns.
        var owners = m.Items.SelectMany(i => i.Files).Where(f => f.HasPartial && queued.Contains((f.GameGogId, f.FileKey)))
                      .Select(DownloadEngine.PartNameFor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in s.Drives)
        {
            var tmp = Path.Combine(d.Path, ".grog-tmp");
            if (!Directory.Exists(tmp)) continue;
            foreach (var part in Directory.EnumerateFiles(tmp, "*.part"))
                Assert.True(owners.Contains(Path.GetFileName(part)), P($"orphan partial {Path.GetFileName(part)} on {Path.GetFileName(d.Path)}"));
        }

        // Nothing held that would still fit: the live rule the engine applies, per drive.
        foreach (var q in queue)
        {
            var f = m.ItemById(q.GogId)!.Files.First(x => x.FileKey == q.FileKey);
            long real = s.Gog.RealSize[$"g{f.GameGogId}{f.FileKey.Split('/').Last()}"];
            foreach (var d in s.Drives)
            {
                if (real > d.MaxFile) continue;
                long partialHere = f.HasPartial && (f.PartialRootId ?? m.PrimaryRootId) == d.RootId ? f.PartialBytes ?? 0 : 0;
                long need = Grog.Core.Storage.DriveResolver.OnDisk(Math.Max(f.PlanSizeBytes ?? 0, real), Cluster) - partialHere;
                long spendable = d.Free() - DownloadEngine.FloorBytes;   // the floor is nobody's (1288)
                Assert.True(need > spendable, P($"{f.FileKey} is held but needs {need} and {Path.GetFileName(d.Path)} has {spendable} spendable"));
            }
        }
    }

    /// <summary>One seed, the whole story: fill a FAT-limited drive, cancel mid-run, resume to the end, then add a
    /// second drive and resume again.</summary>
    private static async Task RunStory(int seed)
    {
        using var s = new Sim(seed, capacityA: 40 * MB, maxFileA: 8 * MB);
        s.BuildLibrary(games: 14);
        string tag = "seed " + seed;
        try { await Story(s, tag, seed); }
        catch (Exception ex) when (Environment.GetEnvironmentVariable("GROG_FILLSIM_DUMP") == "1")
        {
            var sb = new StringBuilder(ex.Message).AppendLine().AppendLine("--- first violation").AppendLine(s.FirstViolationDetail ?? "").AppendLine("--- log");
            lock (s.Host.Logged) foreach (var l in s.Host.Logged) sb.AppendLine(l);
            sb.AppendLine("--- settled");
            lock (s.Host.Settled) foreach (var x in s.Host.Settled) sb.AppendLine($"{x.Key} {x.Outcome} {x.Error}");
            sb.AppendLine("--- queue");
            foreach (var q in s.Store.Current.Downloads.Snapshot())
            {
                var f = s.Store.Current.ItemById(q.GogId)!.Files.First(x => x.FileKey == q.FileKey);
                sb.AppendLine($"{q.FileKey} fits={q.Fits} target={q.TargetRootId} label={f.ExpectedSizeBytes} wire={f.WireSizeBytes} real={s.Gog.RealSize[$"g{f.GameGogId}{f.FileKey.Split('/').Last()}"]} partial={f.HasPartial}/{f.PartialBytes}@{f.PartialRootId} state={f.State}");
            }
            foreach (var d in s.Drives) sb.AppendLine($"{Path.GetFileName(d.Path)} used={d.Used()} cap={d.Capacity} free={d.Free()}");
            File.WriteAllText(Path.Combine(Path.GetTempPath(), $"fillsim-seed{seed}.txt"), sb.ToString());
            throw;
        }
    }

    private static async Task Story(Sim s, string tag, int seed)
    {

        // 1. A fresh backup, cancelled mid-transfer: partials are left behind on purpose.
        await s.RunAsync(resume: false, cancelAfterMs: 40 + seed % 60);
        Assert.Equal("", string.Join(" | ", s.Violations), $"[{tag} cancelled run] the drive was over-committed");

        // 2. Resume to the end: the drive fills, the rest is held.
        var filled = await s.RunAsync(resume: true);
        AssertSettledState(s, filled, tag + " fill");
        Assert.True(s.Drives[0].Used() > s.Drives[0].Capacity * 8 / 10, $"[{tag}] the drive was actually filled ({s.Drives[0].Used()} of {s.Drives[0].Capacity})");

        // 3. Resume again with nothing changed: nothing may fail, move or be lost.
        var again = await s.RunAsync(resume: true);
        AssertSettledState(s, again, tag + " idle resume");

        // 4. A second drive with no per-file ceiling: the big files and the overflow land there.
        s.AddDrive(capacity: 45 * MB);
        var spilled = await s.RunAsync(resume: true);
        AssertSettledState(s, spilled, tag + " second drive");
    }

    private static async Task RunVariant(int seed, Func<Sim, string, int, Task> story, bool keepOld = false)
    {
        using var s = new Sim(seed, capacityA: 40 * MB, maxFileA: 8 * MB);
        s.Store.Current.KeepOldVersions = keepOld;
        s.BuildLibrary(games: 14);
        try { await story(s, "seed " + seed, seed); }
        catch (Exception ex) when (Environment.GetEnvironmentVariable("GROG_FILLSIM_DUMP") == "1")
        {
            var sb = new StringBuilder(ex.Message).AppendLine().AppendLine("--- log");
            lock (s.Host.Logged) foreach (var l in s.Host.Logged) sb.AppendLine(l);
            lock (s.Host.Settled) foreach (var x in s.Host.Settled) sb.AppendLine($"{x.Key} {x.Outcome} {x.Error}");
            foreach (var q in s.Store.Current.Downloads.Snapshot())
            {
                var f = s.Store.Current.ItemById(q.GogId)!.Files.First(x => x.FileKey == q.FileKey);
                sb.AppendLine($"{q.FileKey} fits={q.Fits} target={q.TargetRootId} label={f.ExpectedSizeBytes} wire={f.WireSizeBytes} partial={f.HasPartial}/{f.PartialBytes}@{f.PartialRootId} state={f.State}");
            }
            foreach (var d in s.Drives) sb.AppendLine($"{Path.GetFileName(d.Path)} used={d.Used()} cap={d.Capacity} free={d.Free()}");
            File.WriteAllText(Path.Combine(Path.GetTempPath(), $"fillsim-variant-seed{seed}.txt"), sb.ToString());
            throw;
        }
    }

    /// <summary>The stick is pulled mid-run and comes back under another path with its partials on it: they are
    /// resumed from there (the .part name carries no drive path), nothing is orphaned, nothing fails.</summary>
    private static async Task ReplugStory(Sim s, string tag, int seed)
    {
        await s.RunAsync(resume: false, cancelAfterMs: 40 + seed % 60);
        int partsBefore = Directory.Exists(Path.Combine(s.Drives[0].Path, ".grog-tmp")) ? Directory.GetFiles(Path.Combine(s.Drives[0].Path, ".grog-tmp"), "*.part").Length : 0;
        s.RebindDriveA();
        int gets = s.Gog.Gets;
        var filled = await s.RunAsync(resume: true);
        if (Environment.GetEnvironmentVariable("GROG_FILLSIM_STATS") == "1") File.AppendAllText(Path.Combine(Path.GetTempPath(), "fillsim-stats.txt"), "\n" + $"STAT replug {tag}: partsBefore={partsBefore} resumed={s.Host.Logged.Count(l => l.Contains("resumed at"))}");
        AssertSettledState(s, filled, tag + " re-plugged fill");
        Assert.True(partsBefore == 0 || s.Gog.Gets > gets, $"[{tag}] the resumed run fetched");
        var again = await s.RunAsync(resume: true);
        AssertSettledState(s, again, tag + " re-plugged idle resume");
    }

    /// <summary>Try Again on files the run held as won't-fit: they go back through the same gate, none fails, none
    /// strikes, and what still does not fit is held again.</summary>
    private static async Task TryAgainStory(Sim s, string tag, int seed)
    {
        await s.RunAsync(resume: false, cancelAfterMs: 40 + seed % 60);
        var filled = await s.RunAsync(resume: true);
        AssertSettledState(s, filled, tag + " fill");
        var m = s.Store.Current;
        using (s.Store.Gate.Enter())
            foreach (var q in m.Downloads.Snapshot().Take(3))
                DownloadSettlement.RequeueFresh(m, q.GogId, m.ItemById(q.GogId)!.Files.First(f => f.FileKey == q.FileKey));
        var retried = await s.RunAsync(resume: true);
        if (Environment.GetEnvironmentVariable("GROG_FILLSIM_STATS") == "1") File.AppendAllText(Path.Combine(Path.GetTempPath(), "fillsim-stats.txt"), "\n" + $"STAT retry {tag}: heldBefore={filled.HeldNoSpace} heldAfter={retried.HeldNoSpace}");
        AssertSettledState(s, retried, tag + " try again");
        Assert.Equal(0, m.Items.SelectMany(i => i.Files).Count(f => f.FailedAttempts > 0), $"[{tag}] a held file took a strike");
        s.AddDrive(capacity: 45 * MB);
        AssertSettledState(s, await s.RunAsync(resume: true), tag + " try again, second drive");
    }

    /// <summary>Keep Old Versions on, drive A full, a second drive present: an update to a file on A is bigger and
    /// spills to B. The old build is archived on A where it sits (a rename, no room charged anywhere).</summary>
    private static async Task UpdateSpillStory(Sim s, string tag, int seed)
    {
        AssertSettledState(s, await s.RunAsync(resume: false), tag + " fill");
        s.AddDrive(capacity: 400 * MB);   // room for everything: the update must be able to land
        AssertSettledState(s, await s.RunAsync(resume: true), tag + " second drive");
        var m = s.Store.Current; var a = s.Drives[0];
        var victims = m.Items.SelectMany(i => i.Files).Where(f => (f.RootId ?? m.PrimaryRootId) == a.RootId && f.State is FileState.Present or FileState.Verified).Take(2).ToList();
        Assert.True(victims.Count > 0, $"[{tag}] something landed on drive A");
        using (s.Store.Gate.Enter())
            foreach (var f in victims)
            {
                var id = $"g{f.GameGogId}{f.FileKey.Split('/').Last()}";
                long real = Math.Min(7 * MB, s.Gog.RealSize[id] + 1 * MB + 300 * KB);
                s.Gog.RealSize[id] = real; s.Gog.NameOf[id] = id + "_v2.bin";
                f.Version = "2.0"; f.State = FileState.Outdated; f.WireSizeBytes = null;
                f.ExpectedSizeBytes = Math.Max(1, (real + MB / 2) / MB) * MB; f.ResolvedFileName = id + "_v2.bin";
                m.Downloads.Enqueue(f.GameGogId, f.FileKey);
            }
        var updated = await s.RunAsync(resume: true);
        var stillQueued = m.Downloads.Snapshot().Select(q => (q.GogId, q.FileKey)).ToHashSet();
        foreach (var f in victims.Where(f => !stillQueued.Contains((f.GameGogId, f.FileKey))))
        {
            var old = m.ItemById(f.GameGogId)!.OldVersionFiles.Where(o => o.FileKey.StartsWith(f.FileKey + "#old:")).ToList();
            Assert.Equal(1, old.Count, $"[{tag}] {f.FileKey}: the old build is tracked once");
            Assert.Equal(a.RootId, old[0].RootId ?? m.PrimaryRootId, $"[{tag}] {f.FileKey}: archived on the drive it sat on");
            Assert.True(File.Exists(Path.Combine(a.Path, old[0].LocalRelativePath!)), $"[{tag}] {f.FileKey}: the archived build is on disk");
        }
        // An updated file still in the queue keeps UpdateAvailable: only that state is excused below.
        foreach (var f in victims.Where(f => stillQueued.Contains((f.GameGogId, f.FileKey)))) { Assert.True(f.State == FileState.Outdated, $"[{tag}] {f.FileKey} held as {f.State}"); f.State = FileState.NotBackedUp; }
        if (Environment.GetEnvironmentVariable("GROG_FILLSIM_STATS") == "1") File.AppendAllText(Path.Combine(Path.GetTempPath(), "fillsim-stats.txt"), "\n" + $"STAT update {tag}: victims={victims.Count} held={victims.Count(f => stillQueued.Contains((f.GameGogId, f.FileKey)))} onB={victims.Count(f => f.RootId == s.Drives[1].RootId)} old={m.Items.Sum(i => i.OldVersionFiles.Count)}");
        AssertSettledState(s, updated, tag + " update");
    }

    /// <summary>Two backup folders on ONE volume share its room: adding the second root to a full stick buys
    /// nothing, and the pair is never over-committed.</summary>
    private static async Task SharedVolumeStory(Sim s, string tag, int seed)
    {
        await s.RunAsync(resume: false, cancelAfterMs: 40 + seed % 60);
        s.AddRootOnDriveA();
        var filled = await s.RunAsync(resume: true);
        if (Environment.GetEnvironmentVariable("GROG_FILLSIM_STATS") == "1") File.AppendAllText(Path.Combine(Path.GetTempPath(), "fillsim-stats.txt"), "\n" + $"STAT shared {tag}: onGuest={s.Store.Current.Items.SelectMany(i => i.Files).Count(f => f.RootId == s.Drives[1].RootId)} held={filled.HeldNoSpace}");
        AssertSettledState(s, filled, tag + " shared volume fill");
        AssertSettledState(s, await s.RunAsync(resume: true), tag + " shared volume idle resume");
        s.AddDrive(capacity: 45 * MB);
        AssertSettledState(s, await s.RunAsync(resume: true), tag + " shared volume, third root");
    }

    /// <summary>Cancel mid-run, add a big drive, carry the in-progress downloads off the stick, shelve the stick,
    /// resume: every partial is resumed from the new drive, none is left behind on the shelved one.</summary>
    private static async Task ShelveStory(Sim s, string tag, int seed)
    {
        await s.RunAsync(resume: false, cancelAfterMs: 40 + seed % 60);
        var m = s.Store.Current;
        int partials = m.Items.SelectMany(i => i.Files).Count(f => f.HasPartial);
        s.AddDrive(capacity: 400 * MB);
        var stick = s.Drives[0];
        var carried = s.ClearAndShelveDriveA();
        Assert.Equal(partials, carried.Carried + carried.Cleared, $"[{tag}] every recorded partial was carried or its record cleared (left {carried.Left})");
        var tmp = Path.Combine(stick.Path, ".grog-tmp");
        Assert.Equal(0, Directory.Exists(tmp) ? Directory.GetFiles(tmp, "*.part").Length : 0, $"[{tag}] no .part stays on the shelved drive");
        int gets = s.Gog.Gets;
        var done = await s.RunAsync(resume: true);
        if (Environment.GetEnvironmentVariable("GROG_FILLSIM_STATS") == "1") File.AppendAllText(Path.Combine(Path.GetTempPath(), "fillsim-stats.txt"), "\n" + $"STAT shelve {tag}: partials={partials} carried={carried.Carried}/{carried.CarriedBytes} cleared={carried.Cleared} resumed={s.Host.Logged.Count(l => l.Contains("resumed at"))} notes={string.Join(" | ", s.Host.Logged.Where(l => l.Contains("resumed") || l.Contains("restarted") || l.Contains("partial")))}");
        AssertSettledState(s, done, tag + " shelved stick");
        Assert.Equal(0, s.Store.Current.Downloads.Snapshot().Count, $"[{tag}] the big drive took everything");
        Assert.Equal(0, Directory.Exists(tmp) ? Directory.GetFiles(tmp, "*.part").Length : 0, $"[{tag}] nothing was written to the shelved drive");
    }

    /// <summary>A move asked for mid-backup: the files in progress finish, nothing new starts, the run ends with the
    /// rest queued and NO partial made; the next run picks the queue up and settles it.</summary>
    private static async Task DrainStory(Sim s, string tag, int seed)
    {
        s.Engine = null;
        var drained = await s.RunAsync(resume: false, drainAfterMs: 30 + seed % 40);
        var m = s.Store.Current;
        Assert.Equal("", string.Join(" | ", s.Violations), $"[{tag} drained run] the drive was over-committed");
        Assert.Equal(0, m.Items.SelectMany(i => i.Files).Count(f => f.HasPartial && (f.PartialBytes ?? 0) > 0 && f.State == FileState.NotBackedUp && !m.Downloads.Snapshot().Any(q => q.FileKey == f.FileKey && q.GogId == f.GameGogId)), $"[{tag}] a partial with no queue row");
        var tmp = Path.Combine(s.Drives[0].Path, ".grog-tmp");
        Assert.Equal(0, Directory.Exists(tmp) ? Directory.GetFiles(tmp, "*.part").Count(p => new FileInfo(p).Length > 0) : 0, $"[{tag}] a drain cuts nothing: no partial is left behind");
        Assert.True(m.Downloads.Snapshot().Count > 0, $"[{tag}] the drain left work queued");
        AssertSettledState(s, await s.RunAsync(resume: true), tag + " after the drain");
    }

    /// <summary>(09-22, owner "Keep downloading") Finish current files, taken back a moment later: whether the undo
    /// lands (the run carries on) or comes too late (it ends drained), the end state is the settled fill.</summary>
    private static async Task UndoDrainStory(Sim s, string tag, int seed)
    {
        s.Engine = null;
        var r = await s.RunAsync(resume: false, drainAfterMs: 20 + seed % 30, undoAfterMs: 5 + seed % 20);
        Assert.Equal("", string.Join(" | ", s.Violations), $"[{tag} undone drain] the drive was over-committed");
        if (s.Store.Current.Downloads.Snapshot().Any(q => q.Fits)) r = await s.RunAsync(resume: true);   // too late: the owner's Resume
        AssertSettledState(s, r, tag + " after keep downloading");
    }

    /// <summary>(09-22, owner) Moves and downloads run side by side. A move onto drive A registers what it still has
    /// to write there (PendingWrites); a fill of A running at the same time must plan around it: never over-commit,
    /// never fail a transfer. The move here is simulated by a registered pending-write ledger that drains over time.</summary>
    private static async Task ParallelMoveStory(Sim s, string tag, int seed)
    {
        // A "move" of 12 MB landing on drive A in 6 steps of 2 MB while the fill runs.
        long pending = 12 * MB; var a = s.Drives[0]; var landing = new List<string>();
        var moveDir = Path.Combine(a.Path, "Games", "moved_in"); Directory.CreateDirectory(moveDir);
        s.Pending.Register(() => new[] { (Path.Combine(moveDir, "x.bin"), Interlocked.Read(ref pending)) });
        var mover = Task.Run(async () =>
        {
            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(15);
                var f = Path.Combine(moveDir, $"in{i}.bin"); File.WriteAllBytes(f, new byte[2 * MB]);
                Interlocked.Add(ref pending, -2 * MB);
            }
        });
        try
        {
            var filled = await s.RunAsync(resume: false);
            await mover;
            // The ledger is conservative while a step lands (bytes on disk AND still pending for a moment), so a file
            // can be held that fits once the move is over: that is the owner's Try Again after the move, run it.
            if (filled.HeldNoSpace > 0) filled = await s.RunAsync(resume: true);
            AssertSettledState(s, filled, tag + " fill beside a move");
        }
        finally { s.Pending.Clear(); }
    }

    [Test] public async Task A_fill_beside_a_move_never_overcommits_seeds_81_to_86() { for (int seed = 81; seed <= 86; seed++) await RunVariant(seed, ParallelMoveStory); }
    [Test] public async Task Keep_downloading_after_finish_settles_seeds_91_to_96() { for (int seed = 91; seed <= 96; seed++) await RunVariant(seed, UndoDrainStory); }
    [Test] public async Task Start_nothing_new_finishes_files_in_progress_seeds_71_to_76() { for (int seed = 71; seed <= 76; seed++) await RunVariant(seed, DrainStory); }
    [Test] public async Task Stick_cleared_and_shelved_with_downloads_in_progress_seeds_61_to_66() { for (int seed = 61; seed <= 66; seed++) await RunVariant(seed, ShelveStory); }
    [Test] public async Task Replugged_under_another_path_with_partials_seeds_21_to_26() { for (int seed = 21; seed <= 26; seed++) await RunVariant(seed, ReplugStory); }
    [Test] public async Task Try_again_after_held_files_seeds_31_to_36() { for (int seed = 31; seed <= 36; seed++) await RunVariant(seed, TryAgainStory); }
    [Test] public async Task Keep_old_versions_update_that_spills_seeds_41_to_46() { for (int seed = 41; seed <= 46; seed++) await RunVariant(seed, UpdateSpillStory, keepOld: true); }
    [Test] public async Task Two_roots_on_one_volume_seeds_51_to_56() { for (int seed = 51; seed <= 56; seed++) await RunVariant(seed, SharedVolumeStory); }

    [Test]
    public async Task Filling_a_drive_never_fails_a_file_or_overcommits_it_seeds_1_to_8()
    {
        for (int seed = 1; seed <= 8; seed++) await RunStory(seed);
    }

    [Test]
    public async Task Filling_a_drive_never_fails_a_file_or_overcommits_it_seeds_9_to_16()
    {
        for (int seed = 9; seed <= 16; seed++) await RunStory(seed);
    }
}

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
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Runs;
using Grog.Core.Scheduling;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

namespace Grog.Core.Tests;

/// <summary>
/// The whole backup sequence in Core (scan -> select -> place -> run -> settle -> verify -> outcome) against a
/// fake GOG that answers both the catalog and the CDN. Written 09-02 as the net under moving the sequence
/// out of the App and the CLI; it is what makes "what works in the App works in the CLI" a tested claim.
/// </summary>
[Trait("run")]
public sealed class BackupRunTests
{
    /// <summary>Catalog + CDN in one handler: owned ids, listing, mod tags, gameDetails, downlink, the file, its checksum.</summary>
    private sealed class FakeGog : HttpMessageHandler
    {
        public List<long> Owned = new() { 1, 2 };
        public HashSet<long> Refused = new();          // downlink 403s
        public HashSet<long> Broken = new();           // CDN 500s: a real failure, strikes apply
        public byte[] Payload = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");   // 32 bytes
        public int ScanCalls, Gets;
        public Action<long>? OnFile;                   // (09-25) runs as a file's bytes are requested: pull a drive here
        public HashSet<long> ExtraOnly = new();        // games whose one file is an extra (routes by the Extras role)
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            string body;
            if (url.EndsWith("/user/data/games")) { ScanCalls++; body = "{\"owned\":[" + string.Join(",", Owned) + "]}"; }
            else if (url.Contains("getFilteredProducts"))
                body = "{\"page\":1,\"totalPages\":1,\"totalProducts\":2,\"products\":["
                     + string.Join(",", Owned.Select(id => $"{{\"id\":{id},\"title\":\"Game {id}\",\"slug\":\"game_{id}\",\"category\":\"RPG\",\"isGame\":true,\"url\":\"/game/game_{id}\"}}"))
                     + "]}";
            else if (url.Contains("catalog.gog.com")) body = "{\"pages\":1,\"products\":[]}";
            else if (url.Contains("/account/gameDetails/"))
            {
                var id = long.Parse(url.Split('/').Last().Replace(".json", ""));
                body = ExtraOnly.Contains(id)
                    ? $"{{\"title\":\"Game {id}\",\"downloads\":[],\"extras\":[{{\"manualUrl\":\"/downlink/game_{id}/extra0\",\"name\":\"Manual\",\"type\":\"manuals\",\"size\":\"32 B\"}}]}}"
                    : $"{{\"title\":\"Game {id}\",\"downloads\":[[\"English\",{{\"windows\":[{{\"manualUrl\":\"/downlink/game_{id}/en1installer0\",\"name\":\"Setup\",\"version\":\"1.0\",\"size\":\"32 B\"}}]}}]],\"extras\":[]}}";
            }
            else if (url.Contains("embed.gog.com/downlink"))
            {
                var id = long.Parse(url.Split("game_")[1].Split('/')[0]);
                if (Refused.Contains(id))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"not for your region\"}") });
                body = $"{{\"downlink\":\"https://cdn.test/file{id}?token=1\",\"checksum\":\"https://cdn.test/file{id}.xml?token=1\"}}";
            }
            else if (url.Contains(".xml"))
            {
                var md5 = Convert.ToHexString(MD5.HashData(Payload)).ToLowerInvariant();
                body = $"<file name=\"setup.exe\" md5=\"{md5}\"/>";
            }
            else
            {
                if (req.Method == HttpMethod.Get) Gets++;
                var id = url.Split("file")[1].Split('?')[0];
                OnFile?.Invoke(long.Parse(id));
                if (Broken.Contains(long.Parse(id)))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") });
                var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
                resp.Content.Headers.ContentLength = Payload.Length;
                resp.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = $"setup_game_{id}_1.0.exe" };
                return Task.FromResult(resp);
            }
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

    private sealed class MemoryStore : IManifestStore
    {
        public LibraryManifest Current { get; } = new();
        public ManifestGate Gate { get; } = new();
        public int Saves;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) { Saves++; return Task.CompletedTask; }
    }

    private sealed class RecordingHost : NullBackupHost
    {
        public List<string> Logged { get; } = new();
        public List<(string Name, DownloadSettlement.Outcome Outcome)> Settled { get; } = new();
        public DownloadEngine? Engine;
        public (int Tasks, int Held, long HeldBytes)? Placement;
        public override void Placed(IReadOnlyList<DownloadTask> tasks, int held, long heldBytes) => Placement = (tasks.Count, held, heldBytes);
        public Action<DownloadEngine>? EngineHook;
        public override void EngineReady(DownloadEngine engine) { Engine = engine; EngineHook?.Invoke(engine); }
        public override void TaskSettled(DownloadTask task, DownloadSettlement.Outcome outcome) => Settled.Add((task.File.Name, outcome));
        public override void Log(string message, bool isError = false) => Logged.Add(message);
    }

    private sealed class Rig : IDisposable
    {
        public FakeGog Gog = new();
        public MemoryStore Store = new();
        public RecordingHost Host = new();
        public string Root, ConfigDir;
        public GogApiClient Api; public HttpClient Http; public BackupLayout Layout;
        public Rig()
        {
            var b = Path.Combine(Path.GetTempPath(), "grog-run-" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(b, "backup"); ConfigDir = Path.Combine(b, "cfg");
            Directory.CreateDirectory(Root); Directory.CreateDirectory(ConfigDir);
            Http = new HttpClient(Gog);
            Api = new GogApiClient(Http, new NoAuth());
            Api.DelayAsync = (_, _) => Task.CompletedTask;
            Layout = new BackupLayout(Store.Current, Root);
        }
        public BackupRun Run() => new(Store, Api, Http, null, Layout, Root, ConfigDir, Host);
        public void Dispose() { try { Directory.Delete(Path.GetDirectoryName(Root)!, true); } catch { } }
    }

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    [Test]
    public async Task A_first_backup_scans_selects_downloads_settles_and_journals()
    {
        using var r = new Rig();
        var result = await r.Run().RunAsync(new BackupRunOptions(Scan: ScanPolicy.IfStale, VerifyAfter: true, JournalCommand: "backup"), Timeout());

        Assert.Equal(ScheduleRunOutcome.Completed, result.Outcome, result.Error?.ToString() ?? "outcome");
        Assert.Equal(1, r.Gog.ScanCalls, "an empty manifest is stale: one scan");
        Assert.Equal(2, result.Queued, "both games' installers selected");
        Assert.Equal(2, result.Completed, "both downloaded");
        Assert.True(r.Store.Current.Items.All(i => i.Files.All(f => f.State == FileState.Verified)), "settled Verified via MD5");
        Assert.Equal(0, r.Store.Current.Downloads.Snapshot().Count, "the persisted queue drained as files settled");
        Assert.NotNull(result.Verify);
        Assert.Equal(2, result.Verify!.Verified + result.Verify.SizeOnlyOk, "verify pass ran over both");
        Assert.True(result.JournalPath is not null && File.Exists(result.JournalPath), "run journal written");
        Assert.Equal(2, r.Host.Settled.Count(s => s.Outcome == DownloadSettlement.Outcome.Completed), "host told per file");
        Assert.True(r.Store.Saves >= 3, "saved after placement, after the run, after verify");
        Assert.True(Directory.EnumerateFiles(r.Root, "setup_game_1_1.0.exe", SearchOption.AllDirectories).Any(), "bytes landed under the real name");
    }

    [Test]
    public async Task A_paused_queue_ends_the_run_as_Skipped_with_the_reason()
    {
        using var r = new Rig();
        r.Store.Current.Downloads.Paused = true;
        var result = await r.Run().RunAsync(new BackupRunOptions(Scan: ScanPolicy.Always), Timeout());

        Assert.Equal(ScheduleRunOutcome.Skipped, result.Outcome, "paused: nothing starts");
        Assert.True(result.QueuePaused, "the result names the pause as the cause");
        Assert.Equal(BackupRun.PausedReason, result.Reason, "and carries the words");
        Assert.Equal(0, r.Gog.ScanCalls, "no work at all, not even the scan");
        Assert.True(r.Store.Current.Downloads.Paused, "the pause is the user's to lift, never the run's");
    }

    [Test]
    public async Task IgnorePause_runs_through_a_paused_queue()
    {
        using var r = new Rig();
        r.Store.Current.Downloads.Paused = true;
        var result = await r.Run().RunAsync(new BackupRunOptions(Scan: ScanPolicy.Always, IgnorePause: true), Timeout());

        Assert.Equal(ScheduleRunOutcome.Completed, result.Outcome, result.Error?.ToString() ?? "outcome");
        Assert.False(result.QueuePaused, "not a paused outcome");
        Assert.Equal(2, result.Completed, "both downloaded");
    }

    // Sweep 2 #28: the import a root owes ran in the App only.
    [Test]
    public async Task A_first_backup_runs_the_import_the_primary_owes()
    {
        using var r = new Rig();
        Assert.True(r.Store.Current.Roots.Single().PendingImport, "the default primary is born owing an import");

        await r.Run().RunAsync(new BackupRunOptions(Scan: ScanPolicy.IfStale, JournalCommand: "backup"), Timeout());

        Assert.False(r.Store.Current.Roots.Single().PendingImport, "paid between the scan and the selection");
        Assert.True(r.Host.Logged.Any(l => l.Contains("for existing backups")), "and it says so");
    }

    [Test]
    public async Task A_recent_scan_is_not_repeated_and_Never_never_scans()
    {
        using var r = new Rig();
        await r.Run().RunAsync(new BackupRunOptions(), Timeout());
        int scans = r.Gog.ScanCalls;
        var again = await r.Run().RunAsync(new BackupRunOptions(Scan: ScanPolicy.IfStale), Timeout());
        Assert.Equal(scans, r.Gog.ScanCalls, "completed a moment ago: no second scan");
        Assert.True(again.NothingToDo, "and nothing left to fetch");
        r.Store.Current.LastSyncCompleted = null;
        await r.Run().RunAsync(new BackupRunOptions(Scan: ScanPolicy.Never), Timeout());
        Assert.Equal(scans, r.Gog.ScanCalls, "Never means never");
        await r.Run().RunAsync(new BackupRunOptions(Scan: ScanPolicy.Always), Timeout());
        Assert.Equal(scans + 1, r.Gog.ScanCalls, "Always scans even when fresh");
    }

    [Test]
    public async Task A_refusal_settles_as_Unavailable_and_the_run_still_completes()
    {
        using var r = new Rig();
        r.Gog.Refused.Add(2);
        var result = await r.Run().RunAsync(new BackupRunOptions(), Timeout());
        Assert.Equal(ScheduleRunOutcome.Completed, result.Outcome, "a refusal is not a failure");
        Assert.Equal(1, result.Refused, "one refused");
        var f2 = r.Store.Current.ItemById(2)!.Files[0];
        Assert.Equal(FileState.Unavailable, f2.State, "GOG's refusal recorded on the file");
        Assert.Equal(0, f2.FailedAttempts, "no strike");
        Assert.Equal(0, r.Store.Current.Downloads.Snapshot().Count, "refused file left the queue too");
        // The journal records the refusal but never counts it as failed: Health reads that number as "files
        // that failed in the last run", and a refusal is not one (the App writes this journal since 09-04).
        Assert.Null(RunJournalReader.ReadLastFailure(r.ConfigDir), "no failure reported for a refusal");
        Assert.True(File.ReadAllText(result.JournalPath!).Contains("\"file.unavailable\""), "but the refusal is on the record");
        Assert.Equal((2, 0, 0L), r.Host.Placement, "host told what was placed before the engine ran");
    }

    [Test]
    public async Task A_canceled_token_names_the_outcome_even_though_the_engine_does_not_throw()
    {
        using var r = new Rig();
        await r.Run().RunAsync(new BackupRunOptions(), Timeout());   // seeds the library
        foreach (var f in r.Store.Current.Items.SelectMany(i => i.Files)) f.State = FileState.NotBackedUp;   // make work
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var result = await r.Run().RunAsync(new BackupRunOptions(Scan: ScanPolicy.Never), cts.Token);
        Assert.Equal(ScheduleRunOutcome.Canceled, result.Outcome, "the token is the fact, not an exception");
        Assert.Null(result.Error, "and no exception surfaced");
    }

    [Test]
    public async Task A_resume_drains_the_persisted_queue_in_its_order_and_drops_present_files()
    {
        using var r = new Rig();
        await r.Run().RunAsync(new BackupRunOptions(), Timeout());
        var m = r.Store.Current;
        var f1 = m.ItemById(1)!.Files[0]; var f2 = m.ItemById(2)!.Files[0];
        f2.State = FileState.NotBackedUp;                 // one gap, one present
        m.Downloads.Enqueue(2, f2.FileKey); m.Downloads.Enqueue(1, f1.FileKey);
        var sel = BackupQueueBuilder.Build(m, r.Layout, new BackupRunOptions(FromPersistedQueue: true));
        Assert.Equal(1, sel.Count, "the present file is dropped from the drain");
        Assert.Equal(2L, sel[0].Item.GogId, "in queue order");
        Assert.False(m.Downloads.Contains(1, f1.FileKey), "and removed from the queue");
    }

    [Test]
    public async Task A_night_with_nothing_new_still_verifies_and_reports_a_missing_file()
    {
        using var r = new Rig();
        await r.Run().RunAsync(new BackupRunOptions(), Timeout());
        var gone = Directory.EnumerateFiles(r.Root, "setup_game_2_1.0.exe", SearchOption.AllDirectories).Single();
        File.Delete(gone);
        var result = await r.Run().RunAsync(new BackupRunOptions(Scan: ScanPolicy.Never, VerifyAfter: true), Timeout());
        Assert.Equal(0, result.Queued, "nothing new to fetch");
        Assert.NotNull(result.Verify);
        Assert.Equal(1, result.Verify!.Missing, "the verify pass ran and noticed");
        Assert.Equal(ScheduleRunOutcome.Partial, result.Outcome, "a missing file is not a completed night");
        Assert.Equal(FileState.Missing, r.Store.Current.ItemById(2)!.Files[0].State);
    }

    [Test]
    public async Task A_failed_file_with_strikes_left_is_Retrying_and_makes_the_run_Partial()
    {
        using var r = new Rig();
        r.Gog.Broken.Add(2);
        var result = await r.Run().RunAsync(new BackupRunOptions(), Timeout());
        Assert.Equal(1, result.Completed, "game 1 landed");
        Assert.Equal(0, result.Failed, "not condemned: strikes remain");
        Assert.Equal(1, result.Retrying, "but it did not land");
        Assert.Equal(1, result.NotLanded);
        Assert.Equal(ScheduleRunOutcome.Partial, result.Outcome, "a run with a failure is partial, retry or not");
        var f2 = r.Store.Current.ItemById(2)!.Files[0];
        Assert.Equal(1, f2.FailedAttempts, "one strike recorded");
        Assert.NotNull(RunJournalReader.ReadLastFailure(r.ConfigDir));
        var failure = RunJournalReader.ReadLastFailure(r.ConfigDir)!.Value;
        Assert.Equal(1, failure.Failed, "a real failure IS counted");
        Assert.Equal((2L, f2.FileKey), failure.FailedFiles().Single(), "and the journal names the file (1280)");
        Assert.True(r.Store.Current.Downloads.Contains(2, f2.FileKey), "and it stays queued for the next run");
    }

    [Test]
    public async Task A_drive_pulled_mid_run_leaves_its_files_waiting_with_no_strike_and_the_rest_lands()
    {
        // (09-25, walk on the Mac) The secondary was force-detached mid-run: every file bound to it failed with a
        // strike and the end said "had no room". Now its files wait for it; the primary's keep landing.
        DriveWaits.Clear();
        using var r = new Rig();
        var secDir = Path.Combine(Path.GetDirectoryName(r.Root)!, "second");
        Directory.CreateDirectory(secDir);
        var sec = new BackupRoot { Label = "GrogTest", PathHint = secDir, State = RootState.Online };
        r.Store.Current.Roots.Add(sec);
        r.Layout = new BackupLayout(r.Store.Current, r.Root);
        // Game 2's one file is an extra, and extras route to the secondary: that is the file bound to the pulled drive.
        r.Gog.ExtraOnly.Add(2);
        r.Store.Current.SetExtrasLayout(ExtrasPlacement.SeparateByGame);
        r.Store.Current.Routing.SetRole(ContentRole.Extras, sec.Id);
        string? wentAway = null;
        // A plain file where the folder was: like an unplugged drive, the folder is gone AND cannot be recreated
        // (on Linux a deleted folder would just be re-made by the next CreateDirectory).
        r.Gog.OnFile = id => { if (id == 2 && Directory.Exists(secDir)) { Directory.Delete(secDir, true); File.WriteAllText(secDir, ""); } };
        var run = r.Run();
        r.Host.EngineHook = e => e.RootWentAway += rid => wentAway = rid;
        try
        {
            var result = await run.RunAsync(new BackupRunOptions(), Timeout());
            Assert.Equal(1, result.Completed, "the primary's file landed");
            Assert.Equal(0, result.NotLanded, "a pulled drive is not a failure");
            Assert.Equal(1, result.DriveOffline, "the pulled drive's file waits for it");
            Assert.Equal(ScheduleRunOutcome.Partial, result.Outcome, "a file left waiting did not land: not Completed");
            Assert.Equal(sec.Id, wentAway, "the host is told which drive went away");
            Assert.True(DriveWaits.Contains(sec.Id), "its files wait for it across re-plans");
            var f2 = r.Store.Current.ItemById(2)!.Files[0];
            Assert.Equal(0, f2.FailedAttempts, "no strike");
            Assert.True(r.Store.Current.Downloads.Contains(2, f2.FileKey), "still queued");
            var q = r.Store.Current.Downloads.Snapshot().First(x => x.GogId == 2);
            Assert.True(q.Fits, "waiting is not won't-fit");
            Assert.Equal(sec.Id, q.TargetRootId, "and it stays headed to its drive (no silent spill)");
            Assert.Equal(0, result.HeldNoSpace, "and the end does not call it 'no room'");

            // The drive is back: the next run takes the waiting file.
            File.Delete(secDir); Directory.CreateDirectory(secDir);
            Grog.Core.Storage.DriveResolver.Probe(secDir, fresh: true);   // the App's watch re-probes before it calls it back
            DriveWaits.Remove(sec.Id);
            r.Gog.OnFile = null;
            r.Layout = new BackupLayout(r.Store.Current, r.Root);
            var again = await r.Run().RunAsync(new BackupRunOptions(Scan: ScanPolicy.Never, FromPersistedQueue: true), Timeout());
            Assert.Equal(1, again.Completed, "the waiting file landed once its drive was back");
            Assert.False(r.Store.Current.Downloads.Contains(2, f2.FileKey), "and left the queue");
        }
        finally { DriveWaits.Clear(); }
    }

    [Test]
    public async Task A_second_run_over_the_same_library_never_starts_while_one_is_live()
    {
        // (09-26) Two engines over one queue downloaded every file twice (grog.log 09-25). The gate is taken before
        // the first await, so a second RunAsync started right after the first is refused, and every file lands once.
        using var r = new Rig();
        var first = r.Run().RunAsync(new BackupRunOptions(), Timeout());
        var second = await r.Run().RunAsync(new BackupRunOptions(), Timeout());
        Assert.Equal(ScheduleRunOutcome.Failed, second.Outcome, "the second run is refused");
        Assert.True(second.Error is RunAlreadyActiveException, "and says why");
        Assert.Equal(0, second.Completed, "it downloaded nothing");
        var done = await first;
        Assert.Equal(ScheduleRunOutcome.Completed, done.Outcome, "the first run is unaffected");
        Assert.False(BackupRun.IsLive(r.Store), "the gate is released when the run ends");
        var again = await r.Run().RunAsync(new BackupRunOptions(), Timeout());
        Assert.True(again.Outcome != ScheduleRunOutcome.Failed, "a later run starts normally");
    }

    [Test]
    public async Task A_canceled_run_is_never_reported_as_nothing_to_do()
    {
        using var r = new Rig();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var result = await r.Run().RunAsync(new BackupRunOptions(Scan: ScanPolicy.Always), cts.Token);
        Assert.Equal(ScheduleRunOutcome.Canceled, result.Outcome, "stopped during the scan");
        Assert.True(result.Canceled);
        Assert.False(result.NothingToDo, "a stopped run has an unknown amount of work left");
    }

    [Test]
    public async Task A_file_held_at_placement_is_admitted_when_room_appears_before_the_run_ends()
    {
        // Two 32 B installers, 40 B free: one fits at Place, one is held. The mid-run Replan only sees what the
        // engine holds, so before 09-18 the held file stayed held, the idle re-check then flipped it, and the
        // next Resume fetched that one file and raised the not-everything-fit dialog again (one file per run).
        // The probe keeps answering 40 B free (the slack GOG's rounded-up sizes leave, in miniature), so the
        // end-of-run pass must admit the second file and the run must end with nothing held.
        using var r = new Rig();
        var run = new BackupRun(r.Store, r.Api, r.Http, null, r.Layout, r.Root, r.ConfigDir, r.Host)
        {
            DeviceProbe = () => r.Store.Current.Roots.Select(x => new DeviceSpace(x.Id, 40, true)).ToList(),
        };
        var result = await run.RunAsync(new BackupRunOptions(), Timeout());

        Assert.Equal(1, r.Host.Placement!.Value.Held, "one file had no room at Place time");
        Assert.Equal(2, result.Completed, "the held file was admitted and downloaded in the same run");
        Assert.Equal(0, result.HeldNoSpace, "nothing is reported as held at the end");
        Assert.True(r.Host.Logged.Any(l => l.Contains("now fit; continuing")), "the admission pass is logged");
        Assert.Equal(0, r.Store.Current.Downloads.Snapshot().Count, "the queue drained");
        Assert.Equal(ScheduleRunOutcome.Completed, result.Outcome);
    }

    [Test]
    public async Task The_mid_run_replan_reads_the_whole_queue_so_a_held_file_is_admitted_before_the_run_ends()
    {
        // (QA 09-18) The old mid-run Replan saw only what the engine held: a file held at Place never got the
        // room a landed file freed. The first completion triggers a whole-queue pass (the cadence clock starts
        // at zero); the probe answers 40 B free throughout, so the held second file must be admitted by THAT
        // pass, logged as a mid-run re-check, and downloaded in the same run.
        using var r = new Rig();
        var run = new BackupRun(r.Store, r.Api, r.Http, null, r.Layout, r.Root, r.ConfigDir, r.Host)
        {
            DeviceProbe = () => r.Store.Current.Roots.Select(x => new DeviceSpace(x.Id, 40, true)).ToList(),
        };
        var result = await run.RunAsync(new BackupRunOptions(), Timeout());

        Assert.Equal(1, r.Host.Placement!.Value.Held, "one file had no room at Place time");
        Assert.Equal(2, result.Completed, "both landed in one run");
        // (09-25) Settling runs on the settler's own worker, so with one file in flight the engine can drain before its
        // settle: then the run's tail (also a whole-queue pass) admits it instead of a mid-run pass. Either is a
        // whole-queue re-check; what matters is that the held file lands in THIS run.
        Assert.True(r.Host.Logged.Any(l => l.Contains("Re-checked what fits")), "a whole-queue pass admitted it: " + string.Join(" | ", r.Host.Logged));
        Assert.Equal(0, result.HeldNoSpace);
    }

    [Test]
    public void KeepOldVersions_travels_with_the_manifest()
    {
        var m = new LibraryManifest { KeepOldVersions = true };
        var json = System.Text.Json.JsonSerializer.Serialize(m);
        var back = System.Text.Json.JsonSerializer.Deserialize<LibraryManifest>(json)!;
        Assert.True(back.KeepOldVersions, "the archive policy is a fact about the backup, not about one machine's app");
        Assert.False(new LibraryManifest().KeepOldVersions, "default off");
    }
}

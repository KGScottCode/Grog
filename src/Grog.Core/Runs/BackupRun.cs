// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.Core.Scheduling;
using Grog.Core.Sync;
using Grog.Core.Verify;
using Grog.Core.Volumes;

namespace Grog.Core.Runs;

/// <summary>
/// THE backup sequence, and the decisions inside it: scan if the catalog is stale, select what to fetch,
/// plan it onto devices and hold what has no room, run the engine with every finished file settled and
/// journalled, verify if asked, and name the outcome. The App and the CLI each had their own copy of this
/// sequence until 09-02, and the copies had drifted: the CLI's nightly `backup` settled nothing and
/// journalled nothing, never saw Ctrl-C (the engine never throws it), and ignored Keep Old Versions.
///
/// <para>Hosts provide inputs (<see cref="BackupRunOptions"/>) and presentation (<see cref="IBackupHost"/>).
/// Everything a host must NOT do is done here; everything here is unit-tested against fakes.</para>
/// </summary>
/// <summary>(09-26) A second run over a library that already has a live one; nothing was started.</summary>
public sealed class RunAlreadyActiveException : InvalidOperationException
{
    public RunAlreadyActiveException() : base("A download run is already active for this library; nothing was started.") { }
}

public sealed class BackupRun
{
    /// <summary>How often the mid-run re-plan may run, at most. It recovers slack from GOG's rounded-up
    /// sizes, which accumulates gradually, so a coarse cadence loses nothing (owner 09-10).</summary>
    internal const int ReplanEvery = 5_000;   // ms
    /// <summary>A free-space INCREASE this large re-plans at once, whatever the cadence: that is a drive
    /// cleanup while the download runs, not the drip of rounded-up sizes.</summary>
    internal const long ReplanFreeJump = 1L << 30;   // 1 GiB

    private readonly IManifestStore _manifest;
    private readonly GogApiClient _api;
    private readonly HttpClient _http;
    private readonly AccountSessions? _sessions;
    private readonly IBackupLayout _layout;
    private readonly string _backupRoot;
    private readonly string _configDir;
    private readonly IBackupHost _host;

    /// <summary>Test seam: the clock the stale-scan rule reads.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;
    /// <summary>Test seam: the disk probe behind every placement decision. Production reads the drives.</summary>
    internal Func<IReadOnlyList<DeviceSpace>>? DeviceProbe { get; init; }
    private IReadOnlyList<DeviceSpace> Devices() => DeviceProbe?.Invoke() ?? new VolumeService(_manifest, PendingWrites).DeviceSpaces(_layout);
    /// <summary>The session's move ledger: placement and the engine's room check both subtract it.</summary>
    public PendingWrites? PendingWrites { get; init; }
    /// <summary>Upper bound on end-of-run admission passes (each one drains before the next; see RunAsync).</summary>
    internal const int MaxAdmissionPasses = 50;

    public BackupRun(IManifestStore manifest, GogApiClient api, HttpClient http, AccountSessions? sessions,
                     IBackupLayout layout, string backupRoot, string configDir, IBackupHost host)
    {
        _manifest = manifest; _api = api; _http = http; _sessions = sessions;
        _layout = layout; _backupRoot = backupRoot; _configDir = configDir; _host = host;
    }

    /// <summary>Does this run refresh the catalog first? The one rule (the App had two copies of "recently").</summary>
    public bool NeedsScan(BackupRunOptions o) => o.Scan switch
    {
        ScanPolicy.Always => true,
        ScanPolicy.Never => false,
        _ => !LibraryScan.IsRecent(_manifest.Current, Now(), o.StaleWindow ?? BackupRunOptions.DefaultStaleWindow),
    };

    /// <summary>(09-26) ONE run per library at a time, in this process. Two engines over one persisted queue downloaded
    /// every pending file twice (grog.log 09-25). The App routes new files into the live engine; this is the backstop
    /// that makes a second run impossible whatever path starts it. (Across processes the app/CLI locks do it.)</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, System.Runtime.CompilerServices.StrongBox<int>> LiveRuns = new();

    /// <summary>True while a run holds this library.</summary>
    public static bool IsLive(object manifestStore)
        => LiveRuns.TryGetValue(manifestStore, out var b) && System.Threading.Volatile.Read(ref b.Value) != 0;

    public async Task<BackupRunResult> RunAsync(BackupRunOptions o, CancellationToken ct)
    {
        var box = LiveRuns.GetValue(_manifest, _ => new System.Runtime.CompilerServices.StrongBox<int>(0));
        if (System.Threading.Interlocked.CompareExchange(ref box.Value, 1, 0) != 0)
            return new BackupRunResult(ScheduleRunOutcome.Failed, null, null, 0, 0, 0, null, 0, 0, 0, 0, 0, null, null,
                new RunAlreadyActiveException());
        try { return await RunCoreAsync(o, ct).ConfigureAwait(false); }
        finally { System.Threading.Volatile.Write(ref box.Value, 0); }
    }

    /// <summary>The words a host prints for a run a paused queue stopped.</summary>
    public const string PausedReason = "The download queue is paused. Resume it, or run with the pause ignored.";

    private async Task<BackupRunResult> RunCoreAsync(BackupRunOptions o, CancellationToken ct)
    {
        SyncResult? scan = null; IReadOnlyList<AccountSyncPass>? passes = null;
        // 0. A paused queue is the user's standing word: nothing starts unless the caller says the pause is theirs to lift.
        if (!o.IgnorePause && _manifest.Read(mm => mm.Downloads.Paused))
            return new BackupRunResult(ScheduleRunOutcome.Skipped, null, null, 0, 0, 0, null, 0, 0, 0, 0, 0, null, null, null)
                { Reason = PausedReason, QueuePaused = true };
        try
        {
            // 1. Catalog.
            if (NeedsScan(o))
            {
                var s = await LibraryScan.RunAsync(_manifest, _api, _sessions, _host, ct);
                scan = s.Aggregate; passes = s.Passes;
                if (s.AllAccountsFailed)
                    return new BackupRunResult(ScheduleRunOutcome.Failed, scan, passes, 0, 0, 0, null, 0, 0, 0, 0, 0, null, null,
                        new AuthExpiredException("No account is signed in; nothing can be fetched."));
            }

            // 1b. Imports owed by storage added before the library had items (the default primary owes one from
            //     birth). Only the App ran these, so the CLI's first backup listed a library that was already on
            //     the drive as not downloaded (sweep 2 #28). Between the scan and the selection, never alongside
            //     the run: both plan placement and save. Nothing pending = no disk work at all.
            if (_manifest.Read(mm => PendingImports.Runnable(mm).Count) > 0)
                await ImportRun.RunPendingAsync(_manifest, _host, ct);

            // 2. Selection + 3. placement. Every selected file is persisted to the queue here.
            var m = _manifest.Current;
            // (09-25) The App awaits this from the UI thread: every whole-library walk, drive probe and placement
            // below runs on a pool thread; only the host callbacks run on the caller's context.
            var selection = await Task.Run(() =>
            {
                using (_manifest.Gate.Enter())   // (manifest gate 09-08) Build prunes the persisted queue; in-memory only
                    return BackupQueueBuilder.Build(m, _layout, o);
            }, ct);
            if (selection.Count == 0)
            {
                // Build already took out what is done, refused or gone. What remains is WAITING (its home drive is
                // unplugged): the blanket Clear() that stood here erased a queue the user never finished (QA 09-19).
                await _manifest.SaveAsync(ct);
                // Nothing new is still a night's run: the verify pass is what notices a file that went missing.
                var v0 = o.VerifyAfter ? await VerifyAsync() : null;
                var out0 = v0 is { } vv && (vv.Corrupt > 0 || vv.Missing > 0) ? ScheduleRunOutcome.Partial : ScheduleRunOutcome.Completed;
                return new BackupRunResult(out0, scan, passes, 0, 0, 0, null, 0, 0, 0, 0, 0, v0, null, null);
            }
            var devices = await Task.Run(Devices, ct);   // disk probes: outside the gate, off the caller's thread
            // (09-19) A label within rounding of a drive's per-file ceiling says nothing: get the real length first.
            var unsettled = CeilingSizeCheck.Unsettled(selection.Select(x => x.File), devices);
            if (unsettled.Count > 0)
            {
                int learned = await CeilingSizeCheck.ResolveAsync(_manifest, unsettled, RealSizeAsync, ct);
                _host.Log($"Checked the real size of {learned} of {Format.Plural.Of(unsettled.Count, "file")} near a drive's per-file limit.");
            }
            List<DownloadTask> tasks = null!; int held = 0; long heldBytes = 0; PlacementPlanner.PlacementPlan plan = null!;
            await Task.Run(() =>
            {
            using (_manifest.Gate.Enter())   // (manifest gate 09-08) Place enqueues every selected file
            {
                (tasks, held, heldBytes, plan) = BackupQueueBuilder.Place(m, _layout, devices, selection);
                // (09-19, walk pass 3) "Back up THESE games" is the user's pick: it goes to the FRONT of the queue.
                // Place only appends or leaves an already-queued file where it sat, and the first mid-run re-plan
                // admits the whole persisted queue in its order -- so a picked game's third file ran from
                // position 46 while strangers from the top of the queue downloaded ahead of it.
                if (o.OnlyGameIds is not null && !o.FromPersistedQueue)
                {
                    if (o.PromotePicked) BackupQueueBuilder.PromoteToFront(m, selection);
                    else
                    {
                        // (09-20) Picked files joined the END (Place appends). The run is the whole queue in its own
                        // order, not the pick first: handing the engine only the pick WAS a jump to the front.
                        selection = BackupQueueBuilder.Build(m, _layout, o with { FromPersistedQueue = true, OnlyGameIds = null });
                        (tasks, held, heldBytes, plan) = BackupQueueBuilder.Place(m, _layout, devices, selection);
                    }
                }
            }
            }, ct);
            var fit = Capacity.Evaluate(selection.Sum(x => x.File.ExpectedSizeBytes ?? 0), m.PrimaryRootId, devices);
            await _manifest.SaveAsync(ct);
            _host.Placed(tasks, held, heldBytes);
            if (tasks.Count == 0)
            {
                // A dead .part may be WHY nothing fits (a 4 GiB orphan held the walk's stick at 0 B free): sweep, and
                // if anything went, read the drives again and place once more before giving up.
                if (await Task.Run(() => SweepOrphans(m, null), ct) > 0)
                {
                    await Task.Delay(2000, ct);   // past the free-space probe's cache
                    devices = await Task.Run(Devices, ct);
                    await Task.Run(() => { using (_manifest.Gate.Enter()) (tasks, held, heldBytes, plan) = BackupQueueBuilder.Place(m, _layout, devices, selection); }, ct);
                    await _manifest.SaveAsync(ct);
                    _host.Placed(tasks, held, heldBytes);
                }
            }
            if (tasks.Count == 0)
            {
                // Nothing at all fits: the host says so from the result instead of running to a zero-file summary.
                return new BackupRunResult(ScheduleRunOutcome.Failed, scan, passes, 0, held, heldBytes, fit, 0, 0, 0, 0, 0, null, null, null);
            }

            // 4. Engine.
            var engine = new DownloadEngine(_api, _http)
            {
                BackupRoot = _backupRoot, Layout = _layout, PendingWrites = PendingWrites, KeepOldVersions = m.KeepOldVersions,
                BytesPerSecondLimit = o.BytesPerSecondLimit,
                Sessions = m.Accounts.Count > 0 ? _sessions : null,
                DeviceMonitor = o.DeviceMonitor,   // hosts pass it only when the worker count is theirs to manage
            };
            // (09-19) The archive record is Core's to write: only the App recorded it, so a CLI or headless run
            // archived the old build and left it untracked (fill simulation). Idempotent; the App's own handler
            // finds the entry already there and only refreshes its page.
            engine.OldVersionArchived += entry =>
            {
                using (_manifest.Gate.Enter())
                    if (m.ItemById(entry.GameGogId) is { } owner && !owner.OldVersionFiles.Any(x => x.FileKey == entry.FileKey))
                        owner.OldVersionFiles.Add(entry);
            };
            engine.PartialNotice = msg => _host.Log(msg);   // (09-19) resumed at N / restarted from zero and why
            if (o.FixedConcurrency is { } fixedN) engine.MaxConcurrent = Math.Clamp(fixedN, 1, 8);
            engine.EnqueueRange(tasks);
            if (_host.PickConcurrency(engine) is { } picked) engine.MaxConcurrent = Math.Clamp(picked, 1, 8);
            _host.EngineReady(engine);

            using var journal = await Task.Run(() => RunJournal.Start(_configDir, o.JournalCommand, tasks.Count));   // folder, prune, fsync: off the caller's thread
            using var settler = new RunSettler(_manifest, engine, journal, _host)
            {
                // (New items 09-09) The run's effective scope, so a completed item drops its New flag
                // against the same scope the selection was built with.
                ClearNewScope = o.ScopeOverride ?? (o.IgnoreSavedScope ? Sync.Scope.Both : m.Scope?.ToScope()),
            };
            // Each landed file can free room the plan did not know about (advertised sizes are rounded up):
            // re-plan what has not started, so a whole game that now fits the primary goes there.
            //
            // THROTTLED (owner 09-10). Unthrottled this ran once per completed file: measured on a real run,
            // 118 passes and 3,846 re-targets in 90 seconds, each one a set of disk probes plus a full
            // PlacementPlanner walk over everything pending. The slack it exists to recover accumulates
            // gradually, so reacting to every single file bought nothing and the same files were re-targeted
            // back and forth. Two triggers, so neither case is lost:
            //   - every ReplanEvery, to fold in the steady drip of rounded-up-size slack;
            //   - immediately when free space GREW by more than ReplanFreeJump since the last pass, which is
            //     the drive-cleanup-during-a-download case (owner: "someone may do a drive cleanup").
            long lastReplan = 0; long lastFreeSeen = -1; int replanInFlight = 0; long lastProbe = long.MinValue / 2; int runEnding = 0;
            // (QA 09-30 A5) The re-plan is a PASS, not a settle: a landed file asks for one, and so does a 5 s clock, so
            // room freed while one long file transfers (a drive cleanup, a move, a delete) is folded in without
            // waiting for the next landing. One in flight; the probe is at most once a second.
            void RequestReplanPass(bool fromSettle)
            {
                // The run's own tail (admission passes, the held count) decides from here on: a mid-run pass that
                // started now could rewrite the flags after the held count was read (fill sim seed 95, 09-25).
                if (Volatile.Read(ref runEnding) != 0) return;
                // A drive probe at most once a second unless a re-plan is due: small files land many per second.
                long tick = Environment.TickCount64;
                if (tick - Interlocked.Read(ref lastReplan) < ReplanEvery && tick - Interlocked.Read(ref lastProbe) < 1000) return;
                // (09-25) Nothing here touches a disk: the drive probe (4.8 s on a busy FAT32 stick) ran on every
                // landed file BEFORE the throttle check, inside the settler's notify, holding up every later settle.
                // Coalesced to one in flight; the probe, the throttle and the re-plan all run on a pool thread.
                if (Interlocked.CompareExchange(ref replanInFlight, 1, 0) != 0) return;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        Interlocked.Exchange(ref lastProbe, Environment.TickCount64);
                        var spaces = Devices();
                        if (Volatile.Read(ref runEnding) != 0) return;   // the tail owns planning now (review 09-25)
                        long freeNow = spaces.Sum(d => d.FreeBytes);
                        long now = Environment.TickCount64;
                        bool due = now - Interlocked.Read(ref lastReplan) >= ReplanEvery;
                        // Against the free seen at the LAST PASS, not the last probe: a 1 GiB gain in 300 MB steps
                        // never tripped the jump when each probe reset the baseline (QA 09-30 A5).
                        long seen = Interlocked.Read(ref lastFreeSeen);
                        bool jumped = seen >= 0 && freeNow - seen >= ReplanFreeJump;
                        if (seen < 0) Interlocked.Exchange(ref lastFreeSeen, freeNow);
                        if (!due && !jumped) return;
                        // The clock alone re-plans only when room appeared: a pass every 5 s over an unchanged drive
                        // re-admitted files the engine turns away for room held by siblings, burning their turn-aways
                        // (review 09-30). A landed file always earns its pass (the rounded-size slack it leaves).
                        if (!fromSettle && !jumped && seen >= 0 && freeNow <= seen) return;
                        Interlocked.Exchange(ref lastReplan, now);
                        Interlocked.Exchange(ref lastFreeSeen, freeNow);

                        // The WHOLE persisted queue, in its order, not just what the engine holds: Replan (engine-pending
                        // only) handed freed room to rows BELOW a held one, so a 1 GB file held at Place watched smaller
                        // files behind it take the space a landed file freed (QA 09-18).
                        var r = BackupQueueBuilder.ReplanWholeQueueDetailed(_manifest, spaces, engine);   // takes the gate itself
                        if (r.Changed > 0) _host.Log($"Re-checked what fits mid-run: {Format.Plural.Of(r.Changed, "file")} changed.");
                    }
                    catch (Exception ex) { _host.Log($"Re-plan skipped: {ex.Message}"); }
                    finally { Interlocked.Exchange(ref replanInFlight, 0); }
                });
            }
            settler.Transition += (t, _) => { if (t.State == DownloadTaskState.Completed) RequestReplanPass(fromSettle: true); };
            using var replanClock = new System.Threading.Timer(_ => RequestReplanPass(fromSettle: false), null, ReplanEvery, ReplanEvery);
            bool canceled = false;
            try { await engine.RunToCompletionAsync(ct); }
            catch (OperationCanceledException) { canceled = true; }
            replanClock.Change(Timeout.Infinite, Timeout.Infinite);
            // The engine does not throw on a canceled token (workers swallow it per task and complete the
            // run), so the token itself is the fact. Both hosts trusted the exception before 09-02.
            canceled |= ct.IsCancellationRequested;
            // Asked to start nothing new ("Finish current files", not taken back): the run stops here like a pause. No admission passes,
            // no orphan sweep, no verify; the queue keeps what is left.
            bool drained = engine.StartNothingNew;
            canceled |= drained;

            // A file held at Place time never enters the engine, and the mid-run Replan only sees what the engine
            // holds, so the slack the LAST files leave (advertised sizes are rounded up) reached nobody: the idle
            // re-check after the run then found "1 file(s) changed", and the next Resume downloaded that one file,
            // ended with the not-everything-fit dialog, and left one more (owner-hit 09-18: 24, then 8, then 1 file
            // per run, a dialog each time). Re-decide over the WHOLE queue against the drives as they are now, in
            // queue order, and keep running while a held file crosses back; the run ends when nothing else fits.
            // Let a mid-run pass that is still running land first: its admissions are Pending tasks the drained engine
            // no longer has workers for, and the loop below is what picks them up.
            // (09-25) Settling runs on the settler's own worker now, so settles (and the mid-run re-plans they start)
            // keep arriving after the engine drained. From here the tail below is the ONE planner: no new mid-run pass
            // may race its admission passes (fill sim seeds 93/95). Let the settler catch up, and any pass already
            // running land, first.
            Interlocked.Exchange(ref runEnding, 1);
            await settler.SettledAsync();
            for (int waited = 0; Volatile.Read(ref replanInFlight) != 0 && waited < 1200; waited++) await Task.Delay(50, CancellationToken.None);
            for (int pass = 0; !canceled && pass < MaxAdmissionPasses; pass++)
            {
                if (engine.StartNothingNew) { canceled = true; drained = true; break; }   // asked mid-admission: stop here too (review 09-19)
                BackupQueueBuilder.ReplanResult admitted;
                try { admitted = await Task.Run(() => BackupQueueBuilder.ReplanWholeQueueDetailed(_manifest, Devices(), engine)); }   // gate NOT held; off the caller's thread
                catch (Exception ex) { _host.Log($"Re-plan skipped: {ex.Message}"); break; }
                int pending = engine.StartablePendingCount();   // (09-25) files waiting for an unplugged drive cannot run now
                if (pending == 0) break;   // Pending here = admitted by this pass or by a late mid-run pass: either way, run it
                _host.Log($"Re-checked what fits: {pending} more {Format.Plural.Noun(pending, "file")} now fit; continuing.");
                try { await engine.RunToCompletionAsync(ct); }
                catch (OperationCanceledException) { canceled = true; }
                canceled |= ct.IsCancellationRequested;
                await settler.SettledAsync();
                for (int waited = 0; Volatile.Read(ref replanInFlight) != 0 && waited < 1200; waited++) await Task.Delay(50, CancellationToken.None);
            }
            await settler.FlushAsync();
            for (int waited = 0; Volatile.Read(ref replanInFlight) != 0 && waited < 1200; waited++) await Task.Delay(50, CancellationToken.None);
            // (09-25) A run that ended on "Finish current files" or a Pause admits nothing more, but its flags must still say what
            // fits NOW: with settling on its own worker the mid-run passes no longer land before the run ends, and a
            // file the last landed ones made room for stayed red (fill sim seed 93). Flags only: the engine is done.
            if (canceled)
                try { await Task.Run(() => BackupQueueBuilder.ReplanWholeQueueDetailed(_manifest, Devices(), null)); }
                catch (Exception ex) { _host.Log($"Re-plan skipped: {ex.Message}"); }
            _host.EngineDone(engine);
            // Nothing left to resume: every .part still in a root's .grog-tmp is an orphan (a re-plan moved its
            // file elsewhere, a 416 restart, an older build). Counted as Grog bytes but owned by nothing.
            // A won't-fit row is queued but owns no transfer, so it does not keep the sweep from running; a
            // file with a partial does (the HasPartial test), whether it fits or not.
            // Owner-aware since 09-19: the gate used to be "no file anywhere has HasPartial", so ONE stale flag
            // (a gave-up file, a removed row) disabled the sweep for good.
            if (!canceled) await Task.Run(() => SweepOrphans(m, engine));

            // 5. Verify.
            var verify = o.VerifyAfter && !canceled ? await VerifyAsync() : null;

            // 6. Outcome.
            // (review 09-25) Waiting = files that stopped when their drive went away PLUS files that never started because
            // it was already gone: both stay queued, neither landed, neither failed.
            int driveOffline = settler.DriveOffline + engine.WaitingPendingKeys().Count(k => !settler.CountedDriveOffline(k.GogId, k.FileKey));
            var outcome = canceled ? ScheduleRunOutcome.Canceled
                // Files left waiting for a disconnected drive did not land: not "Completed" (the CLI's exit 0).
                : settler.Failed + settler.Retrying + driveOffline > 0 || (verify is { } v && (v.Corrupt > 0 || v.Missing > 0)) ? ScheduleRunOutcome.Partial
                : ScheduleRunOutcome.Completed;
            if (journal is { } jf) await Task.Run(() => jf.Finish(outcome));
            // What is STILL held, not what was held at Place time: the admission passes above may have drained some.
            // A canceled run too (QA 09-30): Stop clears the queue, and the Place-time count said "12 had no room" over it.
            using (_manifest.Gate.Enter())
            {
                held = 0; heldBytes = 0;
                foreach (var q in m.Downloads.Snapshot())
                {
                    if (q.Fits) continue;
                    held++;
                    if (m.ItemById(q.GogId)?.Files.FirstOrDefault(f => f.FileKey == q.FileKey) is { } hf) heldBytes += PlacementPlanner.StillNeeds(hf);
                }
            }
            return new BackupRunResult(outcome, scan, passes, tasks.Count, held, heldBytes, fit,
                settler.Completed, settler.Failed, settler.Retrying, settler.Refused, settler.Skipped, verify, journal?.EventsPath, null)
                { DriveOffline = driveOffline };
        }
        catch (OperationCanceledException)
        {
            return new BackupRunResult(ScheduleRunOutcome.Canceled, scan, passes, 0, 0, 0, null, 0, 0, 0, 0, 0, null, null, null);
        }
        catch (Exception ex)
        {
            // The host renders Error from the result (once); no log line here or it prints twice.
            return new BackupRunResult(ScheduleRunOutcome.Failed, scan, passes, 0, 0, 0, null, 0, 0, 0, 0, 0, null, null, ex);
        }
    }

    /// <summary>Delete every old .part no queued file's record owns, and drop the partial record of files that are
    /// no longer queued (their bytes are what is being swept).</summary>
    private int SweepOrphans(LibraryManifest m, DownloadEngine? engine)
    {
        try
        {
            List<Models.GameFile> owners;
            using (_manifest.Gate.Enter())
            {
                var queued = m.Downloads.Snapshot().Select(q => (q.GogId, q.FileKey)).ToHashSet();
                owners = new List<Models.GameFile>();
                foreach (var f in m.Items.ToArray().SelectMany(i => i.Files.ToArray()))
                {
                    if (!f.HasPartial) continue;
                    if (queued.Contains((f.GameGogId, f.FileKey))) owners.Add(f);
                    else { f.HasPartial = false; f.PartialBytes = null; f.PartialRootId = null; }
                }
            }
            engine ??= new DownloadEngine(_api, _http) { BackupRoot = _backupRoot, Layout = _layout, PendingWrites = PendingWrites };
            int swept = engine.SweepOrphanPartials(owners);
            if (swept > 0) _host.Log($"Removed {swept} leftover partial file(s) no download owns.");
            return swept;
        }
        catch { return 0; /* housekeeping: never fails a run */ }
    }

    /// <summary>Size-only: bytes-on-disk against the version we hold. Hash re-verification is out of scope.</summary>
    /// <summary>The CDN's length for one file, asked of an account that owns it. No body is downloaded.</summary>
    private async Task<long?> RealSizeAsync(Models.GameFile f, CancellationToken ct)
    {
        var owner = _sessions?.FirstAuthenticatedOwner(f);
        var api = owner is not null ? _sessions!.ApiFor(owner) : _api;
        return (await api.ResolveDownloadAsync(f.FileKey, ct, probe: true)).ContentLength;
    }

    private async Task<VerifyResult> VerifyAsync()
    {
        var v = await new VerifyService(_manifest, _backupRoot) { Layout = _layout }.RunAsync(VerifyMode.SizeOnly, CancellationToken.None);
        await _manifest.SaveAsync(CancellationToken.None);
        return v;
    }
}

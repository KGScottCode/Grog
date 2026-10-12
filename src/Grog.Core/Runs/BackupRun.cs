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

public sealed class RunAlreadyActiveException : InvalidOperationException
{
    public RunAlreadyActiveException() : base("A download run is already active for this library; nothing was started.") { }
}

public sealed class BackupRun
{
    internal const int ReplanEvery = 5_000;   // ms
    internal const long ReplanFreeJump = 1L << 30;   // 1 GiB

    private readonly IManifestStore _manifest;
    private readonly GogApiClient _api;
    private readonly HttpClient _http;
    private readonly AccountSessions? _sessions;
    private readonly IBackupLayout _layout;
    private readonly string _backupRoot;
    private readonly string _configDir;
    private readonly IBackupHost _host;

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;
    internal Func<IReadOnlyList<DeviceSpace>>? DeviceProbe { get; init; }
    private IReadOnlyList<DeviceSpace> Devices() => DeviceProbe?.Invoke() ?? new VolumeService(_manifest, PendingWrites).DeviceSpaces(_layout);
    public PendingWrites? PendingWrites { get; init; }
    internal const int MaxAdmissionPasses = 50;

    public BackupRun(IManifestStore manifest, GogApiClient api, HttpClient http, AccountSessions? sessions,
                     IBackupLayout layout, string backupRoot, string configDir, IBackupHost host)
    {
        _manifest = manifest; _api = api; _http = http; _sessions = sessions;
        _layout = layout; _backupRoot = backupRoot; _configDir = configDir; _host = host;
    }

    public bool NeedsScan(BackupRunOptions o) => o.Scan switch
    {
        ScanPolicy.Always => true,
        ScanPolicy.Never => false,
        _ => !LibraryScan.IsRecent(_manifest.Current, Now(), o.StaleWindow ?? BackupRunOptions.DefaultStaleWindow),
    };

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, System.Runtime.CompilerServices.StrongBox<int>> LiveRuns = new();

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

            if (_manifest.Read(mm => PendingImports.Runnable(mm).Count) > 0)
                await ImportRun.RunPendingAsync(_manifest, _host, ct);

            // 2. Selection + 3. placement. Every selected file is persisted to the queue here.
            var m = _manifest.Current;
            var selection = await Task.Run(() =>
            {
                using (_manifest.Gate.Enter())   // Build prunes the persisted queue; in-memory only
                    return BackupQueueBuilder.Build(m, _layout, o);
            }, ct);
            if (selection.Count == 0)
            {
                await _manifest.SaveAsync(ct);
                var v0 = o.VerifyAfter ? await VerifyAsync() : null;
                var out0 = v0 is { } vv && (vv.Corrupt > 0 || vv.Missing > 0) ? ScheduleRunOutcome.Partial : ScheduleRunOutcome.Completed;
                return new BackupRunResult(out0, scan, passes, 0, 0, 0, null, 0, 0, 0, 0, 0, v0, null, null);
            }
            var devices = await Task.Run(Devices, ct);   // disk probes: outside the gate, off the caller's thread
            var unsettled = CeilingSizeCheck.Unsettled(selection.Select(x => x.File), devices);
            if (unsettled.Count > 0)
            {
                int learned = await CeilingSizeCheck.ResolveAsync(_manifest, unsettled, RealSizeAsync, ct);
                _host.Log($"Checked the real size of {learned} of {Format.Plural.Of(unsettled.Count, "file")} near a drive's per-file limit.");
            }
            List<DownloadTask> tasks = null!; int held = 0; long heldBytes = 0; PlacementPlanner.PlacementPlan plan = null!;
            await Task.Run(() =>
            {
            using (_manifest.Gate.Enter())   // Place enqueues every selected file
            {
                (tasks, held, heldBytes, plan) = BackupQueueBuilder.Place(m, _layout, devices, selection);
                if (o.OnlyGameIds is not null && !o.FromPersistedQueue)
                {
                    if (o.PromotePicked) BackupQueueBuilder.PromoteToFront(m, selection);
                    else
                    {
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
            engine.OldVersionArchived += entry =>
            {
                using (_manifest.Gate.Enter())
                    if (m.ItemById(entry.GameGogId) is { } owner && !owner.OldVersionFiles.Any(x => x.FileKey == entry.FileKey))
                        owner.OldVersionFiles.Add(entry);
            };
            engine.PartialNotice = msg => _host.Log(msg);   // resumed at N / restarted from zero and why
            if (o.FixedConcurrency is { } fixedN) engine.MaxConcurrent = Math.Clamp(fixedN, 1, 8);
            engine.EnqueueRange(tasks);
            if (_host.PickConcurrency(engine) is { } picked) engine.MaxConcurrent = Math.Clamp(picked, 1, 8);
            _host.EngineReady(engine);

            using var journal = await Task.Run(() => RunJournal.Start(_configDir, o.JournalCommand, tasks.Count));   // folder, prune, fsync: off the caller's thread
            using var settler = new RunSettler(_manifest, engine, journal, _host)
            {
                ClearNewScope = o.ScopeOverride ?? (o.IgnoreSavedScope ? Sync.Scope.Both : m.Scope?.ToScope()),
            };
            long lastReplan = 0; long lastFreeSeen = -1; int replanInFlight = 0; long lastProbe = long.MinValue / 2; int runEnding = 0;
            void RequestReplanPass(bool fromSettle)
            {
                if (Volatile.Read(ref runEnding) != 0) return;

                long tick = Environment.TickCount64;

                if (tick - Interlocked.Read(ref lastReplan) < ReplanEvery && tick - Interlocked.Read(ref lastProbe) < 1000) return;
                if (Interlocked.CompareExchange(ref replanInFlight, 1, 0) != 0) return;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        Interlocked.Exchange(ref lastProbe, Environment.TickCount64);
                        var spaces = Devices();

                        if (Volatile.Read(ref runEnding) != 0) return;   // the tail owns planning now

                        long freeNow = spaces.Sum(d => d.FreeBytes);
                        long now = Environment.TickCount64;
                        bool due = now - Interlocked.Read(ref lastReplan) >= ReplanEvery;
                        long seen = Interlocked.Read(ref lastFreeSeen);
                        bool jumped = seen >= 0 && freeNow - seen >= ReplanFreeJump;

                        if (seen < 0) Interlocked.Exchange(ref lastFreeSeen, freeNow);
                        if (!due && !jumped) return;
                        if (!fromSettle && !jumped && seen >= 0 && freeNow <= seen) return;

                        Interlocked.Exchange(ref lastReplan, now);
                        Interlocked.Exchange(ref lastFreeSeen, freeNow);

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
            canceled |= ct.IsCancellationRequested;
            bool drained = engine.StartNothingNew;
            canceled |= drained;
            Interlocked.Exchange(ref runEnding, 1);
            await settler.SettledAsync();
            for (int waited = 0; Volatile.Read(ref replanInFlight) != 0 && waited < 1200; waited++) await Task.Delay(50, CancellationToken.None);
            for (int pass = 0; !canceled && pass < MaxAdmissionPasses; pass++)
            {
                if (engine.StartNothingNew) { canceled = true; drained = true; break; }   // asked mid-admission: stop here too
                BackupQueueBuilder.ReplanResult admitted;
                try { admitted = await Task.Run(() => BackupQueueBuilder.ReplanWholeQueueDetailed(_manifest, Devices(), engine)); }   // gate NOT held; off the caller's thread
                catch (Exception ex) { _host.Log($"Re-plan skipped: {ex.Message}"); break; }
                int pending = engine.StartablePendingCount();   // files waiting for an unplugged drive cannot run now
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
            if (canceled)
                try { await Task.Run(() => BackupQueueBuilder.ReplanWholeQueueDetailed(_manifest, Devices(), null)); }
                catch (Exception ex) { _host.Log($"Re-plan skipped: {ex.Message}"); }
            _host.EngineDone(engine);
            if (!canceled) await Task.Run(() => SweepOrphans(m, engine));

            // 5. Verify.
            var verify = o.VerifyAfter && !canceled ? await VerifyAsync() : null;

            // 6. Outcome.
            int driveOffline = settler.DriveOffline + engine.WaitingPendingKeys().Count(k => !settler.CountedDriveOffline(k.GogId, k.FileKey));
            var outcome = canceled ? ScheduleRunOutcome.Canceled
                : settler.Failed + settler.Retrying + driveOffline > 0 || (verify is { } v && (v.Corrupt > 0 || v.Missing > 0)) ? ScheduleRunOutcome.Partial
                : ScheduleRunOutcome.Completed;
            if (journal is { } jf) await Task.Run(() => jf.Finish(outcome));
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
            return new BackupRunResult(ScheduleRunOutcome.Failed, scan, passes, 0, 0, 0, null, 0, 0, 0, 0, 0, null, null, ex);
        }
    }

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

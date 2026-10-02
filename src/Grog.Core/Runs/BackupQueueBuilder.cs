// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Volumes;

namespace Grog.Core.Runs;

/// <summary>
/// THE selection: which (game, file) pairs a run fetches, from the manifest and the run's options. Pure.
/// Before 09-02 the App (DownloadFiltered) and the CLI (download, backup) each spelled this out; they had
/// already drifted (the CLI skipped files on an offline root, the App did not; the CLI re-fetched a present
/// file whose bytes were gone, re-deriving Missing at a call site that Verify owns).
/// </summary>
public static class BackupQueueBuilder
{
    /// <remarks>Selection only: nothing is queued or saved. The one mutation is <c>RetryFailed</c>, which
    /// re-admits Corrupt files by clearing their strikes in memory (a dry run shows what a retry would pick).</remarks>
    /// <param name="layout">Answers "is this file's home drive plugged in". Null skips that rule, for the cheap
    /// counts a label needs on every selection change (a real layout scans volumes to construct).</param>
    public static List<(LibraryItem Item, GameFile File)> Build(LibraryManifest manifest, IBackupLayout? layout, BackupRunOptions o)
    {
        // A label count passes no layout: CPU only, under the UI budget at 5000 files (stress 09-25). With a layout it
        // probes the roots, which the UI thread never does.
        if (layout is not null) UiThreadGuard.NotOnUi("building the backup selection");
        var picked = new List<(LibraryItem, GameFile)>();

        if (o.FromPersistedQueue)
        {
            // Resume / launch drain: the persisted queue is the source of truth and its ORDER is the order
            // that runs (owner 09-02: pause and resume must be exactly that). A queued file already present
            // on disk leaves the queue instead of being fetched again.
            foreach (var q in manifest.Downloads.Snapshot())
            {
                var item = manifest.ItemById(q.GogId);
                var f = item?.Files.FirstOrDefault(x => x.FileKey == q.FileKey);
                // A phantom (its game or file left the library) leaves too: skipped, it kept a stale Fits flag for
                // good, inflating "N had no room" at 0 bytes or blocking the orphan sweep (QA 09-19).
                if (item is null || f is null) { manifest.Downloads.Remove(q.GogId, q.FileKey); continue; }
                if (BackupScope.IsPresent(f)) { manifest.Downloads.Remove(q.GogId, q.FileKey); continue; }
                // Unavailable leaves the queue the same way a refusal does at settlement: GOG has nothing to
                // serve (a sync marked it sizeless, 09-13), so there is no transfer to hold a row for.
                if (f.State == FileState.Unavailable) { manifest.Downloads.Remove(q.GogId, q.FileKey); continue; }
                // Same rule as the fresh path below: a file whose home drive is unplugged waits for it. The drain
                // fetched an Outdated file onto another root and stranded the old build (QA 09-18). It stays queued.
                if (layout is not null && f.RootId is not null && !layout.IsOnline(f.RootId)) continue;
                picked.Add((item, f));
            }
            return picked;
        }

        // The scope: an explicit override (a right-click "back up games only"), else the manifest's saved
        // scope, unless the caller lifted it. Null scope = everything the file set offers.
        Scope? scope = o.ScopeOverride ?? (o.IgnoreSavedScope ? null : manifest.Scope?.ToScope());

        IEnumerable<(LibraryItem game, GameFile file)> candidates = o.RunScope is { } rs
            ? DownloadFileSelector.Select(manifest.Items, rs)
            : manifest.Items.ToArray().SelectMany(i => i.Files.ToArray().Select(f => (game: i, file: f)));   // snapshots: a UI render may be enumerating

        foreach (var (item, f) in candidates.ToList())
        {
            if (o.OnlyGameIds is not null && !o.OnlyGameIds.Contains(item.GogId)) continue;
            if (scope is { } sc && !sc.Includes(f)) continue;
            if (o.ViewFilter is not null && !o.ViewFilter(item, f)) continue;
            // A file whose home root is unplugged is not fetched onto another device: that would split one
            // file across two roots and strand its Old Versions step. It waits for its drive.
            if (layout is not null && f.RootId is not null && !layout.IsOnline(f.RootId)) continue;
            // A condemned file has burned its automatic attempts; only an explicit retry re-admits it.
            if (o.RetryFailed && f.State == FileState.Corrupt) RetryPolicy.ManualRetry(f);
            bool need = o.UpdatesOnly ? f.State == FileState.Outdated : BackupScope.NeedsFetch(f);
            if (!need) continue;
            picked.Add((item, f));
        }

        if (o.KeepLargest is { } n && n > 0)
            picked = picked.OrderByDescending(x => x.Item2.ExpectedSizeBytes ?? 0).Take(n).ToList();
        return picked;
    }

    /// <summary>Re-plan the files that have not started against the devices' CURRENT free space. The plan is
    /// made once at Back up with GOG's advertised sizes, which are rounded up; every file that lands smaller
    /// leaves room the plan never knew about, and by the end of a run a whole game that would now fit the
    /// primary is on the secondary instead (owner, 09-05: three games under the 1.09 GB left). Per file, in
    /// queue order (09-11): nothing is regrouped or reordered. Persists the new targets and Fits flags.</summary>
    /// <param name="dropped">Pending tasks that STOPPED fitting. Their record says won't-fit, but they are still
    /// in the engine; the caller must <c>CancelRange</c> them AFTER releasing the manifest gate (a cancel raises
    /// TaskChanged into the settler, which takes the gate -- see <see cref="ReplanWholeQueue"/>). Before 09-13
    /// they stayed Pending and downloaded anyway, even after the user removed the red rows.</param>
    /// <returns>How many targets changed.</returns>
    public static int Replan(LibraryManifest manifest, IReadOnlyList<DeviceSpace> devices, DownloadEngine engine,
                             out List<DownloadTask> dropped)
    {
        var drop = new List<DownloadTask>();
        int changed = engine.Retarget((pending, active) =>
        {
            // In-flight bytes are spoken for on their device.
            var adjusted = ReserveInFlight(devices, active, manifest.PrimaryRootId);

            var files = pending.Select(t => t.File).ToList();
            var plan = PlacementPlanner.PlanQueueDetailed(files, adjusted, manifest.Routing, manifest.PrimaryRootId!,
                                                          manifest.Routing.Mode, manifest.ExtrasLayout,
                                                          pending.Select(t => t.TargetRootId).ToList());
            var answer = new Dictionary<Guid, string?>(pending.Count);
            for (int i = 0; i < pending.Count; i++)
            {
                var p = plan.Placements[i];
                // SetPlacement, never Enqueue: a file whose entry is already gone (completed and settled, or
                // cancelled by its row's x) must not be re-added as a phantom row the engine does not hold.
                if (!manifest.Downloads.SetPlacement(pending[i].File.GameGogId, pending[i].File.FileKey, p.RootId, p.Fits)) continue;
                if (!p.Fits || string.IsNullOrEmpty(p.RootId)) { drop.Add(pending[i]); continue; }   // keeps its position, waits
                answer[pending[i].Id] = p.RootId;
            }
            return answer;
        });
        dropped = drop;
        return changed;
    }

    /// <summary>The devices with every in-flight transfer's remaining bytes taken off. Charged to every root on the
    /// SAME volume (DeviceSpace.Key), not just the target root: two roots on one drive spend the same free space.</summary>
    internal static List<DeviceSpace> ReserveInFlight(IReadOnlyList<DeviceSpace> devices, IEnumerable<DownloadTask> active, string? primaryRootId)
    {
        var free = devices.ToDictionary(d => d.RootId, d => d.FreeBytes);
        var keyOf = devices.ToDictionary(d => d.RootId, d => d.Key);
        foreach (var a in active)
        {
            var rid = a.TargetRootId ?? primaryRootId ?? "";
            if (!keyOf.TryGetValue(rid, out var key)) continue;
            // The engine's own figure (DownloadEngine.OthersStillToWrite): the real size once known, one write buffer
            // of received bytes not yet on the disk, and the file's allocation cost. A plan that reserved less than
            // the engine's live check admitted files the engine then turned away (fill simulation 09-19).
            // Unchecked: its partial may not be on this drive yet, unless the record says it sits on this very volume
            // (then the free figure already excludes those bytes; QA 09-30 A9).
            long onDisk = a.RoomChecked ? Math.Max(0, a.BytesReceived - DownloadEngine.WriteBufferBytes)
                        : a.File.HasPartial && a.File.PartialRootId is { } pr && keyOf.TryGetValue(pr, out var pkey) && pkey == key
                          ? a.File.PartialBytes ?? 0 : 0;
            foreach (var d in devices) if (d.Key == key)
            {
                long left = Math.Max(0, d.Charge(a.BytesTotal ?? a.File.PlanSizeBytes ?? 0) - onDisk);
                free[d.RootId] = Math.Max(0, free[d.RootId] - left);
            }
        }
        return devices.Select(d => d with { FreeBytes = free[d.RootId] }).ToList();
    }

    /// <summary>Convenience for callers with no engine work to do afterwards (tests). Production callers use the
    /// overload with <c>dropped</c> and cancel those tasks.</summary>
    public static int Replan(LibraryManifest manifest, IReadOnlyList<DeviceSpace> devices, DownloadEngine engine)
        => Replan(manifest, devices, engine, out _);

    /// <summary>Re-decide what fits over the WHOLE persisted queue, in its current order, and reconcile the
    /// engine to the answer. Returns how many files changed side (started fitting, or stopped).</summary>
    /// <remarks>
    /// Placement is order-dependent: <see cref="PlacementPlanner.PlanQueueDetailed"/> walks the queue in order
    /// and consumes each device's free space greedily, so a different order yields a different set of
    /// won't-fit files. Re-sorting the queue therefore invalidates the fit decision, and this recomputes it.
    ///
    /// It exists alongside <see cref="Replan"/> because Replan works through <c>engine.Retarget</c>, whose
    /// <c>pending</c> list is what the engine HOLDS. A file that did not fit at <see cref="Place"/> time was
    /// never handed to the engine, so no amount of re-planning could ever see it again: it was pinned to
    /// "won't fit" for the life of the run no matter how the queue was reordered or how much space came free
    /// (owner 09-10). This reads the persisted queue instead, so a file can cross back.
    ///
    /// Files already in flight are left alone and their remaining bytes are reserved on their device, exactly
    /// as Replan does: a re-sort pre-empts, it does not strand a partial transfer.
    ///
    /// THREADING -- the caller must NOT hold the manifest gate. This takes it itself, for the read and the
    /// record writes only, and touches the engine with NO gate held. Cancel/Enqueue/Reorder raise TaskChanged
    /// SYNCHRONOUSLY, which lands in RunSettler.SettlePass; that takes the settler lock and THEN the manifest
    /// gate. Holding the gate across an engine call therefore deadlocks against any worker already inside
    /// SettlePass, and with a queue-sized batch of cancels it does so reliably (owner-hit 09-10, hard lock on
    /// "Installers first"). Replan gets away with running under the gate only because engine.Retarget raises
    /// no events.
    /// A file that stops fitting is CANCELED out of the engine, which by
    /// <see cref="DownloadSettlement"/>'s contract leaves its queue entry in place -- it is still queued and
    /// still shown, it simply has nowhere to land.
    /// </remarks>
    /// <param name="engine">Null when no run is live: the records (target + Fits) are still rewritten, so the
    /// red rows, the banner and "Remove them" read one fact whether or not a run is up. Before 09-13 an idle
    /// sort or an idle Storage tick left the flags from the previous order, and the banner (a fresh projection)
    /// disagreed with the rows (persisted Fits).</param>
    /// <summary>How often one run lets the engine's live room check turn the same file away before it stops
    /// re-admitting it. Being turned away while other transfers hold the room is normal and clears when they finish.</summary>
    internal const int MaxTurnAways = 6;

    public static int ReplanWholeQueue(
        IManifestStore store, IReadOnlyList<DeviceSpace> devices, DownloadEngine? engine)
        => ReplanWholeQueueDetailed(store, devices, engine).Changed;

    /// <summary>What a whole-queue re-plan leaves: how many targets changed, and per device the free bytes left
    /// after every fitting file has its room (the in-flight reservation already taken). Empty when the queue
    /// held no candidate.</summary>
    public sealed record ReplanResult(int Changed, IReadOnlyList<DeviceSpace> FreeAfter)
    {
        /// <summary>Where the pass spent its time, "step ms" pairs in order (walk 09-25: a re-sort re-plan took 5 to
        /// 24 s on the UI thread and nothing said where). Diagnostic only.</summary>
        public string Timing { get; init; } = "";
        public long TotalMs { get; init; }
    }

    /// <inheritdoc cref="ReplanWholeQueue"/>
    public static ReplanResult ReplanWholeQueueDetailed(
        IManifestStore store, IReadOnlyList<DeviceSpace> devices, DownloadEngine? engine)
    {
        // (QA 09-30 A12) One whole-queue pass at a time per manifest: an App pass (sort, drag, Add to queue) and the
        // run's own pass raced, and the slower one's SetPlacement(false) + CancelRange landed over the fresher
        // pass's Fits=true, leaving a fitting row white, out of the engine and never fetched.
        var gate = PassGates.GetValue(store, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try { return ReplanWholeQueueDetailed(store, devices, engine, afterRelease: false); }
        finally { gate.Release(); }
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IManifestStore, SemaphoreSlim> PassGates = new();

    private static ReplanResult ReplanWholeQueueDetailed(
        IManifestStore store, IReadOnlyList<DeviceSpace> devices, DownloadEngine? engine, bool afterRelease)
    {
        UiThreadGuard.NotOnUi("the whole-queue fit re-check");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var marks = new System.Text.StringBuilder();
        long last = 0;
        void Mark(string step) { long now = clock.ElapsedMilliseconds; marks.Append(step).Append(' ').Append(now - last).Append(" ms, "); last = now; }
        ReplanResult Done(ReplanResult r) => r with { Timing = marks.ToString().TrimEnd(',', ' '), TotalMs = clock.ElapsedMilliseconds };
        var manifest = store.Current;

        // What the engine holds right now, by file identity.
        // TWO sets on purpose. `held` is what can still be dropped (Pending/Active). `known` is EVERY state,
        // including Completed, and exists to suppress a re-add: a finished task stays in the engine's queue,
        // and its manifest entry is removed a moment later by the settler. In that window a Completed file
        // would look like one the engine does not have, and this would enqueue it a SECOND time -- a duplicate
        // download caused by nothing but sorting at the wrong instant.
        var held = new Dictionary<(long, string), DownloadTask>();
        var known = new HashSet<(long, string)>();
        var active = new List<DownloadTask>();
        var turnedAwayCount = new Dictionary<(long, string), int>();
        foreach (var t in engine?.Snapshot ?? Enumerable.Empty<DownloadTask>())
        {
            // A task cancelled while ACTIVE stays in the engine's list as Canceled (only Pending ones are removed),
            // and counting it as "known" pinned its file out of the engine for the rest of the run: the flag said
            // Fits, nothing fetched it (QA 09-18). Canceled is not a membership; Completed still is (see above).
            // A task the engine turned away for lack of room (Failed + DiskFull) is not a membership either: the
            // file is held as won't-fit and comes back when a plan finds it room. Turned away MaxTurnAways times in one run it
            // stays out (and reads won't-fit), so a plan and the engine's live check can never trade the same file back and forth.
            bool turnedAway = t.State == DownloadTaskState.Failed && t.DiskFull;
            if (turnedAway) turnedAwayCount[(t.File.GameGogId, t.File.FileKey)] = turnedAwayCount.GetValueOrDefault((t.File.GameGogId, t.File.FileKey)) + 1;
            // (09-25) A file stopped because its drive went away is not a membership either: when the drive is back (or
            // the file is sent elsewhere) a re-plan must hand it to the engine again.
            bool driveGone = t.State == DownloadTaskState.Failed && t.DriveGone;
            if (t.State != DownloadTaskState.Canceled && !turnedAway && !driveGone) known.Add((t.File.GameGogId, t.File.FileKey));
            if (t.State == DownloadTaskState.Active) active.Add(t);
            if (t.State is DownloadTaskState.Pending or DownloadTaskState.Active)
                held[(t.File.GameGogId, t.File.FileKey)] = t;
        }
        var capped = turnedAwayCount.Where(kv => kv.Value >= MaxTurnAways).Select(kv => kv.Key).ToHashSet();
        foreach (var k in capped) known.Add(k);

        Mark("engine snapshot");
        // In-flight bytes are spoken for on their device (same reservation Replan makes).
        var adjusted = ReserveInFlight(devices, active, manifest.PrimaryRootId);

        // Plan over the queue IN ORDER, skipping what is already in flight (it is homed, not a candidate).
        // Snapshot and candidate walk under ONE gate hold: a sync mutating Items/Files mid-enumeration threw
        // "Collection was modified" and the caller logged "Fit re-check skipped", leaving stale flags.
        var snap = new List<QueuedDownload>();
        var cands = new List<(LibraryItem Item, GameFile File, bool WasFit)>();
        var candRoots = new List<string?>();   // (09-25) each candidate's current target, parallel to cands
        using (store.Gate.Enter())
        {
            snap = manifest.Downloads.Snapshot();
            foreach (var q in snap)
            {
                if (held.TryGetValue((q.GogId, q.FileKey), out var t) && t.State == DownloadTaskState.Active) continue;
                var item = manifest.ItemById(q.GogId);
                var f = item?.Files.FirstOrDefault(x => x.FileKey == q.FileKey);
                if (item is null || f is null) continue;
                // Same rule as Build: a file whose home drive (its old build) is unplugged waits for it. Walked here
                // without the rule, the re-plan spilled the new build onto the primary beside the old one (QA 09-30).
                if (f.RootId is not null && devices.FirstOrDefault(d => d.RootId == f.RootId) is { IsOnline: false }) continue;
                cands.Add((item, f, q.Fits));
                candRoots.Add(q.TargetRootId);
            }
        }
        Mark("queue walk");
        if (cands.Count == 0) return Done(new ReplanResult(0, adjusted.Where(d => d.IsOnline).ToList()));

        var plan = PlacementPlanner.PlanQueueDetailed(
            cands.Select(x => x.File).ToList(), adjusted, manifest.Routing, manifest.PrimaryRootId!,
            manifest.Routing.Mode, manifest.ExtrasLayout, candRoots);
        Mark("plan");

        var deadPartials = new List<(LibraryItem Item, GameFile File)>();
        int changed = 0;
        var toAdd = new List<DownloadTask>();
        var toDrop = new List<DownloadTask>();
        var retarget = new Dictionary<Guid, string?>();
        // Decide, and write the records, under the gate. No engine call in here (see THREADING above).
        using (store.Gate.Enter())
        {
            for (int i = 0; i < cands.Count; i++)
            {
                var (item, f, wasFit) = cands[i];
                var p = plan.Placements[i];   // positional: a FileKey can repeat across games (shared extras)
                var key = (f.GameGogId, f.FileKey);
                if (capped.Contains(key)) p = p with { Fits = false };   // out for this run: the flag must say so
                bool has = held.TryGetValue(key, out var task);
                // SetPlacement, never Enqueue: an entry that left the queue since the snapshot (settled, or
                // cancelled by its row's x) stays gone instead of coming back as a phantom row.
                if (!manifest.Downloads.SetPlacement(item.GogId, f.FileKey, p.RootId, p.Fits)) continue;
                // "Changed" = the flag flipped, or the engine's membership did. With NO engine only the flag
                // counts: counting the would-be adds reported every fitting file as changed on every idle
                // re-plan ("14 file(s) changed" on each drag; measured 09-16).
                bool changedHere = wasFit != p.Fits;
                if (p.Fits && !string.IsNullOrEmpty(p.RootId))
                {
                    if (has) retarget[task!.Id] = p.RootId;   // applied under the ENGINE lock below, not here
                    else if (engine is not null && !known.Contains(key))
                    {
                        toAdd.Add(new DownloadTask { File = f, GameTitle = item.Title, GameSlug = item.Slug,
                                                     TargetRootId = p.RootId, BytesReceived = f.PartialBytes ?? 0 });
                        changedHere = true;   // it fits now and the engine did not have it
                    }
                }
                else if (has) { toDrop.Add(task!); changedHere = true; }   // it no longer fits: out of the engine, still queued
                // A won't-fit file with a partial the engine does not hold: a candidate for the dead-partial release
                // below (a held task is being dropped this pass; the next one sees it).
                if (!p.Fits && !has && f.HasPartial) deadPartials.Add((item, f));
                if (changedHere) changed++;
            }
        }
        Mark("decide");
        // A partial sitting on a volume that can NEVER hold its file (over the per-file ceiling: FAT32, 4 GiB - 1)
        // only eats the room other files need: a 4 GiB dead .part left "0 B still free" (walk 09-19). Released
        // only with every root connected (an unplugged drive may be where the file can finish, and the stray
        // adoption would carry the bytes there), and the record follows the DISK: cleared when no .part is left.
        if (!afterRelease && deadPartials.Count > 0 && devices.All(d => d.IsOnline))
        {
            var freedByKey = new Dictionary<string, long>();
            var gone = new List<GameFile>();
            foreach (var (_, f) in deadPartials)   // file I/O outside the gate
            {
                bool left = false;
                foreach (var d in devices)
                {
                    if (string.IsNullOrEmpty(d.Path)) { left = true; continue; }   // cannot look: assume it is there
                    try
                    {
                        // The ceiling test first: it needs no disk. Probing every red partial on a busy USB stick on every
                        // pass took 1.8 to 11.8 s of each re-plan (measured, walk 09-25), for files that were never dead.
                        if (!PlacementPlanner.OverCeiling(PlacementPlanner.ToPlace(f), d.MaxFileBytes)) { left = true; continue; }   // resumable here
                        var part = DownloadEngine.PartialPathFor(d.Path, f);
                        if (!System.IO.File.Exists(part)) continue;
                        long len = new System.IO.FileInfo(part).Length;
                        System.IO.File.Delete(part);
                        freedByKey[d.Key] = freedByKey.GetValueOrDefault(d.Key) + len;
                    }
                    catch { left = true; /* in use or unreadable: a later pass tries again */ }
                }
                if (!left) gone.Add(f);
            }
            if (gone.Count > 0)
                using (store.Gate.Enter())
                    foreach (var f in gone) { f.HasPartial = false; f.PartialBytes = null; f.PartialRootId = null; }
            if (freedByKey.Count > 0)
            {
                // The room just freed is not in this pass's figures: plan once more with it added back, so the
                // files behind the dead partial turn green NOW instead of after some later trigger.
                var roomier = devices.Select(d => freedByKey.TryGetValue(d.Key, out var fb) ? d with { FreeBytes = d.FreeBytes + fb } : d).ToList();
                Mark("dead partials");
                var again = ReplanWholeQueueDetailed(store, roomier, engine, afterRelease: true);
                marks.Append("second pass [").Append(again.Timing).Append("], ");
                return Done(new ReplanResult(changed + again.Changed + 1, again.FreeAfter));
            }
        }

        Mark("dead partials");
        if (engine is null) return Done(new ReplanResult(changed, plan.FreeAfter));

        // Targets change under the engine's own lock (Retarget raises no events, so it is safe here): a task
        // snapshotted as Pending may have gone Active, and Retarget only touches what is still Pending.
        if (retarget.Count > 0) engine.Retarget((_, _) => retarget);
        Mark("retarget");

        // Engine work with the gate RELEASED: each of these raises TaskChanged synchronously into the settler.
        // CancelRange, never a Cancel loop: one event for the batch. A loop is O(N * queue) because every
        // cancel runs a full settle pass and a full queue rebuild -- a hundred files against a thousand-file
        // queue froze the app hard enough to need End Task (owner-hit 09-10, twice).
        if (toDrop.Count > 0) engine.CancelRange(toDrop);
        Mark($"cancel {toDrop.Count}");
        if (toAdd.Count > 0)
        {
            engine.EnqueueRange(toAdd);
            Mark($"enqueue {toAdd.Count}");
            // A file admitted here is appended, so put the engine back in queue order or it runs last. The order
            // is read NOW, not from this pass's snapshot: a re-sort made while the pass ran must not be undone (QA 09-25).
            List<(long, string)> order;
            using (store.Gate.Enter()) order = manifest.Downloads.Snapshot().Select(x => (x.GogId, x.FileKey)).ToList();
            engine.Reorder(order);
            Mark("reorder");
        }
        return Done(new ReplanResult(changed, plan.FreeAfter));
    }

    /// <summary>(09-19) Move the selection's queue entries to the front, keeping the selection's own order. The
    /// persisted order is the single truth every re-plan and the engine's Reorder follow. Caller holds the gate.</summary>
    public static void PromoteToFront(LibraryManifest manifest, IReadOnlyList<(LibraryItem Item, GameFile File)> selection)
    {
        int at = 0;
        foreach (var (item, f) in selection)
            if (manifest.Downloads.Contains(item.GogId, f.FileKey)) manifest.Downloads.MoveTo(item.GogId, f.FileKey, at++);
    }

    public static (List<DownloadTask> Tasks, int Held, long HeldBytes, PlacementPlanner.PlacementPlan Plan) Place(
        LibraryManifest manifest, IBackupLayout layout, IReadOnlyList<DeviceSpace> devices,
        List<(LibraryItem Item, GameFile File)> selection)
    {
        UiThreadGuard.NotOnUi("placing the queue");
        // (09-25) Where each file is headed now, so a file waiting on an unplugged drive stays there.
        List<string?>? current = null;
        if (DriveWaits.Roots.Count > 0)
        {
            var byKey = manifest.Downloads.Snapshot().GroupBy(q => (q.GogId, q.FileKey)).ToDictionary(g => g.Key, g => g.First().TargetRootId);
            current = selection.Select(x => byKey.TryGetValue((x.Item.GogId, x.File.FileKey), out var r) ? r : null).ToList();
        }
        var plan = PlacementPlanner.PlanQueueDetailed(
            selection.Select(x => x.File).ToList(), devices, manifest.Routing, manifest.PrimaryRootId!,
            manifest.Routing.Mode, manifest.ExtrasLayout, current);
        var tasks = new List<DownloadTask>(); int held = 0; long heldBytes = 0;
        for (int i = 0; i < selection.Count; i++)
        {
            var (item, f) = selection[i];
            var p = plan.Placements[i];   // positional: a FileKey can repeat across games (shared extras)
            var rid = p.RootId;
            manifest.Downloads.Enqueue(item.GogId, f.FileKey, rid, p.Fits);
            if (!p.Fits) { held++; heldBytes += PlacementPlanner.StillNeeds(f); continue; }
            tasks.Add(new DownloadTask { File = f, GameTitle = item.Title, GameSlug = item.Slug, TargetRootId = rid,
                                         BytesReceived = f.PartialBytes ?? 0 });   // the resumed amount shows at once
        }
        return (tasks, held, heldBytes, plan);
    }
}

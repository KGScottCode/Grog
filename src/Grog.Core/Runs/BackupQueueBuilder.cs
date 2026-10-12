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

public static class BackupQueueBuilder
{
    /// <remarks>Selection only: nothing is queued or saved. The one mutation is <c>RetryFailed</c>, which
    /// re-admits Corrupt files by clearing their strikes in memory (a dry run shows what a retry would pick).</remarks>
    /// <param name="layout">Answers "is this file's home drive plugged in". Null skips that rule, for the cheap
    /// counts a label needs on every selection change (a real layout scans volumes to construct).</param>
    public static List<(LibraryItem Item, GameFile File)> Build(LibraryManifest manifest, IBackupLayout? layout, BackupRunOptions o)
    {
        if (layout is not null) UiThreadGuard.NotOnUi("building the backup selection");
        var picked = new List<(LibraryItem, GameFile)>();

        if (o.FromPersistedQueue)
        {
            foreach (var q in manifest.Downloads.Snapshot())
            {
                var item = manifest.ItemById(q.GogId);
                var f = item?.Files.FirstOrDefault(x => x.FileKey == q.FileKey);
                if (item is null || f is null) { manifest.Downloads.Remove(q.GogId, q.FileKey); continue; }
                if (BackupScope.IsPresent(f)) { manifest.Downloads.Remove(q.GogId, q.FileKey); continue; }
                if (f.State == FileState.Unavailable) { manifest.Downloads.Remove(q.GogId, q.FileKey); continue; }
                if (layout is not null && f.RootId is not null && !layout.IsOnline(f.RootId)) continue;
                picked.Add((item, f));
            }
            return picked;
        }

        Scope? scope = o.ScopeOverride ?? (o.IgnoreSavedScope ? null : manifest.Scope?.ToScope());

        IEnumerable<(LibraryItem game, GameFile file)> candidates = o.RunScope is { } rs
            ? DownloadFileSelector.Select(manifest.Items, rs)
            : manifest.Items.ToArray().SelectMany(i => i.Files.ToArray().Select(f => (game: i, file: f)));   // snapshots: a UI render may be enumerating

        foreach (var (item, f) in candidates.ToList())
        {
            if (o.OnlyGameIds is not null && !o.OnlyGameIds.Contains(item.GogId)) continue;
            if (scope is { } sc && !sc.Includes(f)) continue;
            if (o.ViewFilter is not null && !o.ViewFilter(item, f)) continue;
            if (layout is not null && f.RootId is not null && !layout.IsOnline(f.RootId)) continue;
            if (o.RetryFailed && f.State == FileState.Corrupt) RetryPolicy.ManualRetry(f);
            bool need = o.UpdatesOnly ? f.State == FileState.Outdated : BackupScope.NeedsFetch(f);
            if (!need) continue;
            picked.Add((item, f));
        }

        if (o.KeepLargest is { } n && n > 0)
            picked = picked.OrderByDescending(x => x.Item2.ExpectedSizeBytes ?? 0).Take(n).ToList();
        return picked;
    }

    public static int Replan(LibraryManifest manifest, IReadOnlyList<DeviceSpace> devices, DownloadEngine engine,
                             out List<DownloadTask> dropped)
    {
        var drop = new List<DownloadTask>();
        int changed = engine.Retarget((pending, active) =>
        {
            var adjusted = ReserveInFlight(devices, active, manifest.PrimaryRootId);
            var files = pending.Select(t => t.File).ToList();
            var plan = PlacementPlanner.PlanQueueDetailed(files, adjusted, manifest.Routing, manifest.PrimaryRootId!,
                                                          manifest.Routing.Mode, manifest.ExtrasLayout,
                                                          pending.Select(t => t.TargetRootId).ToList());
            var answer = new Dictionary<Guid, string?>(pending.Count);
            for (int i = 0; i < pending.Count; i++)
            {
                var p = plan.Placements[i];
                if (!manifest.Downloads.SetPlacement(pending[i].File.GameGogId, pending[i].File.FileKey, p.RootId, p.Fits)) continue;
                if (!p.Fits || string.IsNullOrEmpty(p.RootId)) { drop.Add(pending[i]); continue; }   // keeps its position, waits
                answer[pending[i].Id] = p.RootId;
            }
            return answer;
        });
        dropped = drop;
        return changed;
    }

    internal static List<DeviceSpace> ReserveInFlight(IReadOnlyList<DeviceSpace> devices, IEnumerable<DownloadTask> active, string? primaryRootId)
    {
        var free = devices.ToDictionary(d => d.RootId, d => d.FreeBytes);
        var keyOf = devices.ToDictionary(d => d.RootId, d => d.Key);
        foreach (var a in active)
        {
            var rid = a.TargetRootId ?? primaryRootId ?? "";
            if (!keyOf.TryGetValue(rid, out var key)) continue;
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

    public static int Replan(LibraryManifest manifest, IReadOnlyList<DeviceSpace> devices, DownloadEngine engine)
        => Replan(manifest, devices, engine, out _);

    internal const int MaxTurnAways = 6;

    public static int ReplanWholeQueue(
        IManifestStore store, IReadOnlyList<DeviceSpace> devices, DownloadEngine? engine)
        => ReplanWholeQueueDetailed(store, devices, engine).Changed;

    public sealed record ReplanResult(int Changed, IReadOnlyList<DeviceSpace> FreeAfter)
    {
        public string Timing { get; init; } = "";
        public long TotalMs { get; init; }
    }

    /// <inheritdoc cref="ReplanWholeQueue"/>
    public static ReplanResult ReplanWholeQueueDetailed(
        IManifestStore store, IReadOnlyList<DeviceSpace> devices, DownloadEngine? engine)
    {
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
        var held = new Dictionary<(long, string), DownloadTask>();
        var known = new HashSet<(long, string)>();
        var active = new List<DownloadTask>();
        var turnedAwayCount = new Dictionary<(long, string), int>();
        foreach (var t in engine?.Snapshot ?? Enumerable.Empty<DownloadTask>())
        {
            bool turnedAway = t.State == DownloadTaskState.Failed && t.DiskFull;
            if (turnedAway) turnedAwayCount[(t.File.GameGogId, t.File.FileKey)] = turnedAwayCount.GetValueOrDefault((t.File.GameGogId, t.File.FileKey)) + 1;
            bool driveGone = t.State == DownloadTaskState.Failed && t.DriveGone;
            if (t.State != DownloadTaskState.Canceled && !turnedAway && !driveGone) known.Add((t.File.GameGogId, t.File.FileKey));
            if (t.State == DownloadTaskState.Active) active.Add(t);
            if (t.State is DownloadTaskState.Pending or DownloadTaskState.Active)
                held[(t.File.GameGogId, t.File.FileKey)] = t;
        }
        var capped = turnedAwayCount.Where(kv => kv.Value >= MaxTurnAways).Select(kv => kv.Key).ToHashSet();
        foreach (var k in capped) known.Add(k);

        Mark("engine snapshot");
        var adjusted = ReserveInFlight(devices, active, manifest.PrimaryRootId);
        var snap = new List<QueuedDownload>();
        var cands = new List<(LibraryItem Item, GameFile File, bool WasFit)>();
        var candRoots = new List<string?>();   // each candidate's current target, parallel to cands
        using (store.Gate.Enter())
        {
            snap = manifest.Downloads.Snapshot();
            foreach (var q in snap)
            {
                if (held.TryGetValue((q.GogId, q.FileKey), out var t) && t.State == DownloadTaskState.Active) continue;
                var item = manifest.ItemById(q.GogId);
                var f = item?.Files.FirstOrDefault(x => x.FileKey == q.FileKey);
                if (item is null || f is null) continue;
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

        using (store.Gate.Enter())
        {
            for (int i = 0; i < cands.Count; i++)
            {
                var (item, f, wasFit) = cands[i];
                var p = plan.Placements[i];   // positional: a FileKey can repeat across games (shared extras)
                var key = (f.GameGogId, f.FileKey);
                if (capped.Contains(key)) p = p with { Fits = false };   // out for this run: the flag must say so
                bool has = held.TryGetValue(key, out var task);
                if (!manifest.Downloads.SetPlacement(item.GogId, f.FileKey, p.RootId, p.Fits)) continue;
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
                if (!p.Fits && !has && f.HasPartial) deadPartials.Add((item, f));
                if (changedHere) changed++;
            }
        }
        Mark("decide");
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
                var roomier = devices.Select(d => freedByKey.TryGetValue(d.Key, out var fb) ? d with { FreeBytes = d.FreeBytes + fb } : d).ToList();
                Mark("dead partials");
                var again = ReplanWholeQueueDetailed(store, roomier, engine, afterRelease: true);
                marks.Append("second pass [").Append(again.Timing).Append("], ");
                return Done(new ReplanResult(changed + again.Changed + 1, again.FreeAfter));
            }
        }

        Mark("dead partials");
        if (engine is null) return Done(new ReplanResult(changed, plan.FreeAfter));
        if (retarget.Count > 0) engine.Retarget((_, _) => retarget);

        Mark("retarget");
        if (toDrop.Count > 0) engine.CancelRange(toDrop);

        Mark($"cancel {toDrop.Count}");
        if (toAdd.Count > 0)
        {
            engine.EnqueueRange(toAdd);
            Mark($"enqueue {toAdd.Count}");
            List<(long, string)> order;
            using (store.Gate.Enter()) order = manifest.Downloads.Snapshot().Select(x => (x.GogId, x.FileKey)).ToList();
            engine.Reorder(order);
            Mark("reorder");
        }
        return Done(new ReplanResult(changed, plan.FreeAfter));
    }

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

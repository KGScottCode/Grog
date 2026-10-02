// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Volumes;

/// <summary>(09-19) A .part belongs to its queued file and follows that file's drive. "Move files" moved only
/// finished files, and a Detached drive is not bound by the layout, so the engine's own stray-partial adopt
/// (PreparePartial) never looked there: clear a stick, detach it, and every in-progress download restarted from
/// zero with its .part left behind on a drive Grog no longer writes to. The owner should not have to know that.
///
/// One helper, three steps in the delete's shape: <see cref="Plan"/> (gate held: records), the disk work (NO gate),
/// then the records follow the disk (gate held). Never runs for a file the engine is transferring right now.</summary>
public static class PartialCarry
{
    /// <summary>One in-progress download recorded on the drive being left.</summary>
    public sealed record Item(GameFile File, string Title, string TargetRootId, string? TargetPath, long RecordedBytes);

    public sealed record Result(int Carried, long CarriedBytes, int Left, long LeftBytes, int Cleared)
    {
        public static readonly Result None = new(0, 0, 0, 0, 0);
        public bool Any => Carried + Left + Cleared > 0;
    }

    /// <summary>The queued files whose partial is recorded on <paramref name="fromRootId"/>, each with the drive it
    /// should follow: the root it is routed to, else <paramref name="preferredRootId"/>, else the primary; never
    /// the drive being left. Records only, no disk. Caller holds the gate.</summary>
    public static List<Item> Plan(LibraryManifest m, IBackupLayout layout, string fromRootId, string? preferredRootId)
    {
        var items = new List<Item>();
        if (m.PrimaryRootId is not { } primary) return items;
        var queued = m.Downloads.Snapshot().Select(q => (q.GogId, q.FileKey)).ToHashSet();
        foreach (var item in m.Items)
            foreach (var f in item.Files)
            {
                if (!f.HasPartial || (f.PartialRootId ?? primary) != fromRootId) continue;
                if (!queued.Contains((f.GameGogId, f.FileKey))) continue;   // an unowned .part is the orphan sweep's
                var routed = m.Routing.ResolveRootId(f.GameGogId, f.Kind, primary, m.ExtrasLayout);
                string? target = new[] { routed, preferredRootId, primary }
                    .FirstOrDefault(r => r is not null && r != fromRootId && layout.IsOnline(r));
                target ??= m.Roots.Where(r => r.State != RootState.Detached && r.Id != fromRootId && layout.IsOnline(r.Id)).Select(r => r.Id).FirstOrDefault();
                items.Add(new Item(f, item.Title, target ?? "", target is null ? null : layout.RootPath(target), f.PartialBytes ?? 0L));
            }
        return items;
    }

    /// <summary>What a confirm can say before anything moves: how many in-progress downloads are recorded on the
    /// drive and their bytes. Records only.</summary>
    /// <summary>The queued files whose in-progress download sits on <paramref name="rootId"/>, by key.</summary>
    public static List<(long GogId, string FileKey)> RecordedKeysOn(LibraryManifest m, string rootId)
    {
        if (m.PrimaryRootId is not { } primary) return new();
        var queued = m.Downloads.Snapshot().Select(q => (q.GogId, q.FileKey)).ToHashSet();
        return m.Items.SelectMany(i => i.Files)
                .Where(f => f.HasPartial && (f.PartialRootId ?? primary) == rootId && queued.Contains((f.GameGogId, f.FileKey)))
                .Select(f => (f.GameGogId, f.FileKey)).ToList();
    }

    public static (int Count, long Bytes) RecordedOn(LibraryManifest m, string rootId)
    {
        if (m.PrimaryRootId is not { } primary) return (0, 0);
        var queued = m.Downloads.Snapshot().Select(q => (q.GogId, q.FileKey)).ToHashSet();
        var on = m.Items.SelectMany(i => i.Files)
                  .Where(f => f.HasPartial && (f.PartialRootId ?? primary) == rootId && queued.Contains((f.GameGogId, f.FileKey))).ToList();
        return (on.Count, on.Sum(f => f.PartialBytes ?? 0L));
    }

    /// <summary>The drives a Relocate job CLEARS: every file that holds bytes on the source root is in the job. Each
    /// comes with the root its files are going to (the job's most common target). Caller holds the gate.</summary>
    public static List<(string FromRootId, string ToRootId)> RootsClearedBy(LibraryManifest m, ReorgJob job)
    {
        var cleared = new List<(string, string)>();
        if (m.PrimaryRootId is not { } primary || job.Reason != ReorgReason.Relocate) return cleared;
        var moving = job.Moves.Where(mv => mv.ToRootId is not null).ToList();
        foreach (var g in moving.GroupBy(mv => mv.FromRootId ?? primary))
        {
            var keys = g.Select(mv => (mv.GogId, mv.FileKey)).ToHashSet();
            bool all = m.Items.SelectMany(i => i.Files.Concat(i.OldVersionFiles))
                        .Where(f => Sync.BackupScope.HoldsBytes(f) && (f.RootId ?? primary) == g.Key)
                        .All(f => keys.Contains((f.GameGogId, f.FileKey)));
            if (!all) continue;
            var to = g.GroupBy(mv => mv.ToRootId!).OrderByDescending(x => x.Count()).First().Key;
            if (to != g.Key) cleared.Add((g.Key, to));
        }
        return cleared;
    }

    /// <summary>Carry every in-progress download off <paramref name="fromRootPath"/>. Across volumes the move is a
    /// copy, so a partial goes only where there is room for it beside what is already planned there
    /// (<paramref name="devices"/>, spent as this goes) and only onto a volume that can hold the whole file. What
    /// cannot go stays where it is and is counted: the caller says so. A recorded partial whose .part is not on the
    /// drive has its record cleared (the record follows the disk). Disk work runs with NO gate held.</summary>
    public static Result Carry(IManifestStore store, IBackupLayout layout, string fromRootId, string fromRootPath,
                               string? preferredRootId, IReadOnlyList<DeviceSpace> devices,
                               Func<GameFile, bool>? isTransferring = null, Action<string>? log = null)
    {
        List<Item> plan;
        using (store.Gate.Enter()) plan = Plan(store.Current, layout, fromRootId, preferredRootId);
        if (plan.Count == 0) return Result.None;

        var free = devices.Where(d => d.IsOnline).GroupBy(d => d.Key).ToDictionary(g => g.Key, g => g.Min(d => d.FreeBytes));
        var deviceOf = devices.Where(d => d.IsOnline).GroupBy(d => d.RootId).ToDictionary(g => g.Key, g => g.First());
        var done = new List<(Item Item, long Bytes)>(); var gone = new List<Item>();
        int left = 0; long leftBytes = 0;
        foreach (var it in plan)
        {
            if (isTransferring?.Invoke(it.File) == true) { left++; leftBytes += it.RecordedBytes; continue; }
            string src;
            try
            {
                src = DownloadEngine.PartialPathFor(fromRootPath, it.File);
                if (!Directory.Exists(fromRootPath)) { left++; leftBytes += it.RecordedBytes; continue; }   // unplugged: cannot look
                if (!File.Exists(src)) { gone.Add(it); continue; }
            }
            catch { left++; leftBytes += it.RecordedBytes; continue; }

            long len; try { len = new FileInfo(src).Length; } catch { left++; leftBytes += it.RecordedBytes; continue; }
            if (it.TargetPath is null || !deviceOf.TryGetValue(it.TargetRootId, out var dev)
                || PlacementPlanner.OverCeiling(PlacementPlanner.ToPlace(it.File), dev.MaxFileBytes)
                || free[dev.Key] < dev.Charge(len))
            { left++; leftBytes += len; continue; }
            try
            {
                var dst = DownloadEngine.PartialPathFor(it.TargetPath, it.File);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                // A shorter copy already there (an earlier restart) loses to the longer one: the bytes paid for win.
                if (File.Exists(dst) && new FileInfo(dst).Length >= len) { File.Delete(src); len = new FileInfo(dst).Length; }
                else File.Move(src, dst, overwrite: true);
                free[dev.Key] -= dev.Charge(len);
                done.Add((it, len));
            }
            catch (Exception ex)
            {
                left++; leftBytes += len;
                log?.Invoke($"Could not carry the in-progress download of {it.Title} - {it.File.Name}: {ex.Message}");
            }
        }

        using (store.Gate.Enter())
        {
            foreach (var (it, bytes) in done) { it.File.PartialRootId = it.TargetRootId; it.File.PartialBytes = bytes; it.File.HasPartial = true; }
            foreach (var it in gone) { it.File.HasPartial = false; it.File.PartialBytes = null; it.File.PartialRootId = null; }
        }
        return new Result(done.Count, done.Sum(d => d.Bytes), left, leftBytes, gone.Count);
    }
}

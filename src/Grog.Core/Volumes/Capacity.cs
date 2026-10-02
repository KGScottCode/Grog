// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;

namespace Grog.Core.Volumes;

/// <summary>A candidate device and how much room it has right now. Order matters: pass devices in
/// fill order (default first, then secondary).</summary>
public sealed record DeviceSpace(string RootId, long FreeBytes, bool IsOnline, string? VolumeKey = null)
{
    /// <summary>The ledger a placement charges: two roots on ONE physical volume share it (QA 09-18: each root
    /// reported the volume's whole free space and the planner kept a ledger per root, so 8 GB to A and 8 GB to B
    /// both read Fits on a 10 GB drive and the second transfer died mid-stream). Null = the root is its own volume.</summary>
    public string Key => VolumeKey ?? RootId;

    /// <summary>The largest single file this volume can hold (FAT32: 4 GiB - 1). A file over it fits nowhere on
    /// this device however much room is free; the planner tests a file's WHOLE size against it.</summary>
    public long MaxFileBytes { get; init; } = long.MaxValue;

    /// <summary>The root's folder on disk, when known (production devices). Lets a plan look at the drive itself:
    /// the dead-partial release reads and deletes .part files through it.</summary>
    public string? Path { get; init; }

    /// <summary>The volume's allocation unit (production devices, via <see cref="ForRoot"/>; 0 in arithmetic tests =
    /// bytes cost bytes). A file costs its size rounded up to this (<see cref="Grog.Core.Storage.DriveResolver.OnDisk"/>):
    /// the disk's own arithmetic, so a landing frees nothing the plan did not already know. The flat 256 KB per file it
    /// replaces was given back on landing and let one more small file in after every landing (walk 10-01).</summary>
    public long ClusterBytes { get; init; }
    /// <summary>What <paramref name="bytes"/> of file cost this volume.</summary>
    public long Charge(long bytes) => Grog.Core.Storage.DriveResolver.OnDisk(bytes, ClusterBytes);

    /// <summary>A device keyed by the volume behind <paramref name="path"/> (path arithmetic on Windows, the mount
    /// table on Unix; no capacity probe). Null or unresolvable path: the root is its own volume.</summary>
    public static DeviceSpace ForRoot(string rootId, long freeBytes, bool isOnline, string? path)
    {
        string? key = null;
        if (!string.IsNullOrEmpty(path))
            try { key = Grog.Core.Storage.DriveResolver.MountRootOf(path)?.ToLowerInvariant(); } catch { key = null; }
        long max = long.MaxValue, cluster = 0;
        if (!string.IsNullOrEmpty(path) && isOnline)
        {
            max = Grog.Core.Storage.DriveResolver.MaxFileBytes(path);
            cluster = Grog.Core.Storage.DriveResolver.ClusterBytes(path);
        }
        // A fixed floor per volume, never handed out: directory growth and metadata the per-file rounding does not
        // name. The engine's own room check keeps the same floor (DownloadEngine.FloorBytes), so the two agree.
        long usable = isOnline ? System.Math.Max(0, freeBytes - Grog.Core.Download.DownloadEngine.FloorBytes) : freeBytes;
        return new DeviceSpace(rootId, usable, isOnline, key) { ClusterBytes = cluster, MaxFileBytes = max, Path = string.IsNullOrEmpty(path) ? null : path };
    }
}

/// <summary>The one placement verdict: will the pending work fit, and where. (Drive-card headroom
/// grading -- comfortable vs tight -- is a different axis and lives in StorageReport.Headroom.)</summary>
public enum FitVerdict
{
    FitsOnSelected,    // everything fits on the selected/default device; no spill needed
    NeedsAnotherDrive, // won't fit on the selected device alone, but fits once another is used
    WontFit,           // exceeds the combined free space of all online devices
}

/// <summary>Before a backup starts, answers "will this fit, and where?" so the app can warn up front
/// rather than failing mid-run (design decision: never silently run out of room). Pure -- takes the
/// bytes still needed and device free space, returns a verdict. The caller turns the verdict into a
/// prompt (add/confirm a secondary device) or a hard stop.</summary>
public static class Capacity
{
    public sealed record FitResult(FitVerdict Verdict, long NeededBytes, long SelectedFreeBytes, long TotalFreeBytes)
    {
        public long ShortfallBytes => System.Math.Max(0, NeededBytes - TotalFreeBytes);
    }

    /// <summary>Evaluate whether <paramref name="neededBytes"/> (what still has to be downloaded) fits.</summary>
    public static FitResult Evaluate(long neededBytes, string? selectedRootId, IReadOnlyList<DeviceSpace> devices)
    {
        var online = devices.Where(d => d.IsOnline).ToList();
        long defaultFree = online.FirstOrDefault(d => d.RootId == selectedRootId)?.FreeBytes ?? 0;
        long totalFree = online.Sum(d => d.FreeBytes);

        var verdict = neededBytes <= defaultFree ? FitVerdict.FitsOnSelected
                    : neededBytes <= totalFree ? FitVerdict.NeedsAnotherDrive
                    : FitVerdict.WontFit;
        return new FitResult(verdict, neededBytes, defaultFree, totalFree);
    }
}

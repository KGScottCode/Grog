// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;

namespace Grog.Core.Volumes;

public sealed record DeviceSpace(string RootId, long FreeBytes, bool IsOnline, string? VolumeKey = null)
{
    public string Key => VolumeKey ?? RootId;

    public long MaxFileBytes { get; init; } = long.MaxValue;

    public string? Path { get; init; }

    public long ClusterBytes { get; init; }
    public long Charge(long bytes) => Grog.Core.Storage.DriveResolver.OnDisk(bytes, ClusterBytes);

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
        long usable = isOnline ? System.Math.Max(0, freeBytes - Grog.Core.Download.DownloadEngine.FloorBytes) : freeBytes;
        return new DeviceSpace(rootId, usable, isOnline, key) { ClusterBytes = cluster, MaxFileBytes = max, Path = string.IsNullOrEmpty(path) ? null : path };
    }
}

public enum FitVerdict
{
    FitsOnSelected,    // everything fits on the selected/default device; no spill needed
    NeedsAnotherDrive, // won't fit on the selected device alone, but fits once another is used
    WontFit,           // exceeds the combined free space of all online devices
}

public static class Capacity
{
    public sealed record FitResult(FitVerdict Verdict, long NeededBytes, long SelectedFreeBytes, long TotalFreeBytes)
    {
        public long ShortfallBytes => System.Math.Max(0, NeededBytes - TotalFreeBytes);
    }

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

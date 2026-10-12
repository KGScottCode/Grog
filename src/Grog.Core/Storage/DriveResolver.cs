// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Grog.Core.Storage;

public static class DriveResolver
{
    public static DriveInfo? For(string path)
    {
        try
        {
            if (IsUncPath(path)) return null;   // no drive letter to resolve; ProbeUnc answers for shares
            var full = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows())
            {
                var root = Path.GetPathRoot(full);
                return string.IsNullOrEmpty(root) ? null : new DriveInfo(root);
            }
            // Unix: longest-prefix match against real mounts. GetPathRoot is useless here (always "/").
            UiThreadGuard.NotOnUi("a mount-table scan (DriveInfo.GetDrives)");   // (Windows above is a name only: no I/O)
            DriveInfo? best = null; int bestLen = -1;
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!IsRealVolume(d)) continue;
                var mount = d.RootDirectory.FullName;
                if (!MountContains(full, mount)) continue;
                if (mount.Length > bestLen) { best = d; bestLen = mount.Length; }
            }
            return best;
        }
        catch { return null; }
    }

    public readonly record struct DriveState(bool Online, long Total, long Free, bool CapacityKnown = true);

    public static bool IsUncPath(string? path)
        => path is { Length: > 2 } && path[0] == '\\' && path[1] == '\\';

    internal static DriveState ProbeUnc(string path)
    {
        bool online;
        try { online = Directory.Exists(path); } catch { online = false; }
        if (!online) return default;
        try
        {
            var di = new DriveInfo(path);
            return new DriveState(true, di.TotalSize, di.AvailableFreeSpace);
        }
        catch { return new DriveState(true, 0, 0, CapacityKnown: false); }
    }

    private sealed class ProbeEntry { public long StampMs; public DriveState State; public int Refreshing; }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProbeEntry> _probeCache
        = new(StringComparer.OrdinalIgnoreCase);
    private const long ProbeTtlMs = 1500;

    public static DriveState Probe(string path, bool fresh = false)
    {
        string key;
        try { key = Path.GetFullPath(path); } catch { key = path ?? ""; }
        long now = Environment.TickCount64;
        if (fresh || !_probeCache.TryGetValue(key, out var e))
        {
            var state = ProbeUncached(key);
            _probeCache[key] = new ProbeEntry { StampMs = now, State = state };
            return state;
        }
        if (now - e.StampMs >= ProbeTtlMs && System.Threading.Interlocked.Exchange(ref e.Refreshing, 1) == 0)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try { e.State = ProbeUncached(key); }
                finally { e.StampMs = Environment.TickCount64; System.Threading.Volatile.Write(ref e.Refreshing, 0); }
            });
        }
        return e.State;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _busySince = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _busyDepth = new(StringComparer.OrdinalIgnoreCase);

    public static string VolumeLabel(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows()) return (Path.GetPathRoot(full) ?? full).TrimEnd('\\', '/');
            return full;
        }
        catch { return path ?? ""; }
    }

    private readonly struct BusyScope : IDisposable
    {
        private readonly string _v;
        public BusyScope(string v)
        {
            _v = v;
            if (_busyDepth.AddOrUpdate(v, 1, (_, n) => n + 1) == 1) _busySince[v] = Environment.TickCount64;
        }
        public void Dispose()
        {
            if (_busyDepth.AddOrUpdate(_v, 0, (_, n) => Math.Max(0, n - 1)) == 0) _busySince.TryRemove(_v, out _);
        }
    }
    private static BusyScope Busy(string path) => new(VolumeLabel(path));
    public static IDisposable HoldBusyForShots(string path) => Busy(path);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _maxFilePending = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<(string Volume, long Ms)> SlowVolumes(long thresholdMs)
    {
        long now = Environment.TickCount64;
        var list = new List<(string, long)>();
        foreach (var kv in _busySince) if (now - kv.Value >= thresholdMs) list.Add((kv.Key, now - kv.Value));
        list.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return list;
    }

    private static DriveState ProbeUncached(string path)
    {
        using var _ = Busy(path);
        if (IsUncPath(path)) return ProbeUnc(path);
        try
        {
            var di = For(path);
            if (di is null || !di.IsReady) return default;   // Online=false
            if (!OperatingSystem.IsWindows() && !Directory.Exists(Path.GetFullPath(path))) return default;
            long total = 0, free = 0;
            try { total = di.TotalSize; free = di.AvailableFreeSpace; } catch { /* readable-but-metadata-failed */ }
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    var direct = new DriveInfo(Path.GetFullPath(path));
                    if (direct.TotalSize > 0) { total = direct.TotalSize; free = direct.AvailableFreeSpace; }
                }
                catch { /* keep the mount-matched numbers */ }
            }
            return new DriveState(true, total, free);
        }
        catch { return default; }
    }

    public static bool IsOnline(string path) => Probe(path).Online;

    public static (long Total, long Free)? Capacity(string path)
    {
        var s = Probe(path);
        return s.Online && s.CapacityKnown ? (s.Total, s.Free) : null;
    }

    public static long? FreeBytes(string path) => Capacity(path)?.Free;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Root, long At)> _mountRootCache
        = new(StringComparer.Ordinal);
    public static string? MountRootCached(string path)
    {
        if (OperatingSystem.IsWindows()) return MountRootOf(path);
        long now = Environment.TickCount64;
        if (_mountRootCache.TryGetValue(path, out var hit))
        {
            if (now - hit.At >= 60_000)
            {
                _mountRootCache[path] = (hit.Root, now);
                _ = System.Threading.Tasks.Task.Run(() => _mountRootCache[path] = (MountRootOf(path), Environment.TickCount64));
            }
            return hit.Root;
        }
        if (UiThreadGuard.IsUiThread is { } ui && ui())
        {
            _mountRootCache.TryAdd(path, (null, now - 55_000));   // one background fill, retried in 5 s if it lost the race
            _ = System.Threading.Tasks.Task.Run(() => _mountRootCache[path] = (MountRootOf(path), Environment.TickCount64));
            return null;
        }
        var r = MountRootOf(path);
        _mountRootCache[path] = (r, now);
        return r;
    }

    public static string? MountRootOf(string path) => For(path)?.RootDirectory.FullName;

    public static long MaxFileBytes(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows() && UiThreadGuard.IsUiThread?.Invoke() == true)
            {
                if (MountRootCached(path) is { } uiRoot)
                    lock (_maxFileCache) if (_maxFileCache.TryGetValue(uiRoot, out var uiHit)) return uiHit.Max;
                if (_maxFilePending.TryAdd(path, 0))
                    _ = System.Threading.Tasks.Task.Run(() => { try { MaxFileBytes(path); } finally { _maxFilePending.TryRemove(path, out _); } });
                return long.MaxValue;
            }
            var d = For(path);
            if (d is null) return long.MaxValue;
            var key = d.RootDirectory.FullName;
            long now = Environment.TickCount64;
            lock (_maxFileCache)
                if (_maxFileCache.TryGetValue(key, out var hit))
                {
                    if (now - hit.At >= 60_000)
                    {
                        _maxFileCache[key] = (hit.Max, now);
                        _ = System.Threading.Tasks.Task.Run(() =>
                        {
                            try { long m; using (Busy(path)) m = d.IsReady ? MaxFileBytesFor(d.DriveFormat) : long.MaxValue; lock (_maxFileCache) _maxFileCache[key] = (m, Environment.TickCount64); }
                            catch { /* keep the last answer */ }
                        });
                    }
                    return hit.Max;
                }
            if (UiThreadGuard.IsUiThread?.Invoke() == true)
            {
                if (_maxFilePending.TryAdd(key, 0))
                    _ = System.Threading.Tasks.Task.Run(() => { try { MaxFileBytes(path); } finally { _maxFilePending.TryRemove(key, out _); } });
                return long.MaxValue;
            }
            long max;
            using (Busy(path)) max = d.IsReady ? MaxFileBytesFor(d.DriveFormat) : long.MaxValue;
            lock (_maxFileCache) _maxFileCache[key] = (max, now);
            return max;
        }
        catch { return long.MaxValue; }
    }
    private static readonly Dictionary<string, (long Max, long At)> _maxFileCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, long> _clusterCache = new(StringComparer.OrdinalIgnoreCase);
    public static long ClusterBytes(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows() && UiThreadGuard.IsUiThread?.Invoke() == true)
            {
                if (MountRootCached(path) is { } uiRoot)
                    lock (_clusterCache) if (_clusterCache.TryGetValue(uiRoot, out var uiHit)) return uiHit;
                _ = System.Threading.Tasks.Task.Run(() => { try { ClusterBytes(path); } catch { } });
                return DefaultClusterBytes;
            }
            var d = For(path);
            if (d is null) return DefaultClusterBytes;
            var key = d.RootDirectory.FullName;
            lock (_clusterCache) if (_clusterCache.TryGetValue(key, out var hit)) return hit;
            if (UiThreadGuard.IsUiThread?.Invoke() == true)
            {
                _ = System.Threading.Tasks.Task.Run(() => { try { ClusterBytes(path); } catch { } });
                return DefaultClusterBytes;
            }
            long c;
            using (Busy(path)) c = ProbeClusterBytes(d) ?? ClusterBytesFor(d.IsReady ? d.DriveFormat : null);
            lock (_clusterCache) _clusterCache[key] = c;
            return c;
        }
        catch { return DefaultClusterBytes; }
    }
    public const long DefaultClusterBytes = 64 * 1024;
    public static long ClusterBytesFor(string? driveFormat) => (driveFormat ?? "").Trim().ToLowerInvariant() switch
    {
        "fat32" or "fat" or "fat16" or "vfat" or "msdos" => 32 * 1024,
        "exfat" => 128 * 1024,
        "ntfs" or "refs" or "ext4" or "ext3" or "ext2" or "xfs" or "btrfs" or "apfs" or "hfs+" or "hfs" => 4 * 1024,
        _ => DefaultClusterBytes,
    };
    public static long OnDisk(long bytes, long clusterBytes)
        => clusterBytes <= 1 || bytes <= 0 ? Math.Max(0, bytes) : (bytes + clusterBytes - 1) / clusterBytes * clusterBytes;

    private static long? ProbeClusterBytes(DriveInfo d)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var root = d.RootDirectory.FullName;
                if (!root.EndsWith('\\')) root += '\\';
                return GetDiskFreeSpaceW(root, out uint spc, out uint bps, out _, out _) && spc > 0 && bps > 0 ? (long)spc * bps : null;
            }
            var buf = new byte[512];   // struct statvfs is well under this on glibc and macOS
            if (statvfs(d.RootDirectory.FullName, buf) != 0) return null;
            long frsize = BitConverter.ToInt64(buf, 8);
            long bsize = BitConverter.ToInt64(buf, 0);
            long unit = frsize > 0 ? frsize : bsize;
            return unit is > 0 and <= (4 << 20) ? unit : null;
        }
        catch { return null; }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceW(string lpRootPathName, out uint lpSectorsPerCluster, out uint lpBytesPerSector, out uint lpNumberOfFreeClusters, out uint lpTotalNumberOfClusters);
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int statvfs(string path, byte[] buf);

    public static long MaxFileBytesFor(string? driveFormat) => (driveFormat ?? "").Trim().ToLowerInvariant() switch
    {
        "fat32" or "fat" or "fat16" or "vfat" or "msdos" => 4294967295L,
        _ => long.MaxValue,
    };

    public static string FileLimitName(long maxFileBytes) => maxFileBytes == 4294967295L ? "FAT32" : Grog.Core.Format.ByteFormat.Size(maxFileBytes) + " per file";

    public static bool IsRealVolume(DriveInfo d)
    {
        try
        {
            if (!d.IsReady) return false;
            return !PseudoFormats.Contains(d.DriveFormat);
        }
        catch { return false; }
    }

    private static bool MountContains(string full, string mount)
    {
        if (mount == "/") return true;
        if (!full.StartsWith(mount, StringComparison.Ordinal)) return false;
        return full.Length == mount.Length
               || full[mount.Length] == '/'
               || full[mount.Length] == Path.DirectorySeparatorChar;
    }

    // Linux/macOS pseudo-filesystem formats to skip when enumerating "real" drives.
    private static readonly HashSet<string> PseudoFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "proc", "sysfs", "tmpfs", "devtmpfs", "devpts", "cgroup", "cgroup2", "overlay", "squashfs",
        "autofs", "mqueue", "hugetlbfs", "debugfs", "tracefs", "securityfs", "pstore", "bpf", "configfs",
        "fusectl", "fuse.gvfsd-fuse", "fuse.portal", "ramfs", "efivarfs", "binfmt_misc", "nsfs", "selinuxfs",
        "devfs",
    };
}

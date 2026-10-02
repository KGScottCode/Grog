// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Grog.Core.Storage;

/// <summary>Resolves the physical volume backing a filesystem path on every OS. On Unix GetPathRoot always
/// returns "/", so the path is matched against the longest mount point among real block-device volumes --
/// otherwise an unplugged removable drive would read "online" with the system disk's free space.</summary>
public static class DriveResolver
{
    /// <summary>The DriveInfo whose volume contains <paramref name="path"/>, or null if it can't be resolved.</summary>
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

    /// <summary>Presence + capacity of the volume backing a path, from ONE filesystem probe. CapacityKnown is false
    /// when the volume answered "present" but no size figures could be read (a UNC share): Total/Free are then 0
    /// and <see cref="Capacity"/> reports null, the same "unknown" every caller already handles.</summary>
    public readonly record struct DriveState(bool Online, long Total, long Free, bool CapacityKnown = true);

    /// <summary>A Windows network share path (\\server\share\...). DriveInfo has no drive letter to build from
    /// and throws on it; the share is probed by its folder instead.</summary>
    public static bool IsUncPath(string? path)
        => path is { Length: > 2 } && path[0] == '\\' && path[1] == '\\';

    /// <summary>The probe for a UNC share: online when the folder answers, sizes only if DriveInfo accepts the path.</summary>
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

    // Drive metadata calls (IsReady / TotalSize / AvailableFreeSpace) can stall on a spun-down external or
    // network mount, and the UI probes in bursts (Folders refresh, nav, 2s poll). A short TTL cache collapses
    // each burst to one real probe per path, and Probe() does a single DriveInfo lookup.
    private sealed class ProbeEntry { public long StampMs; public DriveState State; public int Refreshing; }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProbeEntry> _probeCache
        = new(StringComparer.OrdinalIgnoreCase);
    private const long ProbeTtlMs = 1500;

    /// <summary>Presence + capacity for a path. NEVER blocks a caller that already has a value: the first call
    /// for a path probes synchronously (startup, drive idle); after that a stale entry is returned as-is and
    /// refreshed on a worker. GetDiskFreeSpaceEx on a removable volume with write-through, while the engine is
    /// writing to it, can block for seconds - on the UI thread that froze the whole window every few seconds
    /// (owner-hit 09-04). Pass <paramref name="fresh"/> = true to force a synchronous probe.</summary>
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

    // (09-25, owner) "Waiting for H: to respond": every real call into a volume's metadata is registered here while it
    // runs, so the App can say which drive is slow to answer instead of freezing or spinning in silence.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _busySince = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _busyDepth = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The name a person knows the volume by: "H:" on Windows, the mount point elsewhere.</summary>
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
    /// <summary>Hold a volume "busy" as if a probe were stuck on it: the waiting line's render check (HeadlessShots).</summary>
    public static IDisposable HoldBusyForShots(string path) => Busy(path);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _maxFilePending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Volumes with a metadata call in flight for at least <paramref name="thresholdMs"/>, with how long.</summary>
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
            // Unix: when a removable mount vanishes, For() falls back to "/" (always ready). Requiring the
            // backup path itself to exist makes an unplugged drive correctly read offline, while a folder on
            // the always-mounted system/home volume stays online. Windows: volume-present is enough.
            if (!OperatingSystem.IsWindows() && !Directory.Exists(Path.GetFullPath(path))) return default;
            long total = 0, free = 0;
            try { total = di.TotalSize; free = di.AvailableFreeSpace; } catch { /* readable-but-metadata-failed */ }
            if (!OperatingSystem.IsWindows())
            {
                // macOS (and any firmlinked/bind-mounted layout): the longest-prefix mount match above can
                // land on the WRONG volume - /Users/... reaches the data volume through a firmlink, so its
                // literal prefix matches "/" (the SEALED READ-ONLY system snapshot) and the free figure
                // freezes forever (owner chased this for an evening, 2026-08-29; dotnet/runtime#28902).
                // statfs the actual path instead: the Unix DriveInfo(name) ctor stats the path it is given,
                // which follows the firmlink to the volume the bytes really land on.
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

    /// <summary>Is the volume backing <paramref name="path"/> present and reachable right now? A removable
    /// backup drive reads offline once unplugged.</summary>
    public static bool IsOnline(string path) => Probe(path).Online;

    /// <summary>Total + free bytes of the volume backing <paramref name="path"/>, or null if unresolved/offline.</summary>
    public static (long Total, long Free)? Capacity(string path)
    {
        var s = Probe(path);
        return s.Online && s.CapacityKnown ? (s.Total, s.Free) : null;
    }

    /// <summary>Free bytes of the volume backing <paramref name="path"/>, or null.</summary>
    public static long? FreeBytes(string path) => Capacity(path)?.Free;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Root, long At)> _mountRootCache
        = new(StringComparer.Ordinal);
    /// <summary>(Mac walk 09-25: "UI thread ran a mount-table scan") <see cref="MountRootOf"/> for UI-thread callers: the
    /// last answer for this path, refreshed on a worker after a minute. The first call for a path returns null (the
    /// caller's own-volume fallback) and fills the cache in the background. Windows computes it inline (a name only).</summary>
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

    /// <summary>The mount point (volume root) containing <paramref name="path"/> -- "C:\" on Windows, "/" or
    /// "/media/user/DISK" on Unix. Use this to tell whether two paths sit on the SAME physical volume;
    /// Path.GetPathRoot can't, because on Unix it returns "/" for everything.</summary>
    public static string? MountRootOf(string path) => For(path)?.RootDirectory.FullName;

    /// <summary>The largest single file the volume behind <paramref name="path"/> can hold, or long.MaxValue when
    /// it has no practical limit. FAT32 stops at 4 GiB - 1 and Windows reports the overrun as "not enough space on
    /// the disk": a 6.1 GB installer died at byte 4,294,981,975 on a FAT32 stick the planner said it fit (walk 09-19).</summary>
    public static long MaxFileBytes(string path)
    {
        try
        {
            var d = For(path);
            if (d is null) return long.MaxValue;
            // Cached per volume for a minute: planners ask on every pass (1 Hz from the Storage page during a run),
            // and IsReady/DriveFormat on a busy USB stick is the stall Probe's own cache exists to avoid.
            var key = d.Name;
            long now = Environment.TickCount64;
            lock (_maxFileCache)
                if (_maxFileCache.TryGetValue(key, out var hit))
                {
                    // Stale-while-revalidate: a known volume NEVER blocks the caller again (the Storage refresh asks
                    // from the UI thread at 1 Hz mid-run, and DriveFormat on a stick being written to can take
                    // seconds). A re-format under the same letter is picked up within a minute, in the background.
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
            // (09-25) The first ask for a volume from the UI thread no longer waits for the drive: the answer is fetched
            // in the background and "no known limit" stands in until it lands. Every planner asks off the UI thread,
            // where this still waits for the real answer, so placement never runs on the stand-in.
            if (UiThreadGuard.IsUiThread?.Invoke() == true)
            {
                // No placeholder in the cache: a planner asking meanwhile must wait for the real answer, never read this.
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

    /// <summary>The per-file ceiling for a filesystem name as DriveInfo reports it (Windows "FAT32"/"FAT", Unix "vfat"/"msdos").</summary>
    // ---- cluster size (1288): what one file really costs the disk is its size rounded up to the allocation unit. The
    // plan used to add a flat 256 KB per file instead, and gave it back on landing, so the end-of-run re-check found
    // room for one more small file after every landing (walk 10-01: 23 planned, 34 landed, one at a time).
    private static readonly Dictionary<string, long> _clusterCache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The volume's allocation unit in bytes, asked of the filesystem (Windows GetDiskFreeSpace, POSIX statvfs),
    /// else a conservative figure for the format. Cached per volume for the process; the first ask from the UI thread
    /// returns the format's figure and fills the cache in the background, like <see cref="MaxFileBytes"/>.</summary>
    public static long ClusterBytes(string path)
    {
        try
        {
            var d = For(path);
            if (d is null) return DefaultClusterBytes;
            var key = d.Name;
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
    /// <summary>Stand-in when the filesystem cannot be asked: the largest common unit, so the plan never under-charges.</summary>
    public const long DefaultClusterBytes = 64 * 1024;
    /// <summary>By format, when the filesystem cannot be asked: FAT32 and exFAT run large units on removable media.</summary>
    public static long ClusterBytesFor(string? driveFormat) => (driveFormat ?? "").Trim().ToLowerInvariant() switch
    {
        "fat32" or "fat" or "fat16" or "vfat" or "msdos" => 32 * 1024,
        "exfat" => 128 * 1024,
        "ntfs" or "refs" or "ext4" or "ext3" or "ext2" or "xfs" or "btrfs" or "apfs" or "hfs+" or "hfs" => 4 * 1024,
        _ => DefaultClusterBytes,
    };
    /// <summary>What <paramref name="bytes"/> occupies on a volume with <paramref name="clusterBytes"/> units.</summary>
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
            // f_bsize (ulong) at 0, f_frsize (ulong) at 8 on glibc x64 and macOS/arm64 alike (both unsigned long).
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

    /// <summary>The name a per-file ceiling is known by ("FAT32" for 4 GiB - 1), for the queue row's wording.</summary>
    public static string FileLimitName(long maxFileBytes) => maxFileBytes == 4294967295L ? "FAT32" : Grog.Core.Format.ByteFormat.Size(maxFileBytes) + " per file";

    /// <summary>A real, mountable block-device volume (not a pseudo-filesystem like proc/sys/tmpfs/cgroup).
    /// DriveType is unreliable on Linux (e.g. /boot/efi reports Removable), so we discriminate by the
    /// filesystem format instead.</summary>
    public static bool IsRealVolume(DriveInfo d)
    {
        try
        {
            if (!d.IsReady) return false;
            return !PseudoFormats.Contains(d.DriveFormat);
        }
        catch { return false; }
    }

    // True when `full` is inside the `mount` subtree (the mount itself, or a child separated by a slash).
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

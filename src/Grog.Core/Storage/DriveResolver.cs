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

    public readonly record struct DriveState(bool Online, long Total, long Free, bool CapacityKnown = true, bool Network = false);

    private static readonly char[] Seps = { '\\', '/' };
    // "\\server\share" for a UNC path, null otherwise. The volume identity a share has instead of a drive letter.
    public static string? UncRoot(string? path)
    {
        if (!IsUncPath(path)) return null;
        var p = path!;
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];   // long form -> plain
        int i = p.IndexOfAny(Seps, 2);
        if (i < 0) return null;
        int j = p.IndexOfAny(Seps, i + 1);
        return j < 0 ? p : p[..j];
    }

    public static bool IsUncPath(string? path)
        => path is { Length: > 2 } && path[0] == '\\' && path[1] == '\\';

    internal static DriveState ProbeUnc(string path)
    {
        bool online;
        try { online = Directory.Exists(path); } catch { online = false; }
        if (!online) return default;
        // DriveInfo wants a drive letter on Windows and throws on a share (issue #3: a NAS root read as 0 B free
        // and nothing fit). GetDiskFreeSpaceEx takes any directory, shares included.
        if (OperatingSystem.IsWindows() && DirectoryCapacity(path) is { } cap)
            return new DriveState(true, cap.Total, cap.Free, Network: true);
        try
        {
            var di = new DriveInfo(path);
            return new DriveState(true, di.TotalSize, di.AvailableFreeSpace, Network: true);
        }
        catch { return new DriveState(true, 0, 0, CapacityKnown: false, Network: true); }
    }

    // Total and free bytes of the volume holding a directory, by path rather than drive letter. Windows only; null when
    // the call fails (share gone, access denied, not Windows).
    internal static (long Total, long Free)? DirectoryCapacity(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var dir = path.EndsWith('\\') || path.EndsWith('/') ? path : path + '\\';
            return GetDiskFreeSpaceExW(dir, out ulong free, out ulong total, out _) && total > 0
                ? ((long)Math.Min(total, long.MaxValue), (long)Math.Min(free, long.MaxValue))
                : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceExW(string lpDirectoryName, out ulong lpFreeBytesAvailableToCaller, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

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
            return new DriveState(true, total, free, Network: IsNetworkVolume(di));
        }
        catch { return default; }
    }

    // A mapped letter (Windows) or a cifs/nfs/sshfs mount (Unix) reaches its bytes over the network like a share does.
    private static readonly HashSet<string> NetworkFormats = new(StringComparer.OrdinalIgnoreCase)
        { "cifs", "smbfs", "smb3", "nfs", "nfs4", "afpfs", "fuse.sshfs", "sshfs", "webdav", "davfs" };
    private static bool IsNetworkVolume(DriveInfo d)
    {
        try
        {
            if (d.DriveType == DriveType.Network) return true;
            return !OperatingSystem.IsWindows() && d.IsReady && NetworkFormats.Contains(d.DriveFormat);
        }
        catch { return false; }   // metadata refused: not worth a label
    }

    // Answered from the probe cache only, so it is safe on the UI thread; a UNC path needs no probe to be network.
    public static bool IsNetworkCached(string path)
    {
        if (IsUncPath(path)) return true;
        string key;
        try { key = Path.GetFullPath(path); } catch { return false; }
        return _probeCache.TryGetValue(key, out var e) && e.State.Network;
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

    public static string? MountRootOf(string path) => UncRoot(path) ?? For(path)?.RootDirectory.FullName;

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
            if (UncRoot(path) is { } share)
            {
                lock (_maxFileCache) if (_maxFileCache.TryGetValue(share, out var shareHit)) return shareHit.Max;
                if (UiThreadGuard.IsUiThread?.Invoke() == true)
                {
                    if (_maxFilePending.TryAdd(share, 0))
                        _ = System.Threading.Tasks.Task.Run(() => { try { MaxFileBytes(path); } finally { _maxFilePending.TryRemove(share, out _); } });
                    return long.MaxValue;
                }
                var fmt = ShareFormat(share);   // null while the share is unreachable: not cached, so a later call asks again
                long shareMax = MaxFileBytesFor(fmt);
                if (fmt is not null) lock (_maxFileCache) _maxFileCache[share] = (shareMax, Environment.TickCount64);
                return shareMax;
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
            if (UncRoot(path) is { } share)
            {
                lock (_clusterCache) if (_clusterCache.TryGetValue(share, out var shareHit)) return shareHit;
                if (UiThreadGuard.IsUiThread?.Invoke() == true)
                {
                    _ = System.Threading.Tasks.Task.Run(() => { try { ClusterBytes(path); } catch { } });
                    return DefaultClusterBytes;
                }
                long sc;
                using (Busy(path)) sc = ProbeClusterBytesAt(share + '\\') ?? ClusterBytesFor(ShareFormat(share));
                lock (_clusterCache) _clusterCache[share] = sc;
                return sc;
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

    // Windows only: the cluster size of the volume at a root path, drive letter or share; null elsewhere or on failure.
    private static long? ProbeClusterBytesAt(string root)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return GetDiskFreeSpaceW(root, out uint spc, out uint bps, out _, out _) && spc > 0 && bps > 0 ? (long)spc * bps : null; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    // Windows only: the filesystem name ("NTFS", "FAT32") of a share root; null elsewhere or when the share will not say.
    private static string? ShareFormat(string share)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var fs = new System.Text.StringBuilder(64);
            return GetVolumeInformationW(share + '\\', null, 0, out _, out _, out _, fs, fs.Capacity) ? fs.ToString() : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(string lpRootPathName, System.Text.StringBuilder? lpVolumeNameBuffer, int nVolumeNameSize,
        out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength, out uint lpFileSystemFlags, System.Text.StringBuilder lpFileSystemNameBuffer, int nFileSystemNameSize);

    private static long? ProbeClusterBytes(DriveInfo d)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var root = d.RootDirectory.FullName;
                if (!root.EndsWith('\\')) root += '\\';
                return ProbeClusterBytesAt(root);
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

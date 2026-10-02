// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Storage;

/// <summary>Computes the dashboard's storage picture: per-drive free space, Grog's share of it, and a
/// color-coded headroom judgment (green comfortable / yellow tight / red won't fit). The same math the
/// onboarding fit-check and pre-sync warning use.</summary>
public static class StorageReport
{
    public enum Headroom { Comfortable, Tight, WontFit, Unknown }

    public sealed record DriveUsage(
        string RootId, string Path, long TotalBytes, long FreeBytes, long GrogBytes, bool IsOnline);

    /// <summary>Per-backup-root usage. Offline roots are reported as offline (not zero/full).</summary>
    public static IReadOnlyList<DriveUsage> DrivesFor(LibraryManifest manifest, IEnumerable<(string RootId, string Path)> roots)
    {
        var list = new List<DriveUsage>();
        foreach (var (rootId, path) in roots)
        {
            // Free space and the .grog-tmp size are read in ONE background pass so the pair is from the same
            // instant: sampled separately, a fast write made "used minus Grog" flicker by a few hundred MB
            // (owner-caught 09-04). The volume probe stays the cached DriveResolver one for everyone else.
            var sample = DriveSample(path);
            bool online = sample.Online; long total = sample.Total, free = sample.Free;

            // "Backed up" = present on disk per BackupScope.IsPresent (Downloaded, Verified, UpdateAvailable),
            // never a hand-rolled ==Downloaded test.
            long grog = manifest.Items
                .SelectMany(i => i.Files)
                .Where(f => manifest.EffectiveRootId(f) == rootId && Sync.BackupScope.IsPresent(f))
                .Sum(f => Grog.Core.Sync.Rollups.HeldBytesOf(f));
            // Plus what the engine holds in <root>/.grog-tmp, MEASURED: every .part there is Grog's data on this
            // volume whatever its current target (a Pause/Resume re-plan can move a file's target to another
            // device and leave its old .part behind), so a modelled partial figure can drift from the drive
            // while the directory cannot (owner-caught 09-04: "Other data" on a drive holding only Grog).
            grog += sample.PartialBytes;

            list.Add(new DriveUsage(rootId, path, total, free, grog, online));
        }
        return list;
    }

    public readonly record struct DriveSampleResult(bool Online, long Total, long Free, long PartialBytes);

    /// <summary>Volume free/total AND the bytes under <root>/.grog-tmp, measured together on a worker and cached
    /// for a second. Never touches the disk on the caller's thread (a saturated USB stick can block a stat for
    /// seconds); the first call for a path seeds from the cached volume probe with 0 partial bytes.</summary>
    public static DriveSampleResult DriveSample(string rootPath)
    {
        long now = Environment.TickCount64;
        var cur = _samples.GetOrAdd(rootPath, _ =>
        {
            var p = DriveResolver.Probe(rootPath);
            return new Sample { Value = new DriveSampleResult(p.Online, p.Total, p.Free, 0), MeasuredAt = long.MinValue / 2 };
        });
        if (now - cur.MeasuredAt >= 1000 && System.Threading.Interlocked.Exchange(ref cur.Refreshing, 1) == 0)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var p = DriveResolver.Probe(rootPath, fresh: true);
                    cur.Value = new DriveSampleResult(p.Online, p.Total, p.Free, MeasurePartials(rootPath));
                }
                finally { cur.MeasuredAt = Environment.TickCount64; System.Threading.Volatile.Write(ref cur.Refreshing, 0); }
            });
        }
        return cur.Value;
    }

    /// <summary>(09-15) Measure NOW, on the caller's thread, and replace the cached sample. For the drive-presence
    /// watch, which runs on a worker: when a drive comes back, the card rebuild that follows must not read the
    /// sample taken while it was unplugged (measured: a re-plugged USB stick read "Offline" until something
    /// else happened to refresh the cards; the presence probe and this sample are two caches).</summary>
    public static void RefreshSampleNow(string rootPath)
    {
        var p = DriveResolver.Probe(rootPath, fresh: true);
        var fresh = new Sample { Value = new DriveSampleResult(p.Online, p.Total, p.Free, MeasurePartials(rootPath)), MeasuredAt = Environment.TickCount64 };
        _samples[rootPath] = fresh;
    }

    /// <summary>Bytes of in-flight / abandoned partials under this root's .grog-tmp, from the same cached sample.</summary>
    public static long PartialBytesOnDisk(string rootPath) => DriveSample(rootPath).PartialBytes;

    private sealed class Sample { public DriveSampleResult Value; public long MeasuredAt; public int Refreshing; }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Sample> _samples = new(StringComparer.OrdinalIgnoreCase);

    private static long MeasurePartials(string rootPath)
    {
        try
        {
            var dir = Path.Combine(rootPath, ".grog-tmp");
            if (!Directory.Exists(dir)) return 0;
            long sum = 0;
            foreach (var f in Directory.EnumerateFiles(dir)) { try { sum += new FileInfo(f).Length; } catch { } }
            return sum;
        }
        catch { return 0; }
    }

    /// <summary>Bytes still needed to complete the backup (not-yet-downloaded / outdated files) within the
    /// FULL scope (games/extras + language + platform) -- so the drive-fit color and the "needs ~X GB"
    /// headline match what will actually queue, not an inflated all-platform/all-language total.</summary>
    public static long PendingBytes(LibraryManifest manifest, Sync.Scope scope)
        => manifest.Items.SelectMany(i => i.Files)
            .Where(Sync.BackupScope.NeedsFetch)
            .Where(f => scope.Includes(f))
            .Sum(f => f.ExpectedSizeBytes ?? 0);

    /// <summary>Judge whether <paramref name="neededBytes"/> fits in <paramref name="freeBytes"/>:
    /// green if it leaves comfortable headroom (&gt;20% free after), yellow if it fits but tight,
    /// red if it won't fit at all.</summary>
    public static Headroom Judge(long neededBytes, long freeBytes)
    {
        if (freeBytes <= 0) return Headroom.Unknown;
        if (neededBytes <= 0) return Headroom.Comfortable;
        if (neededBytes > freeBytes) return Headroom.WontFit;
        double afterFreeRatio = (double)(freeBytes - neededBytes) / freeBytes;
        return afterFreeRatio >= 0.20 ? Headroom.Comfortable : Headroom.Tight;
    }

    public static string HeadroomColorHex(Headroom fit) => fit switch
    {
        Headroom.Comfortable => "#6FAE72",   // green
        Headroom.Tight       => "#D9A441",   // amber
        Headroom.WontFit     => "#C9605C",   // red
        _               => "#9AA4B0",   // neutral
    };
}

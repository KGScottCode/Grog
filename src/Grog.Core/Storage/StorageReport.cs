// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Storage;

public static class StorageReport
{
    public enum Headroom { Comfortable, Tight, WontFit, Unknown }

    public sealed record DriveUsage(
        string RootId, string Path, long TotalBytes, long FreeBytes, long GrogBytes, bool IsOnline);

    public static IReadOnlyList<DriveUsage> DrivesFor(LibraryManifest manifest, IEnumerable<(string RootId, string Path)> roots)
    {
        var list = new List<DriveUsage>();
        foreach (var (rootId, path) in roots)
        {
            var sample = DriveSample(path);
            bool online = sample.Online; long total = sample.Total, free = sample.Free;
            long grog = manifest.Items
                .SelectMany(i => i.Files)
                .Where(f => manifest.EffectiveRootId(f) == rootId && Sync.BackupScope.IsPresent(f))
                .Sum(f => Grog.Core.Sync.Rollups.HeldBytesOf(f));
            grog += sample.PartialBytes;

            list.Add(new DriveUsage(rootId, path, total, free, grog, online));
        }
        return list;
    }

    public readonly record struct DriveSampleResult(bool Online, long Total, long Free, long PartialBytes);

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

    public static void RefreshSampleNow(string rootPath)
    {
        var p = DriveResolver.Probe(rootPath, fresh: true);
        var fresh = new Sample { Value = new DriveSampleResult(p.Online, p.Total, p.Free, MeasurePartials(rootPath)), MeasuredAt = Environment.TickCount64 };
        _samples[rootPath] = fresh;
    }

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

    public static long PendingBytes(LibraryManifest manifest, Sync.Scope scope)
        => manifest.Items.SelectMany(i => i.Files)
            .Where(Sync.BackupScope.NeedsFetch)
            .Where(f => scope.Includes(f))
            .Sum(f => f.ExpectedSizeBytes ?? 0);

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
        Headroom.Comfortable => "#6FAE72",  // green
        Headroom.Tight       => "#D9A441",  // amber
        Headroom.WontFit     => "#C9605C",  // red
        _               => "#9AA4B0",       // neutral
    };
}

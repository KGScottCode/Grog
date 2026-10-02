// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Volumes;

namespace Grog.Core.Runs;

/// <summary>(09-19) GOG's size is a rounded label. Near a drive's per-file ceiling (FAT32: 4 GiB - 1) the label cannot
/// say which side the file is on: 45 of the owner's Windows parts read exactly 4 GiB. Before a run places them, ask
/// the CDN for the real length and record it as <see cref="GameFile.WireSizeBytes"/>, so the plan, the red rows and
/// the "too big for this drive" wording all rest on a measured size. Asked once per file; the answer persists.</summary>
public static class CeilingSizeCheck
{
    /// <summary>No real length yet, and the label is within the rounding slack of <paramref name="max"/> either way.</summary>
    public static bool IsUnsettled(GameFile f, long max)
        => max != long.MaxValue && f.WireSizeBytes is null && f.ExpectedSizeBytes is { } e && Math.Abs(e - max) <= PlacementPlanner.LabelSlack(max);

    public static List<GameFile> Unsettled(IEnumerable<GameFile> files, IReadOnlyList<DeviceSpace> devices)
    {
        var ceilings = devices.Where(d => d.IsOnline && d.MaxFileBytes != long.MaxValue).Select(d => d.MaxFileBytes).Distinct().ToList();
        return ceilings.Count == 0 ? new List<GameFile>() : files.Where(f => ceilings.Any(c => IsUnsettled(f, c))).ToList();
    }

    /// <summary>Ask <paramref name="realSizeOf"/> (the CDN, no body) for each unsettled file, four at a time, and write
    /// the answers under the manifest gate. A file that cannot be asked stays unsettled: planned as fitting, stopped by
    /// the engine's own check if it is not. Returns how many sizes were learned.</summary>
    public static async Task<int> ResolveAsync(IManifestStore manifest, IReadOnlyList<GameFile> unsettled,
                                               Func<GameFile, CancellationToken, Task<long?>> realSizeOf, CancellationToken ct)
    {
        if (unsettled.Count == 0) return 0;
        var answers = new (GameFile File, long? Size)[unsettled.Count];
        using var slots = new SemaphoreSlim(4);
        await Task.WhenAll(unsettled.Select(async (f, i) =>
        {
            await slots.WaitAsync(ct);
            try { answers[i] = (f, await realSizeOf(f, ct)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { answers[i] = (f, null); }
            finally { slots.Release(); }
        }));
        int learned = 0;
        using (manifest.Gate.Enter())
            foreach (var (f, size) in answers)
                if (size is { } s && s > 0) { f.WireSizeBytes = s; learned++; }
        return learned;
    }
}

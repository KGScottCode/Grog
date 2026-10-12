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

public static class CeilingSizeCheck
{
    public static bool IsUnsettled(GameFile f, long max)
        => max != long.MaxValue && f.WireSizeBytes is null && f.ExpectedSizeBytes is { } e && Math.Abs(e - max) <= PlacementPlanner.LabelSlack(max);

    public static List<GameFile> Unsettled(IEnumerable<GameFile> files, IReadOnlyList<DeviceSpace> devices)
    {
        var ceilings = devices.Where(d => d.IsOnline && d.MaxFileBytes != long.MaxValue).Select(d => d.MaxFileBytes).Distinct().ToList();
        return ceilings.Count == 0 ? new List<GameFile>() : files.Where(f => ceilings.Any(c => IsUnsettled(f, c))).ToList();
    }

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

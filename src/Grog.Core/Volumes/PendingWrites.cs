// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;

namespace Grog.Core.Volumes;

public sealed class PendingWrites
{
    private readonly object _gate = new();
    private Func<IReadOnlyList<(string TargetPath, long Bytes)>>? _source;

    public void Register(Func<IReadOnlyList<(string TargetPath, long Bytes)>> source) { lock (_gate) _source = source; }
    public void Clear() { lock (_gate) _source = null; }

    public long ForVolume(string? path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        Func<IReadOnlyList<(string, long)>>? src; lock (_gate) src = _source;
        if (src is null) return 0;
        string? vol; try { vol = Storage.DriveResolver.MountRootOf(path); } catch { return 0; }
        if (vol is null) return 0;
        long sum = 0;
        IReadOnlyList<(string TargetPath, long Bytes)> items;
        try { items = src(); } catch { return 0; }
        foreach (var (target, bytes) in items)
        {
            if (bytes <= 0) continue;
            string? tv; try { tv = Storage.DriveResolver.MountRootOf(target); } catch { continue; }
            if (string.Equals(tv, vol, StringComparison.OrdinalIgnoreCase)) sum += bytes;
        }
        return sum;
    }
}

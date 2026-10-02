// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;

namespace Grog.Core.Volumes;

/// <summary>(09-22, owner) Moves and downloads run side by side. What keeps them honest is one ledger of bytes a
/// running MOVE still has to write per volume: the download planner and the engine's last-moment room check both
/// subtract it from a drive's free space, so "fits" means fits once the move has landed too. The host owns ONE
/// instance per session and hands it to the planner (<see cref="VolumeService"/>) and the engine; it registers the
/// running job's remaining cross-volume copies; nothing registered = nothing pending.</summary>
public sealed class PendingWrites
{
    private readonly object _gate = new();
    private Func<IReadOnlyList<(string TargetPath, long Bytes)>>? _source;

    /// <summary>The host's live view of what is still to be copied: target path + bytes not yet on disk.</summary>
    public void Register(Func<IReadOnlyList<(string TargetPath, long Bytes)>> source) { lock (_gate) _source = source; }
    public void Clear() { lock (_gate) _source = null; }

    /// <summary>Bytes a running move still has to write onto the volume that holds <paramref name="path"/>.</summary>
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

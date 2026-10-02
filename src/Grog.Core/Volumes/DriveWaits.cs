using System.Collections.Generic;
using System.Linq;
using System;
using System.Collections.Concurrent;

namespace Grog.Core.Volumes;

/// <summary>(09-25, owner) Storage that went away while files were headed to it. Process-wide, like the
/// <see cref="DriveResolver"/> busy registry: the engine adds a root the moment a file finds its folder gone, and
/// the host removes it when the drive is back (or the user sends the files elsewhere / removes them).
/// While a root is here its files WAIT: the planner keeps them on it (no silent spill, no "won't fit"), and no
/// engine starts them. Nothing is persisted: a new process plans against the drives as they are.</summary>
public static class DriveWaits
{
    private static readonly ConcurrentDictionary<string, byte> _roots = new(StringComparer.Ordinal);
    public static bool Contains(string? rootId) => !string.IsNullOrEmpty(rootId) && _roots.ContainsKey(rootId);
    /// <summary>True when newly added.</summary>
    public static bool Add(string? rootId) => !string.IsNullOrEmpty(rootId) && _roots.TryAdd(rootId, 0);
    /// <summary>True when it was waiting.</summary>
    public static bool Remove(string? rootId) => !string.IsNullOrEmpty(rootId) && _roots.TryRemove(rootId, out _);
    public static IReadOnlyCollection<string> Roots => _roots.Keys.ToList();
    /// <summary>Tests only (the runner clears it per class through TestHooks); the app never resets it.</summary>
    public static void Clear() => _roots.Clear();
}

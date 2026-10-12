using System.Collections.Generic;
using System.Linq;
using System;
using System.Collections.Concurrent;

namespace Grog.Core.Volumes;

public static class DriveWaits
{
    private static readonly ConcurrentDictionary<string, byte> _roots = new(StringComparer.Ordinal);
    public static bool Contains(string? rootId) => !string.IsNullOrEmpty(rootId) && _roots.ContainsKey(rootId);
    public static bool Add(string? rootId) => !string.IsNullOrEmpty(rootId) && _roots.TryAdd(rootId, 0);
    public static bool Remove(string? rootId) => !string.IsNullOrEmpty(rootId) && _roots.TryRemove(rootId, out _);
    public static IReadOnlyCollection<string> Roots => _roots.Keys.ToList();
    public static void Clear() => _roots.Clear();
}

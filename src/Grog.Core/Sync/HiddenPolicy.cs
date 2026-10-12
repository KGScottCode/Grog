// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Linq;
using System.Collections.Generic;
using Grog.Core.Models;

namespace Grog.Core.Sync;

public readonly record struct HiddenPolicy(
    bool HideMature,
    int MatureAgeThreshold,
    IReadOnlyCollection<string>? Boards,
    IReadOnlySet<long>? HiddenIds,
    bool RevealHidden = false,
    IReadOnlySet<long>? KeptIds = null)
{
    // Parameter names match the primary constructor so named arguments bind to either overload.
    public HiddenPolicy(
        bool HideMature, int MatureAgeThreshold, IReadOnlyCollection<string>? Boards,
        IEnumerable<long>? HiddenIds, bool RevealHidden = false, IEnumerable<long>? KeptIds = null)
        : this(HideMature, MatureAgeThreshold, Boards, ToSet(HiddenIds), RevealHidden, ToSet(KeptIds)) { }

    public static IReadOnlySet<long>? ToSet(IEnumerable<long>? ids)
    {
        if (ids is null) return null;
        if (ids is IReadOnlySet<long> s) return s.Count > 0 ? s : null;
        var set = new HashSet<long>(ids);
        return set.Count > 0 ? set : null;
    }

    public static readonly HiddenPolicy ShowEverything = new(false, AdultAge, null, null);

    public const int AdultAge = 18;

    public bool IsHidden(LibraryItem item)
    {
        if (item is null) return false;
        // "Show hidden items" is a temporary reveal, never persisted -- it unhides the per-game picks only.
        if (!RevealHidden && HiddenIds is { Count: > 0 } && HiddenIds.Contains(item.GogId)) return true;
        if (!HideMature) return false;
        // A game the user chose to Keep in the mature review stays visible; per-game hides still apply.
        if (KeptIds is { Count: > 0 } && KeptIds.Contains(item.GogId)) return false;
        return IsMatureRated(item);
    }

    public bool IsMatureRated(LibraryItem item)
        => item is not null && EffectiveAge(item.AgeRatings) >= (MatureAgeThreshold > 0 ? MatureAgeThreshold : AdultAge);

    public bool IsVisible(LibraryItem item) => !IsHidden(item);

    public int EffectiveAge(IReadOnlyDictionary<string, int>? ratings)
    {
        if (ratings is null || ratings.Count == 0) return -1;
        int max = -1;
        foreach (var kv in ratings)
        {
            if (Boards is { Count: > 0 } && !Contains(Boards, kv.Key)) continue;
            if (kv.Value > max) max = kv.Value;
        }
        return max;
    }

    private static bool Contains(IReadOnlyCollection<string> set, string key)
    {
        foreach (var s in set)
            if (string.Equals(s, key, System.StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

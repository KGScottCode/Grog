// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Linq;

namespace Grog.Core.Format;

/// <summary>Version strings as the hosts show them.</summary>
public static class VersionText
{
    /// <summary>MAJOR.MINOR.PATCH from an informational version: build metadata after '+' and a fourth
    /// build-counter part are dropped; an empty input falls back, then to "0.0.0".</summary>
    public static string Trim3(string? info, string? fallback)
    {
        var v = string.IsNullOrWhiteSpace(info) ? fallback ?? "0.0.0" : info;
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        var parts = v.Split('.');
        return parts.Length > 3 ? string.Join('.', parts.Take(3)) : v;
    }
}

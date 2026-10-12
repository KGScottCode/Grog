// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Linq;

namespace Grog.Core.Format;

public static class VersionText
{
    public static string Trim3(string? info, string? fallback)
    {
        var v = string.IsNullOrWhiteSpace(info) ? fallback ?? "0.0.0" : info;
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        var parts = v.Split('.');
        return parts.Length > 3 ? string.Join('.', parts.Take(3)) : v;
    }
}

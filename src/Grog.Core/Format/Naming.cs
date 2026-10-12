// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Grog.Core.Format;

public static class Naming
{
    public static string Slug(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "untitled";
        var s = new string(title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        while (s.Contains("__")) s = s.Replace("__", "_");
        return s.Trim('_') is { Length: > 0 } r ? r : "untitled";
    }

    public static string WindowsSafe(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name) sb.Append(c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || c < 32 ? '_' : c);
        return sb.ToString().Trim(' ', '.');
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "untitled";

        var lastSlash = name.LastIndexOfAny(new[] { '/', '\\' });
        if (lastSlash >= 0) name = name[(lastSlash + 1)..];

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray());

        cleaned = cleaned.Trim().Trim('.').Trim();
        if (cleaned.Length == 0) return "untitled";

        // Reserved device name
        var stem = cleaned.Split('.')[0];
        if (ReservedNames.Contains(stem)) cleaned = "_" + cleaned;

        // Cap the component length
        const int max = 150;
        if (cleaned.Length > max)
        {
            var ext = Path.GetExtension(cleaned);
            if (ext.Length is > 0 and <= 16)
                cleaned = cleaned[..(max - ext.Length)] + ext;
            else
                cleaned = cleaned[..max];
        }
        return cleaned;
    }
}

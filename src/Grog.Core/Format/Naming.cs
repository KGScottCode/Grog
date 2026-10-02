// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Grog.Core.Format;

/// <summary>
/// THE on-disk naming rules. A slug names a game's folder and a safe file name is the only guard between an
/// untrusted server filename and the write path; both decide where bytes land, so a second copy that drifts
/// silently re-homes a library. Three slugifiers and two sanitizers existed before 09-02.
/// </summary>
public static class Naming
{
    /// <summary>Folder-safe slug: lowercase, every non-alphanumeric run to one underscore, trimmed;
    /// "untitled" when nothing survives. "Baldur's Gate II" -> "baldur_s_gate_ii".</summary>
    public static string Slug(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "untitled";
        var s = new string(title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        while (s.Contains("__")) s = s.Replace("__", "_");
        return s.Trim('_') is { Length: > 0 } r ? r : "untitled";
    }

    /// <summary>What Windows would have made of a GOG display label saved as a file name: the nine reserved
    /// characters and controls to underscore, outer spaces and dots trimmed. Used to recognise files written
    /// under a display label by an older build.</summary>
    public static string WindowsSafe(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name) sb.Append(c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || c < 32 ? '_' : c);
        return sb.ToString().Trim(' ', '.');
    }

    // Reserved DOS device names (case-insensitive, with or without an extension) that Windows refuses as
    // file names. A game or CDN file literally called "CON" / "NUL" would otherwise fail to write.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Turn an untrusted name (server Content-Disposition, URL tail, or a GOG file name) into a
    /// single, safe file-name component. Defends against directory traversal (no separators or "..", so the
    /// result can never escape its target folder), reserved Windows device names, and pathological length.</summary>
    public static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "untitled";

        // Collapse away any path structure: take only the last component and drop separators of EITHER OS
        // (a Windows-style "a\b" reaching a Linux box wouldn't be split by Path, so handle both explicitly).
        var lastSlash = name.LastIndexOfAny(new[] { '/', '\\' });
        if (lastSlash >= 0) name = name[(lastSlash + 1)..];

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray());

        // Trim leading/trailing dots and spaces: "..", "." and trailing dots/spaces are traversal- or
        // Windows-hostile (Explorer silently strips a trailing dot, so the on-disk name would drift).
        cleaned = cleaned.Trim().Trim('.').Trim();
        if (cleaned.Length == 0) return "untitled";

        // Reserved device name (matched on the stem, before the first dot) -> prefix so it's no longer reserved.
        var stem = cleaned.Split('.')[0];
        if (ReservedNames.Contains(stem)) cleaned = "_" + cleaned;

        // Cap the component length, preserving the extension. 150 bytes leaves ample room under the 255-byte
        // per-component limit AND under Windows MAX_PATH for a reasonable directory depth.
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

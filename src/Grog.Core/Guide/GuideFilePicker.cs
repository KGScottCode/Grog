// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using Grog.Core.Models;
using Grog.Core.Sync;

namespace Grog.Core.Guide;

/// <summary>
/// The guide's example-file selection policy: which single file the back-up step points at.
/// Core-owned so the GUI never re-derives it and the rule is testable without a window.
/// </summary>
public static class GuideFilePicker
{
    /// <summary>Picks the smallest actionable (NotBackedUp/Missing/Outdated) in-scope game file with a positive
    /// expected size; patches are excluded by kind AND by name, extras by kind. Ties break alphabetically by
    /// game then file (GOG rounds sizes to whole MB, so ties are common). Null when nothing needs backing up.</summary>
    /// <param name="alreadyInFlight">Files that show no download arrow right now (queued, or a paused partial):
    /// the stop asks the user to CLICK the arrow, so a file whose arrow is not on screen is no demo.</param>
    public static (long GogId, string FileKey)? Pick(IEnumerable<LibraryItem> items, Scope scope,
                                                     Func<long, GameFile, bool>? alreadyInFlight = null)
    {
        (long Gog, string Key, long Size, string Game, string File)? best = null;
        foreach (var it in items)
        {
            if (it is null) continue;
            foreach (var f in it.Files)
            {
                if (f.Kind is FileKind.Extra or FileKind.Patch) continue;
                if (f.State is not (FileState.NotBackedUp or FileState.Missing or FileState.Outdated)) continue;
                if (!scope.Includes(f)) continue;
                if (f.HasPartial || alreadyInFlight?.Invoke(it.GogId, f) == true) continue;
                var size = f.ExpectedSizeBytes ?? 0;
                if (size <= 0) continue;
                if (f.Name.Contains("patch", StringComparison.OrdinalIgnoreCase)) continue;
                if (best is { } b)
                {
                    if (size > b.Size) continue;
                    if (size == b.Size)
                    {
                        int byGame = string.Compare(it.Title, b.Game, StringComparison.OrdinalIgnoreCase);
                        if (byGame > 0) continue;
                        if (byGame == 0 && string.Compare(f.Name, b.File, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    }
                }
                best = (it.GogId, f.FileKey, size, it.Title, f.Name);
            }
        }
        return best is { } w ? (w.Gog, w.Key) : null;
    }
}

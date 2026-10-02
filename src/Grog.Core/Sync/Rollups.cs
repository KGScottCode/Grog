// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Sync;

/// <summary>
/// The ONE place byte-weighted progress is computed (file, game, library, device). Pure: no state, no I/O.
/// Done bytes = sum(present file sizes) + sum(received bytes of in-flight/partial files), so a bar climbs
/// smoothly mid-download; callers inject live bytes via <see cref="InFlightBytes"/>, and with no lookup
/// the rollups degrade cleanly to whole-file accounting.
/// </summary>
public static class Rollups
{
    /// <summary>Received bytes for a file that is mid-transfer (download or move), keyed by the file.
    /// Return 0 (or don't register the file) when nothing is flowing. Never exceeds the file's size.</summary>
    public delegate long InFlightBytes(GameFile file);

    private static readonly InFlightBytes NoInFlight = _ => 0;

    /// <summary>Total (expected, else last-known-local) size of a single file, 0 when unknown.</summary>
    public static long TotalBytesOf(GameFile f) => f.ExpectedSizeBytes ?? f.LocalSizeBytes ?? 0;

    /// <summary>Bytes actually HELD for a file: local size preferred, expected as the fallback. The disk
    /// view's number (composition, per-device usage, delete estimates), where what is on disk outranks
    /// what GOG advertises. TotalBytesOf is the planning view; this is the holdings view.</summary>
    public static long HeldBytesOf(GameFile f) => f.LocalSizeBytes ?? f.ExpectedSizeBytes ?? 0;

    /// <summary>Done bytes for one file: full size when present; otherwise the greater of persisted partial
    /// bytes and live received bytes, clamped to the file size so a stale number cannot pass 100%.</summary>
    public static long DoneBytesOf(GameFile f, InFlightBytes? inFlight = null)
    {
        long total = TotalBytesOf(f);
        if (BackupScope.IsPresent(f)) return total;
        long recv = Math.Max(f.PartialBytes ?? 0, (inFlight ?? NoInFlight)(f));
        return recv <= 0 ? 0 : (total > 0 ? Math.Min(recv, total) : recv);
    }

    /// <summary>Progress for one product, over the given scope (kind + language).</summary>
    public static ProgressSnapshot Game(LibraryItem item, Scope scope, InFlightBytes? inFlight = null)
    {
        long done = 0, total = 0;
        foreach (var f in item.Files)
        {
            if (!scope.Includes(f)) continue;
            total += TotalBytesOf(f);
            done += DoneBytesOf(f, inFlight);
        }
        return new(done, total);
    }

    /// <summary>FILE-COUNT progress for one product, 0-100, in the unit the Library row's bar and fraction use
    /// (owner 09-16): every present in-scope file is one, and a file mid-transfer is the fraction of it that
    /// has arrived, so the bar still climbs smoothly. The row's live figure was byte-weighted while its resting
    /// figure was file-weighted, and the bar took the larger: a game whose one big file was done read 90%
    /// while downloading and fell to 20% (1/5) the moment the run ended (sweep 2 #18). Same scoped set as
    /// <see cref="BackupScope.Scoped"/>.</summary>
    public static double GameFilePercent(LibraryItem item, Scope scope, InFlightBytes? inFlight = null)
    {
        double done = 0; int total = 0;
        foreach (var f in BackupScope.Scoped(item, scope))
        {
            total++;
            if (BackupScope.IsPresent(f)) { done += 1; continue; }
            long size = TotalBytesOf(f);
            if (size > 0) done += Math.Min(1.0, DoneBytesOf(f, inFlight) / (double)size);
        }
        return total == 0 ? 0 : 100.0 * done / total;
    }

    /// <summary>The three library-level bars: Games (game-kind files), Extras (extra-kind files), and
    /// Whole (both). Non-downloadable products and out-of-scope language content never enter the numbers.
    /// Cloud saves are excluded entirely (separate lifecycle).</summary>
    public static LibraryRollup Library(LibraryManifest manifest, Scope scope, InFlightBytes? inFlight = null)
    {
        long gDone = 0, gTotal = 0, xDone = 0, xTotal = 0;
        foreach (var item in manifest.Items)
        {
            if (!LibraryStats.IsBackupEligible(item)) continue;
            foreach (var f in item.Files)
            {
                // Language filter always applies; the games/extras split is by kind, not by the scope
                // toggle -- each card shows its own category's full magnitude in the active languages.
                if (!scope.IncludesLanguage(f)) continue;
                long total = TotalBytesOf(f);
                long done = DoneBytesOf(f, inFlight);
                if (f.Kind == FileKind.Extra) { xTotal += total; xDone += done; }
                else { gTotal += total; gDone += done; }
            }
        }
        return new LibraryRollup(new(gDone, gTotal), new(xDone, xTotal));
    }

}

/// <summary>The three library bars. Whole = games + extras combined.</summary>
public readonly record struct LibraryRollup(ProgressSnapshot Games, ProgressSnapshot Extras)
{
    public ProgressSnapshot Whole => new(Games.DoneBytes + Extras.DoneBytes, Games.TotalBytes + Extras.TotalBytes);
}


// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Sync;

public static class Rollups
{
    public delegate long InFlightBytes(GameFile file);

    private static readonly InFlightBytes NoInFlight = _ => 0;

    public static long TotalBytesOf(GameFile f) => f.ExpectedSizeBytes ?? f.LocalSizeBytes ?? 0;

    public static long HeldBytesOf(GameFile f) => f.LocalSizeBytes ?? f.ExpectedSizeBytes ?? 0;

    public static long DoneBytesOf(GameFile f, InFlightBytes? inFlight = null)
    {
        long total = TotalBytesOf(f);
        if (BackupScope.IsPresent(f)) return total;
        long recv = Math.Max(f.PartialBytes ?? 0, (inFlight ?? NoInFlight)(f));
        return recv <= 0 ? 0 : (total > 0 ? Math.Min(recv, total) : recv);
    }

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

    public static LibraryRollup Library(LibraryManifest manifest, Scope scope, InFlightBytes? inFlight = null)
    {
        long gDone = 0, gTotal = 0, xDone = 0, xTotal = 0;
        foreach (var item in manifest.Items)
        {
            if (!LibraryStats.IsBackupEligible(item)) continue;
            foreach (var f in item.Files)
            {
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

public readonly record struct LibraryRollup(ProgressSnapshot Games, ProgressSnapshot Extras)
{
    public ProgressSnapshot Whole => new(Games.DoneBytes + Extras.DoneBytes, Games.TotalBytes + Extras.TotalBytes);
}


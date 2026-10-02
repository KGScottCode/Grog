// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Sync;

/// <summary>
/// What each choice on ONE scope axis would COST, priced against the user's other choices (09-11).
///
/// The rule every axis follows, and the reason this is not just a filtered sum: the axis being priced is
/// LIFTED out of the scope first. An option the user has not picked must show what picking it would cost,
/// not zero -- the number is there to inform the choice, so measuring it under the current choice would
/// make every unpicked option read as empty.
///
/// Pure and Core-side so the pricing rule has one definition and can be pinned by tests; the Settings card
/// only renders what comes back.
/// </summary>
public static class ScopeTally
{
    /// <summary>A count and its byte weight. Adds, because "Games &amp; Extras" is the two halves and must
    /// never be measured a second time by a slightly different walk.</summary>
    public readonly record struct Bucket(int Files, long Bytes)
    {
        public static Bucket operator +(Bucket a, Bucket b) => new(a.Files + b.Files, a.Bytes + b.Bytes);
    }

    /// <summary>The content axis' three choices. <see cref="Both"/> is derived, never counted separately.</summary>
    public readonly record struct ContentTally(Bucket Games, Bucket Extras)
    {
        public Bucket Both => Games + Extras;
    }

    /// <summary>Prices "Games &amp; Extras" / "Games only" / "Extras only" under <paramref name="scope"/>'s
    /// language and platform picks, with the games/extras axis lifted.
    ///
    /// Unavailable files stay out (<see cref="BackupScope.CountsTowardCompleteness"/>) -- the same rule every
    /// other stat in the app uses, so this number agrees with Overview instead of quietly exceeding it.</summary>
    public static ContentTally ByContent(LibraryManifest manifest, Scope scope)
    {
        var lifted = scope with { Games = true, Extras = true };
        int gFiles = 0, xFiles = 0; long gBytes = 0, xBytes = 0;
        foreach (var item in manifest.Items)
            foreach (var f in item.Files)
            {
                if (!lifted.Includes(f)) continue;
                if (!BackupScope.CountsTowardCompleteness(f)) continue;
                long bytes = Rollups.TotalBytesOf(f);
                if (f.Kind == FileKind.Extra) { xFiles++; xBytes += bytes; }
                else { gFiles++; gBytes += bytes; }
            }
        return new ContentTally(new Bucket(gFiles, gBytes), new Bucket(xFiles, xBytes));
    }
}

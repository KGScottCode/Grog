// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Sync;

public static class ScopeTally
{
    public readonly record struct Bucket(int Files, long Bytes)
    {
        public static Bucket operator +(Bucket a, Bucket b) => new(a.Files + b.Files, a.Bytes + b.Bytes);
    }

    public readonly record struct ContentTally(Bucket Games, Bucket Extras)
    {
        public Bucket Both => Games + Extras;
    }

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

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

    public readonly record struct ContentTally(Bucket Games, Bucket Extras, Bucket Patches)
    {
        public Bucket Both => Games + Extras;
    }

    public static ContentTally ByContent(LibraryManifest manifest, Scope scope)
    {
        // Games/Extras are priced with the content axis lifted but the patch choice kept; Patches are priced
        // with the patch axis lifted too, so the toggle's row shows what it adds or removes.
        var lifted = scope with { Games = true, Extras = true };
        var liftedAll = lifted with { IncludePatches = true };
        int gFiles = 0, xFiles = 0, pFiles = 0; long gBytes = 0, xBytes = 0, pBytes = 0;
        foreach (var item in manifest.Items)
            foreach (var f in item.Files)
            {
                if (!liftedAll.Includes(f)) continue;
                if (!BackupScope.CountsTowardCompleteness(f)) continue;
                long bytes = Rollups.TotalBytesOf(f);
                if (f.Kind == FileKind.Patch) { pFiles++; pBytes += bytes; }
                if (!lifted.Includes(f)) continue;
                if (f.Kind == FileKind.Extra) { xFiles++; xBytes += bytes; }
                else { gFiles++; gBytes += bytes; }
            }
        return new ContentTally(new Bucket(gFiles, gBytes), new Bucket(xFiles, xBytes), new Bucket(pFiles, pBytes));
    }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Api;

namespace Grog.App.Services;

public static class ArtCache
{
    public static string CoverPath(string dir, long gogId) => Path.Combine(dir, $"{gogId}_cover.jpg");

    public static string GalaxyPath(string dir, long gogId) => Path.Combine(dir, $"{gogId}_galaxy.jpg");

    public static async Task<GameArt?> CacheProductAsync(GogApiClient api, long gogId, string dir, bool needMeta, CancellationToken ct)
    {
        var cover = CoverPath(dir, gogId);
        var galaxy = GalaxyPath(dir, gogId);
        // Skip the network call only when BOTH art files are cached AND the metadata riding in the same
        // v2 response is not needed; returns the fetched GameArt, or null when nothing was fetched.
        if (File.Exists(cover) && File.Exists(galaxy) && !needMeta) return null;
        var art = await api.FetchGameArtAsync(gogId, ct);
        if (art.BoxArt is not null && !File.Exists(cover)) await File.WriteAllBytesAsync(cover, art.BoxArt, ct);
        if (art.Galaxy is not null && !File.Exists(galaxy)) await File.WriteAllBytesAsync(galaxy, art.Galaxy, ct);
        return art;
    }
}

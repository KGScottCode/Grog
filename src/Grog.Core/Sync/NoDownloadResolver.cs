// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Api;
using Grog.Core.Models;

namespace Grog.Core.Sync;

/// <summary>
/// Explains owned products that returned no downloadable details: records a real lookup status (never a
/// bare "unknown") and decides whether each product's content is already covered by a base game in the
/// local manifest, so real backup gaps stand out.
/// </summary>
public sealed class NoDownloadResolver
{
    private readonly GogApiClient _api;

    public int MaxParallelism { get; init; } = 3;
    public event Action<SyncProgress>? Progress;

    public NoDownloadResolver(GogApiClient api) => _api = api;

    /// <param name="noDownloadIds">Ids from a sync's NoDetailProductIds.</param>
    /// <param name="ownedGames">The LibraryItems that did parse, for coverage matching.</param>
    public async Task<IReadOnlyList<ResolvedProduct>> ResolveAsync(
        IReadOnlyList<long> noDownloadIds,
        IReadOnlyList<LibraryItem> ownedGames,
        CancellationToken ct = default)
    {
        var gameTitles = ownedGames.Select(g => g.Title).ToList();
        var results = new List<ResolvedProduct>();

        using var throttle = new SemaphoreSlim(MaxParallelism);
        int done = 0;
        var tasks = noDownloadIds.Select(async id =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var lookup = await LookUpAsync(id, ct);
                var coverage = DetermineCoverage(lookup.Title, gameTitles);
                var resolved = new ResolvedProduct(
                    Id: id,
                    Title: lookup.Title,
                    Type: lookup.Type,
                    StoreUrl: lookup.StoreUrl,
                    Status: lookup.Status,
                    Coverage: coverage.kind,
                    CoveredBy: coverage.coveredBy);
                lock (results) results.Add(resolved);
            }
            finally
            {
                throttle.Release();
                var n = Interlocked.Increment(ref done);
                Progress?.Invoke(new SyncProgress(n, noDownloadIds.Count));
            }
        });
        await Task.WhenAll(tasks);

        return results.OrderBy(r => r.Id).ToList();
    }

    private async Task<(string Title, string Type, string StoreUrl, string Status)> LookUpAsync(long id, CancellationToken ct)
    {
        // 1. Public catalog.
        try
        {
            var info = await _api.GetProductInfoAsync(id, ct);
            if (info is not null && !string.IsNullOrWhiteSpace(info.Title))
                return (info.Title, info.GameType, info.StoreUrl, "public");
        }
        catch (System.Net.Http.HttpRequestException hex) when (hex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // fall through to account scope
        }
        catch { /* try account scope below */ }

        // 2. Account-scoped: embed.gog.com can know a product as owned even when the public catalog 404s.
        try
        {
            var acct = await _api.GetAccountProductInfoAsync(id, ct);
            if (acct is not null && !string.IsNullOrWhiteSpace(acct.Title))
                return (acct.Title, acct.GameType, acct.StoreUrl, "account");
        }
        catch { /* record as not-found below */ }

        return ("(not found in public or account catalog)", "", "", "404");
    }

    /// <summary>Heuristic coverage: does a parsed game's title contain, or share a strong stem with, this product?</summary>
    public static (CoverageKind kind, string? coveredBy) DetermineCoverage(string productTitle, IReadOnlyList<string> gameTitles)
    {
        if (string.IsNullOrWhiteSpace(productTitle) || productTitle.StartsWith("("))
            return (CoverageKind.Unknown, null);

        var stem = BaseStem(productTitle);
        foreach (var g in gameTitles)
        {
            if (string.Equals(g, productTitle, StringComparison.OrdinalIgnoreCase))
                return (CoverageKind.SameProduct, g);
        }
        foreach (var g in gameTitles)
        {
            var gStem = BaseStem(g);
            if (gStem.Length >= 6 && (stem.StartsWith(gStem, StringComparison.OrdinalIgnoreCase)
                                   || gStem.StartsWith(stem, StringComparison.OrdinalIgnoreCase)))
                return (CoverageKind.LikelyCoveredByBaseGame, g);
        }
        return (CoverageKind.NotObviouslyCovered, null);
    }

    /// <summary>Title up to the first separator (":", "-", "--") -- the base-game name for packs/DLC.</summary>
    private static string BaseStem(string title)
    {
        int cut = title.IndexOfAny(new[] { ':', '-', '\u2013', '\u2014' });
        var stem = cut > 0 ? title[..cut] : title;
        return stem.Trim();
    }
}

public enum CoverageKind
{
    Unknown,                    // couldn't determine (no title)
    SameProduct,                // exact title match to a parsed game
    LikelyCoveredByBaseGame,    // shares a base stem with a parsed game (pack/DLC of it)
    NotObviouslyCovered,        // named, but no matching base game -- worth a look
}

public sealed record ResolvedProduct(
    long Id, string Title, string Type, string StoreUrl, string Status,
    CoverageKind Coverage, string? CoveredBy);

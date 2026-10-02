// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using Grog.Core.Api;
using Grog.Core.Models;

namespace Grog.Core.Sync;

/// <summary>
/// Assigns a <see cref="ProductType"/> from GOG signals: mod-tag set -> Mod (mods are typed as games),
/// isMovie -> Movie, no downloads + referenced as parent -> Pack, no downloads + child of a pack -> Dlc,
/// otherwise Game. Pure and side-effect free.
/// </summary>
public static class ProductTaxonomy
{
    public static ProductType Classify(
        OwnedProduct product,
        IReadOnlySet<long> modIds,
        bool hasDownloads,
        bool isReferencedAsParent)
    {
        if (modIds.Contains(product.Id)) return ProductType.Mod;
        if (product.IsMovie) return ProductType.Movie;

        if (!hasDownloads)
        {
            // No direct downloads: it's a grouping. If other owned products point at its slug,
            // it's a Pack; if it itself points at a different parent, it's DLC/child content.
            if (isReferencedAsParent) return ProductType.Pack;
            if (product.IsChildOfPack) return ProductType.Dlc;
            return ProductType.Pack; // no downloads and unreferenced: treat as a pack/bundle shell
        }

        return ProductType.Game;
    }
}

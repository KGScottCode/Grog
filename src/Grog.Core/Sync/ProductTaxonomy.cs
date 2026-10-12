// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using Grog.Core.Api;
using Grog.Core.Models;

namespace Grog.Core.Sync;

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
            // No direct downloads: it's a grouping or it's a Pack
            if (isReferencedAsParent) return ProductType.Pack;
            if (product.IsChildOfPack) return ProductType.Dlc;
            return ProductType.Pack; // no downloads and unreferenced: treat as a pack/bundle shell
        }

        return ProductType.Game;
    }
}

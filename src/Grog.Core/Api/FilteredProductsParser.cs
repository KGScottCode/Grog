// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
//
// Parses embed.gog.com/account/getFilteredProducts, GOG's rich library listing. Its `url` field points
// at a product's PARENT pack when it belongs to one -- the authoritative pack signal, no title guessing.
// Paged: honor totalPages.
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Grog.Core.Api;

public sealed class FilteredProductsPage
{
    public int Page { get; init; }
    public int TotalPages { get; init; }
    public int TotalProducts { get; init; }
    public List<OwnedProduct> Products { get; init; } = new();
}

/// <summary>One row from getFilteredProducts: a game/movie you own, with its parent link.</summary>
public sealed record OwnedProduct(
    long Id,
    string Title,
    string Slug,
    string Category,
    bool IsGame,
    bool IsMovie,
    int DlcCount,
    string Url,            // "/en/game/<parent-or-self-slug>"; empty when GOG omits it
    string Image = "",    // protocol-relative logo base ("//images.../<hash>"), "" when absent
    /// <summary>GOG's own "new to this account" marker. (New items 09-09) Consulted ONLY when a run
    /// starts with an EMPTY library, to seed the first scan; every later scan ignores it and decides
    /// from the manifest instead.</summary>
    bool IsNew = false)
{
    /// <summary>The slug embedded in Url (the last path segment), or "" -- this is the parent
    /// pack's slug for child products, or the product's own slug for standalone items.</summary>
    public string UrlSlug
    {
        get
        {
            if (string.IsNullOrEmpty(Url)) return "";
            var i = Url.LastIndexOf('/');
            return i >= 0 && i < Url.Length - 1 ? Url[(i + 1)..] : "";
        }
    }

    /// <summary>True when this product's Url points at a DIFFERENT slug than its own -- i.e. it's
    /// a child of a pack whose slug is UrlSlug.</summary>
    public bool IsChildOfPack => UrlSlug.Length > 0 && !string.Equals(UrlSlug, Slug, StringComparison.OrdinalIgnoreCase);
}

public static class FilteredProductsParser
{
    public static FilteredProductsPage Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        int page = GetInt(root, "page");
        int totalPages = GetInt(root, "totalPages");
        int totalProducts = GetInt(root, "totalProducts");

        var products = new List<OwnedProduct>();
        if (root.TryGetProperty("products", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in arr.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                if (!p.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) continue;

                products.Add(new OwnedProduct(
                    Id: id,
                    Title: GetStr(p, "title"),
                    Slug: GetStr(p, "slug"),
                    Category: GetStr(p, "category"),
                    IsGame: GetBool(p, "isGame"),
                    IsMovie: GetBool(p, "isMovie"),
                    DlcCount: GetInt(p, "dlcCount"),
                    Url: GetStr(p, "url"),
                    Image: GetStr(p, "image"),
                    IsNew: GetBool(p, "isNew")));   // (New items 09-09)
            }
        }

        return new FilteredProductsPage
        {
            Page = page,
            TotalPages = totalPages,
            TotalProducts = totalProducts,
            Products = products,
        };
    }

    private static string GetStr(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int GetInt(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.TryGetInt32(out var i) ? i : 0;
    private static bool GetBool(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;   // (New items 09-09) simplified; behaviour identical
}

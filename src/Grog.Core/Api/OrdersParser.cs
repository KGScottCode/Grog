// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
//
// Parses embed.gog.com/account/settings/orders/data, GOG's order history -- the ONLY place an
// acquisition date comes from. Measured against a real account 2026-09-09 (docs/dev/GOG_Orders_API_2026-09-09.md);
// the community reference lists the endpoint by name only, with no parameters and no response shape.
//
// Three facts of the payload that the shape here exists to survive:
//   - `date` is UNIX EPOCH SECONDS, not an ISO string.
//   - a product `id` is a STRING ("1472210676"), not a number.
//   - orders are COARSE: one order dates every product inside it, and free claims produce orders too
//     (a batch giveaway claim was one order at 0.00, paymentMethod "Free Order").
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Grog.Core.Api;

/// <summary>One order: when it happened, and which products it covers.</summary>
public sealed record OrderRecord(DateTimeOffset Date, string PublicId, IReadOnlyList<OrderProduct> Products);

/// <summary>One product inside an order. <paramref name="IsRefunded"/> is carried, not acted on: a refunded
/// product is usually no longer owned, and mapping only ids we already hold is self-limiting.</summary>
public sealed record OrderProduct(long Id, string Title, bool IsRefunded);

/// <summary>What one sweep of order history cost and found: id -> earliest order date, the number of HTTP
/// requests actually spent (the honest figure a caller reports, not the page cap it asked for), and whether
/// the walk was COMPLETE. Complete means every page was read to the end; it is false for a deliberately
/// capped walk AND for one cut short by an error or a shape change. Only a complete walk may be used to
/// conclude that a product has no order record -- a truncated one has not finished asking.</summary>
public sealed record PurchaseDateSweep(IReadOnlyDictionary<long, DateTimeOffset> Dates, int Requests, bool Complete)
{
    public static readonly PurchaseDateSweep Empty = new(new Dictionary<long, DateTimeOffset>(), 0, false);
}

/// <summary>One page of order history.</summary>
public sealed class OrdersPage
{
    public int TotalPages { get; init; }
    public List<OrderRecord> Orders { get; init; } = new();
}

public static class OrdersParser
{
    /// <summary>The endpoint, with the filter flags that are NOT optional: a bare `?page=N` answers 200 with
    /// an EMPTY orders array, which reads exactly like an account with no orders. (Measured 09-09.)</summary>
    public static string PageUrl(int page)
        => "https://embed.gog.com/account/settings/orders/data"
         + "?canceled=0&completed=1&in_progress=1&not_redeemed=1&pending=1&redeemed=1&page=" + page;

    public static OrdersPage Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var orders = new List<OrderRecord>();

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("orders", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in arr.EnumerateArray())
            {
                if (o.ValueKind != JsonValueKind.Object) continue;
                if (ReadDate(o) is not { } when) continue;   // an order with no usable date dates nothing

                var products = new List<OrderProduct>();
                if (o.TryGetProperty("products", out var ps) && ps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in ps.EnumerateArray())
                    {
                        if (p.ValueKind != JsonValueKind.Object) continue;
                        if (ReadProductId(p) is not { } pid) continue;
                        products.Add(new OrderProduct(pid, Str(p, "title"), Bool(p, "isRefunded")));
                    }
                }
                orders.Add(new OrderRecord(when, Str(o, "publicId"), products));
            }
        }

        return new OrdersPage { TotalPages = Int(root, "totalPages"), Orders = orders };
    }

    /// <summary>Fold pages into "when did this product enter the library": product id -> EARLIEST order date.
    /// Earliest, because a product can appear in more than one order (gifted, then bought again) and the
    /// question the UI asks is when you got it, not the last time money moved.</summary>
    public static void Fold(IEnumerable<OrderRecord> orders, Dictionary<long, DateTimeOffset> into)
    {
        foreach (var o in orders)
            foreach (var p in o.Products)
                if (!into.TryGetValue(p.Id, out var have) || o.Date < have)
                    into[p.Id] = o.Date;
    }

    /// <summary>`date` is epoch SECONDS. A string form is accepted too, because one endpoint variant
    /// quoted it; anything else leaves the order undated rather than guessing.</summary>
    private static DateTimeOffset? ReadDate(JsonElement o)
    {
        if (!o.TryGetProperty("date", out var d)) return null;
        if (d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out var secs)) return FromEpoch(secs);
        if (d.ValueKind == JsonValueKind.String)
        {
            var s = d.GetString();
            if (long.TryParse(s, out var parsed)) return FromEpoch(parsed);
            if (DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                                        System.Globalization.DateTimeStyles.AssumeUniversal
                                        | System.Globalization.DateTimeStyles.AdjustToUniversal, out var iso)) return iso;
        }
        return null;
    }

    // Guard the epoch range: DateTimeOffset throws outside it, and a junk value must not take a scan down.
    private static DateTimeOffset? FromEpoch(long secs)
        => secs <= 0 || secs > 253_402_300_799 ? null : DateTimeOffset.FromUnixTimeSeconds(secs);

    /// <summary>Product ids arrive as STRINGS here (they are numbers in every other GOG payload).
    /// Both forms are read so a future tightening on GOG's side does not silently drop every date.</summary>
    private static long? ReadProductId(JsonElement p)
    {
        if (!p.TryGetProperty("id", out var id)) return null;
        if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n)) return n;
        if (id.ValueKind == JsonValueKind.String && long.TryParse(id.GetString(), out var s)) return s;
        return null;
    }

    private static string Str(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int Int(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.TryGetInt32(out var i) ? i : 0;
    private static bool Bool(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
}

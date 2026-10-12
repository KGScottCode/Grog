// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Grog.Core.Api;

public sealed record OrderRecord(DateTimeOffset Date, string PublicId, IReadOnlyList<OrderProduct> Products);

public sealed record OrderProduct(long Id, string Title, bool IsRefunded);

public sealed record PurchaseDateSweep(IReadOnlyDictionary<long, DateTimeOffset> Dates, int Requests, bool Complete)
{
    public static readonly PurchaseDateSweep Empty = new(new Dictionary<long, DateTimeOffset>(), 0, false);
}

public sealed class OrdersPage
{
    public int TotalPages { get; init; }
    public List<OrderRecord> Orders { get; init; } = new();
}

public static class OrdersParser
{
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

    public static void Fold(IEnumerable<OrderRecord> orders, Dictionary<long, DateTimeOffset> into)
    {
        foreach (var o in orders)
            foreach (var p in o.Products)
                if (!into.TryGetValue(p.Id, out var have) || o.Date < have)
                    into[p.Id] = o.Date;
    }

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

    // Guard the epoch range
    private static DateTimeOffset? FromEpoch(long secs)
        => secs <= 0 || secs > 253_402_300_799 ? null : DateTimeOffset.FromUnixTimeSeconds(secs);

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

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Sync;

namespace Grog.Cli;

internal static partial class CliVerbs
{
    internal static async Task<int> ClearNew(CliContext ctx)
    {
        var args = ctx.Args;
        var manifest = ctx.Manifest;
        await manifest.LoadAsync();
        bool all = args.Contains("--all", StringComparer.OrdinalIgnoreCase);
        var q = string.Join(' ', PositionalArgs(args.Skip(1)));

        if (!all && string.IsNullOrWhiteSpace(q))
        {
            Out.Usage("Usage: clear-new --all | clear-new <game title|id>");
            return Continue;
        }

        int n = 0;
        if (all)
        {
            manifest.Mutate(m => n = NewItems.ClearAll(m));   // 
        }
        else
        {
            var item = manifest.Current.FindItem(q);
            if (item is null) { Out.Usage($"No item matched '{q}'."); return Continue; }
            manifest.Mutate(m => n = NewItems.Clear(m, new[] { item.GogId }));   // 
        }
        if (n > 0) await manifest.SaveAsync();
        Out.Success(n == 0 ? "Nothing was flagged New." : $"Cleared the New flag on {n} item(s).");
        return Continue;
    }

    internal static async Task<int> MarkNew(CliContext ctx)
    {
        var args = ctx.Args;
        var manifest = ctx.Manifest;
        await manifest.LoadAsync();
        bool all = args.Contains("--all", StringComparer.OrdinalIgnoreCase);
        var q = string.Join(' ', PositionalArgs(args.Skip(1)));
        if (!all && string.IsNullOrWhiteSpace(q)) { Out.Usage("Usage: mark-new --all | mark-new <game title|id>"); return Continue; }

        int n = 0;
        manifest.Mutate(m =>
        {
            foreach (var item in m.Items)
            {
                if (!all && !(item.GogId.ToString() == q || item.Title.Equals(q, StringComparison.OrdinalIgnoreCase))) continue;
                if (item.IsNew) continue;
                item.IsNew = true; n++;
            }
        });   // 
        if (n > 0) await manifest.SaveAsync();
        Out.Success($"Flagged {n} item(s) New.");
        return Continue;
    }

    internal static async Task<int> Ordersprobe(CliContext ctx)
    {
        var manifest = ctx.Manifest;
        await manifest.LoadAsync();
        var session = await ctx.Auth.EnsureAuthenticatedAsync(CancellationToken.None);

        string[] urls =
        {
            "https://embed.gog.com/account/settings/orders/data?canceled=0&completed=1&in_progress=1&not_redeemed=1&pending=1&redeemed=1&page=1",
            "https://embed.gog.com/account/settings/orders/data?page=1",
            "https://embed.gog.com/account/settings/orders/data?canceled=1&completed=1&in_progress=1&not_redeemed=1&pending=1&redeemed=1&page=1",
            "https://api.gog.com/orders",
        };

        var dated = new HashSet<long>();
        bool anyOk = false;
        foreach (var url in urls)
        {
            Out.Heading(url);
            string body;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
                using var resp = await ctx.Http.SendAsync(req, CancellationToken.None);
                Out.Info($"  HTTP {(int)resp.StatusCode} {resp.StatusCode}");
                body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    Out.Dim($"  {Trunc(body.Replace('\n', ' '), 200)}");
                    continue;
                }
            }
            catch (Exception ex)
            {
                Out.Warn($"  request failed: {ex.Message}");
                continue;
            }

            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (Exception ex) { Out.Warn($"  not JSON ({ex.Message}): {Trunc(body.Replace('\n', ' '), 200)}"); continue; }
            using (doc)
            {
                var orders = FindArray(doc.RootElement, "orders") ?? FindArray(doc.RootElement, "items")
                          ?? (doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : (JsonElement?)null);
                if (orders is not { } arr || arr.GetArrayLength() == 0)
                {
                    Out.Warn("  answered, but carries no order array. Top-level keys: "
                        + (doc.RootElement.ValueKind == JsonValueKind.Object
                            ? string.Join(", ", doc.RootElement.EnumerateObject().Select(p => p.Name))
                            : doc.RootElement.ValueKind.ToString()));
                    continue;
                }
                anyOk = true;
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var scalars = doc.RootElement.EnumerateObject()
                        .Where(pr => pr.Value.ValueKind is JsonValueKind.Number or JsonValueKind.String
                                        or JsonValueKind.True or JsonValueKind.False)
                        .Select(pr => $"{pr.Name}={pr.Value}").ToList();
                    if (scalars.Count > 0) Out.Info("  paging/meta: " + string.Join("  ", scalars));
                }
                Out.Info($"  {arr.GetArrayLength()} order(s) on this page. First order, in full:");
                Console.WriteLine(JsonSerializer.Serialize(arr[0], new JsonSerializerOptions { WriteIndented = true }));
                foreach (var o in arr.EnumerateArray()) CollectProductIds(o, dated);
            }
        }

        int owned = manifest.Current.Items.Count;
        Out.Heading($"dated {dated.Count} of {owned} owned products (PAGE 1 ONLY -- read the paging line above "
                  + "for how many more pages a one-time backfill would need)");
        if (!anyOk)
            Out.Warn("No order endpoint answered with orders. Acquisition dates are not available this way; "
                   + "this is a clear negative result, not a bug to chase.");
        return Continue;
    }

    private static JsonElement? FindArray(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.Array) return direct;
        foreach (var p in root.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Object
                && p.Value.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.Array)
                return nested;
        return null;
    }

    private static void CollectProductIds(JsonElement e, HashSet<long> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.NameEquals("id") || p.NameEquals("productId") || p.NameEquals("product_id"))
                    {
                        if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var n)) into.Add(n);
                        else if (p.Value.ValueKind == JsonValueKind.String && long.TryParse(p.Value.GetString(), out var s)) into.Add(s);
                    }
                    CollectProductIds(p.Value, into);
                }
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) CollectProductIds(x, into);
                break;
        }
    }
}

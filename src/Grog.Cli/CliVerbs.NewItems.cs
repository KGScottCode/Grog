// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
//
// (New items 09-09) The CLI half of the New flag: seeing what is flagged, backing exactly those up, and
// clearing them. Plus two DEV-ONLY verbs (GROG_DEV_VERBS=1, never in help): `mark-new`, the inverse of
// clear-new so a test can replay the flagged state, and `ordersprobe`, a research probe at GOG's order
// history -- the only place a real ACQUISITION DATE might come from.
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
    /// <summary>`clear-new [--all | &lt;title|id&gt;]` -- retire the New flag. Without --all a title or id is
    /// required: clearing the whole library is an explicit ask, never a bare verb's default.</summary>
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
            manifest.Mutate(m => n = NewItems.ClearAll(m));   // (manifest gate 09-08)
        }
        else
        {
            var item = manifest.Current.FindItem(q);
            if (item is null) { Out.Usage($"No item matched '{q}'."); return Continue; }
            manifest.Mutate(m => n = NewItems.Clear(m, new[] { item.GogId }));   // (manifest gate 09-08)
        }
        if (n > 0) await manifest.SaveAsync();
        Out.Success(n == 0 ? "Nothing was flagged New." : $"Cleared the New flag on {n} item(s).");
        return Continue;
    }

    /// <summary>DEV ONLY (`GROG_DEV_VERBS=1`): `mark-new [--all | &lt;id&gt;]`, the inverse of clear-new, so a
    /// test can put the library back into a flagged state without re-running a scan.</summary>
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
        });   // (manifest gate 09-08)
        if (n > 0) await manifest.SaveAsync();
        Out.Success($"Flagged {n} item(s) New.");
        return Continue;
    }

    /// <summary>DEV ONLY (`GROG_DEV_VERBS=1`): does GOG expose an order history we could date a purchase from?
    /// Tries the likely endpoints with the authenticated client and reports what each one answered -- the URL,
    /// the status, one whole order pretty-printed, and how many owned products the orders actually cover.
    /// A research tool: an endpoint that says "no" clearly is a useful answer, not a failure.</summary>
    internal static async Task<int> Ordersprobe(CliContext ctx)
    {
        var manifest = ctx.Manifest;
        await manifest.LoadAsync();
        var session = await ctx.Auth.EnsureAuthenticatedAsync(CancellationToken.None);

        // Most likely first: embed.gog.com's account order history (the page the website's "Orders" tab
        // reads). The v2 form is listed after it because it has moved before.
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
                // Find the array of orders whatever it is called; then print ONE whole order, verbatim,
                // because the point of a probe is to see the real shape rather than a summary of it.
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
                // The scalars beside the array answer the question that decides the design: is this one pull
                // or a page walk, and how many requests would a one-time backfill cost?
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

    /// <summary>The first array-valued property named <paramref name="name"/>, at the root or one level in.</summary>
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

    /// <summary>Every numeric-looking "id" anywhere under an order object -- the coverage line only needs to
    /// know WHICH products an order mentions, not how GOG nests them.</summary>
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

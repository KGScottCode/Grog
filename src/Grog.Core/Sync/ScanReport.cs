// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Grog.Core.Manifest;

namespace Grog.Core.Sync;

/// <summary>
/// The scan report: a plain-text diagnostic of what the last scan found, written beside Grog's other state
/// so it can be pasted into an issue. Order is deliberate: reconciliation, errors, no-download products,
/// then the ordinary library last. Unresolved entries carry the RAW account URL so the user can verify.
/// </summary>
public static class ScanReport
{
    public const string FileName = "scan-report.txt";

    /// <summary>The account record for one product, by id. Works without a slug or a title, which is what
    /// makes it usable for the very items Grog could learn nothing about.</summary>
    public static string RawUrl(long gogId) => $"https://embed.gog.com/account/gameDetails/{gogId}.json";

    public static string StoreUrl(string slug) => $"https://www.gog.com/en/game/{slug}";

    /// <summary>Build the report text. Pure: takes what the scan saw, returns the document.</summary>
    public static string Build(LibraryManifest manifest, int productsSeen, DateTimeOffset stamp,
                               IReadOnlyList<long>? failedIds = null)
    {
        failedIds ??= Array.Empty<long>();
        var items = manifest.Items;
        var excluded = manifest.Excluded;

        var sb = new StringBuilder();
        sb.AppendLine($"Grog scan report - {stamp.LocalDateTime:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"GOG listed {productsSeen} products for your account.");
        sb.AppendLine($"  {items.Count} can be backed up");
        sb.AppendLine($"  {excluded.Count} have nothing of their own to download");
        sb.AppendLine($"  {failedIds.Count} could not be read");
        sb.AppendLine();
        sb.AppendLine("These numbers add up to what GOG reports. Nothing is silently dropped.");
        sb.AppendLine();

        sb.AppendLine($"COULD NOT BE READ ({failedIds.Count})");
        if (failedIds.Count == 0) sb.AppendLine("  (none)");
        else
        {
            sb.AppendLine("  Grog could not understand GOG's answer for these. They are NOT backed up.");
            sb.AppendLine("  Open a URL below while signed in to GOG to see the raw record.");
            sb.AppendLine();
            foreach (var id in failedIds) sb.AppendLine($"  {id,-12} {RawUrl(id)}");
        }
        sb.AppendLine();

        sb.AppendLine($"NOTHING OF THEIR OWN TO DOWNLOAD ({excluded.Count})");
        if (excluded.Count == 0) sb.AppendLine("  (none)");
        else
        {
            sb.AppendLine("  GOG lists these, but they carry no files: DLC that ships inside a base game,");
            sb.AppendLine("  bundle and collection entries, and claims from platform promotions. Nothing here");
            sb.AppendLine("  is missing from your backup. Open a URL to see the empty download list yourself.");
            sb.AppendLine("  An entry marked UNAVAILABLE is different: GOG's servers failed to answer for it");
            sb.AppendLine("  this scan. That can be temporary - every rescan retries it automatically.");

            // When GOG names none of them, say it once up front instead of repeating "(no title)" per row.
            var ordered = excluded.OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList();
            bool anyNamed = ordered.Any(e => !string.IsNullOrWhiteSpace(e.Title));
            if (!anyNamed)
            {
                sb.AppendLine("  GOG returns no title for any of these ids, so they are listed by id alone.");
                sb.AppendLine();
                foreach (var e in ordered) sb.AppendLine($"  {e.GogId,-12} {RawUrl(e.GogId)}");
            }
            else
            {
                sb.AppendLine();
                foreach (var e in ordered)
                {
                    var name = string.IsNullOrWhiteSpace(e.Title) ? "(GOG gives no title for this id)" : e.Title;
                    sb.AppendLine($"  {e.GogId,-12} {name}");
                    if (!string.IsNullOrWhiteSpace(e.UnavailableReason))
                        sb.AppendLine($"               UNAVAILABLE: {e.UnavailableReason}");
                    sb.AppendLine($"               {RawUrl(e.GogId)}");
                    if (!string.IsNullOrWhiteSpace(e.Slug)) sb.AppendLine($"               {StoreUrl(e.Slug)}");
                    sb.AppendLine();   // one blank line between entries: two or three lines each, otherwise they run together
                }
            }
        }
        sb.AppendLine();

        sb.AppendLine($"CAN BE BACKED UP ({items.Count})");
        sb.AppendLine();
        foreach (var i in items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase))
        {
            long bytes = i.Files.Sum(f => Rollups.HeldBytesOf(f));
            sb.AppendLine($"  {i.Title,-52} {i.Files.Count,4} files  {Human(bytes),10}");
        }

        return sb.ToString();
    }

    /// <summary>Write the report next to Grog's other state and return its path. Never throws: a report that
    /// cannot be written must not fail a scan.</summary>
    public static string? Write(LibraryManifest manifest, int productsSeen, string configDir,
                                IReadOnlyList<long>? failedIds = null, DateTimeOffset? stamp = null)
    {
        try
        {
            Directory.CreateDirectory(configDir);
            var path = Path.Combine(configDir, FileName);
            File.WriteAllText(path, Build(manifest, productsSeen, stamp ?? DateTimeOffset.Now, failedIds));
            return path;
        }
        catch { return null; }
    }

    private static string Human(long b) => b <= 0 ? "-" : Format.ByteFormat.Size(b);
}

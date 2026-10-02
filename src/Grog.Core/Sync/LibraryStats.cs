using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Sync;

/// <summary>
/// Every library-level count and percentage, derived purely from the manifest for a given scope. The ONLY
/// source of dashboard numbers -- never compute them from ViewModel/grid collections.
/// </summary>
public sealed record LibraryStats(
    int TotalProducts,
    int BackupEligible,
    int NothingToBackUp,
    int BackedUp,
    int Missing,
    long DoneBytes,
    long TotalBytes)
{
    /// <summary>Byte-weighted completion across all backable products, 0..100.</summary>
    public double CompletePercent => TotalBytes > 0 ? 100.0 * DoneBytes / TotalBytes : 0;

    // Full-library magnitudes (independent of scope or download state): how much each category
    // accounts for, so the user can see the shape of the job and whether it will fit. Cloud saves
    // are excluded (they aren't part of the games/extras backup footprint).
    public int GameFileCount { get; init; }
    public int GameDoneFileCount { get; init; }
    public long GameSizeBytes { get; init; }
    public long GameDoneBytes { get; init; }
    public int ExtraFileCount { get; init; }
    public int ExtraDoneFileCount { get; init; }
    public long ExtraSizeBytes { get; init; }
    public long ExtraDoneBytes { get; init; }

    /// <summary>Extras broken down by GOG's extra type (soundtracks, wallpapers, manuals...), biggest
    /// first, capped with a trailing "Other" bucket absorbing the tail.</summary>
    public IReadOnlyList<ExtraTypeStat> ExtraTypes { get; init; } = Array.Empty<ExtraTypeStat>();

    public int AllFileCount => GameFileCount + ExtraFileCount;
    public int AllDoneFileCount => GameDoneFileCount + ExtraDoneFileCount;
    public long AllSizeBytes => GameSizeBytes + ExtraSizeBytes;
    public long AllDoneBytes => GameDoneBytes + ExtraDoneBytes;

    // THE HEALTH TALLY. The verdict and every vitals tile read these six facts. Rules: files are gated by
    // the FULL scope test so % can reach 100; Unavailable never enters a completeness number; legacy Movie
    // products leave the health figures when the caller hides them (includeLegacyMovies), while the
    // category magnitudes above stay movie-unfiltered and describe the whole backup on disk.
    /// <summary>Files the user has chosen to back up (full scope test), excluding Unavailable.</summary>
    public int InScopeFiles { get; init; }
    /// <summary>How many of <see cref="InScopeFiles"/> are on disk (present rule).</summary>
    public int InScopePresentFiles { get; init; }
    /// <summary>How many of the present files are MD5-verified.</summary>
    public int VerifiedFiles { get; init; }
    /// <summary>Bytes of the present in-scope files (local size preferred - what is actually held).</summary>
    public long PresentBytes { get; init; }
    /// <summary>Bytes of the in-scope files NOT held - the headline "unprotected" figure.</summary>
    public long GapBytes { get; init; }
    /// <summary>Newest per-item API refresh stamp across the whole catalog (staleness readouts).</summary>
    public DateTimeOffset? LastApiRefresh { get; init; }


    /// <summary>Products with backup work outstanding (missing/partial + updates).</summary>
    public int Pending => Missing;

    public static readonly LibraryStats Empty = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>A product is backable if GOG gives it any downloadable file (cloud saves don't count).
    /// Non-downloadable products are DLC folded into a base game, bundle shells, and delisted stubs.</summary>
    public static bool IsBackupEligible(LibraryItem item)
        => item.Files.Count > 0;

    public static LibraryStats From(LibraryManifest manifest, Scope scope, bool includeLegacyMovies = true)
    {
        var items = manifest.Items;
        int total = items.Count;
        var backable = items.Where(IsBackupEligible).ToList();

        int backedUp = 0, missing = 0;
        long done = 0, totalBytes = 0;
        int gameFiles = 0, extraFiles = 0, gameDoneFiles = 0, extraDoneFiles = 0;
        long gameBytes = 0, extraBytes = 0, gameDone = 0, extraDone = 0;
        int hbTotal = 0, hbPresent = 0, hbVerified = 0;
        long hbPresentBytes = 0, hbGapBytes = 0;
        DateTimeOffset? lastApi = null;
        foreach (var item in items)
            if (item.LastApiRefresh > (lastApi ?? DateTimeOffset.MinValue)) lastApi = item.LastApiRefresh;
        foreach (var item in backable)
        {
            // The health tally, gated by the FULL scope test (see the health block above for the rules).
            if (includeLegacyMovies || item.Type != ProductType.Movie)
                foreach (var f in item.Files)
                {
                    if (!scope.Includes(f)) continue;
                    if (f.State == FileState.Unavailable) continue;
                    hbTotal++;
                    if (BackupScope.IsPresent(f)) { hbPresent++; hbPresentBytes += Rollups.HeldBytesOf(f); }
                    else hbGapBytes += f.PlanSizeBytes ?? 0;   // the real size once a transfer has learned it, as the queue figures use
                    if (f.State == FileState.Verified) hbVerified++;
                }
            switch (BackupScope.Status(item, scope))
            {
                case BackupStatus.Complete: backedUp++; break;
                // Update-available triggers the same action as missing (download something), so it
                // counts as missing rather than a separate bucket. The row still shows an amber cue.
                default: missing++; break;   // NotDownloaded / Partial / Error / UpdateAvailable
            }
            done += BackupScope.ScopedDoneBytes(item, scope);
            totalBytes += BackupScope.ScopedTotalBytes(item, scope);

            // Full-library category magnitudes (all files, any state) + raw backed-up bytes (present on
            // disk, independent of scope), for the size breakdown.
            foreach (var f in item.Files)
            {
                // Excluded languages leave the denominator entirely, or 100% would be unreachable.
                if (!scope.IncludesLanguage(f)) continue;
                // A file GOG refuses to serve is not a gap you can close; it never enters a completeness number.
                if (f.State == FileState.Unavailable) continue;
                long sz = Rollups.TotalBytesOf(f);
                bool present = BackupScope.IsPresent(f);
                long doneSz = present ? sz : 0;
                if (f.Kind == FileKind.Extra) { extraFiles++; extraBytes += sz; extraDone += doneSz; if (present) extraDoneFiles++; }
                else { gameFiles++; gameBytes += sz; gameDone += doneSz; if (present) gameDoneFiles++; }
            }
        }

        return new LibraryStats(total, backable.Count, total - backable.Count, backedUp, missing, done, totalBytes)
        {
            GameFileCount = gameFiles,
            GameDoneFileCount = gameDoneFiles,
            GameSizeBytes = gameBytes,
            GameDoneBytes = gameDone,
            ExtraFileCount = extraFiles,
            ExtraDoneFileCount = extraDoneFiles,
            ExtraSizeBytes = extraBytes,
            ExtraDoneBytes = extraDone,
            ExtraTypes = BreakdownExtras(backable, scope),
            InScopeFiles = hbTotal,
            InScopePresentFiles = hbPresent,
            VerifiedFiles = hbVerified,
            PresentBytes = hbPresentBytes,
            GapBytes = hbGapBytes,
            LastApiRefresh = lastApi,
        };
    }

    /// <summary>Extras grouped into ExtraClassifier buckets, IN SCOPE only (language filter honored).
    /// Order is fixed so the legend never reshuffles; anything outside the named set collapses into "Other".</summary>
    private static IReadOnlyList<ExtraTypeStat> BreakdownExtras(List<LibraryItem> items, Scope scope)
    {
        var byBucket = new Dictionary<string, (long done, long total, int doneFiles, int files)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
            foreach (var f in item.Files)
            {
                if (f.Kind != FileKind.Extra) continue;
                if (!scope.Includes(f)) continue;          // excluded languages aren't part of the job
                if (f.State == FileState.Unavailable) continue;   // same rule as the row totals above
                var bucket = ExtraClassifier.Classify(f.Name, f.ExtraType);
                if (!Named.Contains(bucket)) bucket = ExtraClassifier.AddOns;   // the catch-all
                long sz = Rollups.TotalBytesOf(f);
                bool present = BackupScope.IsPresent(f);
                byBucket.TryGetValue(bucket, out var t);
                byBucket[bucket] = (t.done + (present ? sz : 0), t.total + sz,
                                    t.doneFiles + (present ? 1 : 0), t.files + 1);
            }

        var result = new List<ExtraTypeStat>();
        foreach (var name in DisplayOrder)
        {
            if (!byBucket.TryGetValue(name, out var t) || t.total <= 0) continue;   // nothing in scope -> not listed
            result.Add(new ExtraTypeStat(Label(name), t.done, t.total, t.doneFiles, t.files));
        }
        return result;
    }

    /// <summary>The buckets the dashboard names. Everything else folds into Add-ons ("Other").</summary>
    private static readonly string[] DisplayOrder =
    {
        ExtraClassifier.Localizations, ExtraClassifier.Soundtracks, ExtraClassifier.Video,
        ExtraClassifier.AlternateVersions, ExtraClassifier.Art, ExtraClassifier.GuidesAndDocs,
        ExtraClassifier.AddOns,   // "Other": level editors, stray audio, anything new GOG invents
    };
    private static readonly HashSet<string> Named = new(new[]
    {
        ExtraClassifier.Localizations, ExtraClassifier.Soundtracks, ExtraClassifier.Video,
        ExtraClassifier.AlternateVersions, ExtraClassifier.Art, ExtraClassifier.GuidesAndDocs,
    }, StringComparer.OrdinalIgnoreCase);

    /// <summary>Short labels: the legend has to fit on a line or two under the Extras bar.</summary>
    private static string Label(string bucket) => ExtraClassifier.ShortLabel(bucket);

    /// <summary>GOG's type keys are lowercase slugs ("wallpapers"); title-case them for display.</summary>
    private static string Pretty(string raw)
        => raw.Length == 0 ? raw : char.ToUpperInvariant(raw[0]) + raw[1..];
}

/// <summary>One extra type's magnitude and how much of it is backed up.</summary>
public sealed record ExtraTypeStat(string Name, long DoneBytes, long TotalBytes, int DoneFileCount, int FileCount)
{
    public double Percent => TotalBytes > 0 ? 100.0 * DoneBytes / TotalBytes : 0;
}

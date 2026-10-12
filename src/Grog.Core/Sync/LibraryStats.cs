using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Sync;

public sealed record LibraryStats(
    int TotalProducts,
    int BackupEligible,
    int NothingToBackUp,
    int BackedUp,
    int Missing,
    long DoneBytes,
    long TotalBytes)
{
    public double CompletePercent => TotalBytes > 0 ? 100.0 * DoneBytes / TotalBytes : 0;

    // Full-library
    public int GameFileCount { get; init; }
    public int GameDoneFileCount { get; init; }
    public long GameSizeBytes { get; init; }
    public long GameDoneBytes { get; init; }
    public int ExtraFileCount { get; init; }
    public int ExtraDoneFileCount { get; init; }
    public long ExtraSizeBytes { get; init; }
    public long ExtraDoneBytes { get; init; }

    public IReadOnlyList<ExtraTypeStat> ExtraTypes { get; init; } = Array.Empty<ExtraTypeStat>();

    public int AllFileCount => GameFileCount + ExtraFileCount;
    public int AllDoneFileCount => GameDoneFileCount + ExtraDoneFileCount;
    public long AllSizeBytes => GameSizeBytes + ExtraSizeBytes;
    public long AllDoneBytes => GameDoneBytes + ExtraDoneBytes;

    // HEALTH TALLY
    public int InScopeFiles { get; init; }
    public int InScopePresentFiles { get; init; }
    public int VerifiedFiles { get; init; }
    public long PresentBytes { get; init; }
    public long GapBytes { get; init; }
    public DateTimeOffset? LastApiRefresh { get; init; }


    public int Pending => Missing;

    public static readonly LibraryStats Empty = new(0, 0, 0, 0, 0, 0, 0);

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
                default: missing++; break;   // NotDownloaded / Partial / Error / UpdateAvailable
            }
            done += BackupScope.ScopedDoneBytes(item, scope);
            totalBytes += BackupScope.ScopedTotalBytes(item, scope);

            foreach (var f in item.Files)
            {
                if (!scope.IncludesLanguage(f)) continue;
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

    private static string Label(string bucket) => ExtraClassifier.ShortLabel(bucket);

    private static string Pretty(string raw)
        => raw.Length == 0 ? raw : char.ToUpperInvariant(raw[0]) + raw[1..];
}

public sealed record ExtraTypeStat(string Name, long DoneBytes, long TotalBytes, int DoneFileCount, int FileCount)
{
    public double Percent => TotalBytes > 0 ? 100.0 * DoneBytes / TotalBytes : 0;
}

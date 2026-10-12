// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Api;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Sync;

public sealed class LibrarySyncService
{
    private readonly GogApiClient _api;
    private readonly IManifestStore _manifest;
    public int MaxParallelism { get; init; } = 6;

    public string? DumpFailuresToDirectory { get; set; }

    public bool FetchProductDetails { get; set; }

    public string AccountId { get; set; } = "";

    public bool BaselineScan { get; set; }

    public bool HostOwnsReconcile { get; set; }

    public static int ApplyDelistedSweep(LibraryManifest manifest, IReadOnlyCollection<long> seenIds)
    {
        var registered = new HashSet<string>(manifest.Accounts.Select(a => a.Id));
        bool Answerable(LibraryItem item)
        {
            if (registered.Count == 0) return true;   // legacy single-account manifest: no tags to test
            var owners = new HashSet<string>(item.Files.SelectMany(f => f.OwnerIds));
            if (owners.Count == 0 || owners.Contains("")) return true;   // untagged / "" = primary
            return owners.Overlaps(registered);
        }

        int delisted = 0;
        foreach (var item in manifest.Items)
        {
            if (!Answerable(item))
            {
                item.IsDelisted = false;   // claim withdrawn; re-adding the account re-evaluates it
                continue;
            }
            bool seen = seenIds.Contains(item.GogId);
            if (!seen && item.Status != BackupStatus.Corrupt)
            {
                if (!item.IsDelisted) delisted++;
                item.IsDelisted = true;   // still owned locally; preserved, just flagged
            }
            else if (seen && item.IsDelisted)
            {
                item.IsDelisted = false;  // reappeared in the account
            }
        }
        return delisted;
    }

    public Func<long, CancellationToken, Task>? FetchArtAsync { get; set; }

    public void StashMeta(long gogId, IReadOnlyList<string> languages, IReadOnlyDictionary<string, int> ageRatings)
        => _stashedMeta[gogId] = (languages, ageRatings);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (IReadOnlyList<string> Languages, IReadOnlyDictionary<string, int> AgeRatings)> _stashedMeta = new();

    public event Action<SyncProgress>? Progress;

    public event Action<LibraryItem>? Discovered;

    public LibrarySyncService(GogApiClient api, IManifestStore manifest)
    {
        _api = api;
        _manifest = manifest;
    }

    public async Task<SyncResult> RunAsync(CancellationToken ct = default) => await RunAsync(null, ct);

    public async Task<SyncResult> RunAsync(IReadOnlyCollection<long>? onlyIds, CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var listing = await FetchListingAsync(ct);
        var result = new SyncResult { GamesSeen = listing.OwnedIds.Count, RichListingUnavailable = !listing.RichOk, ModCatalogError = listing.ModCatalogError };

        var targetIds = onlyIds is { Count: > 0 } ? listing.OwnedIds.Where(onlyIds.Contains).ToList() : listing.OwnedIds;
        var fetch = await FetchDetailsAsync(targetIds, ct);

        HashSet<long> seenIds;
        using (_manifest.Gate.Enter())
        {
            RecordExcluded(result, fetch, listing, onlyIds is { Count: > 0 } ? targetIds : null);
            seenIds = ApplyFetched(result, fetch, listing);
        }
        result.NoDetailsProducts = fetch.NoDetailIds.Count;
        result.NoDetailProductIds = fetch.NoDetailIds;
        result.ParseFailures = fetch.ParseFailures.Count;
        result.FailedProductIds = fetch.ParseFailures.Select(f => f.ProductId).ToList();   // parse only; fetch-failed live in Excluded
        result.FetchFailures = fetch.FetchFailedIds.Count;
        result.FetchFailReason = fetch.FetchFailReason;
        result.FetchFailedNames = fetch.FetchFailedIds
            .Select(id => listing.RichById.TryGetValue(id, out var row) && row.Title.Length > 0
                ? $"{row.Title} ({id})" : id.ToString())
            .ToList();

        if (FetchProductDetails) await EnrichProductDetailsAsync(ct);

        result.SeenIds.UnionWith(seenIds);
        using (_manifest.Gate.Enter())
        {
            if (!HostOwnsReconcile && onlyIds is not { Count: > 0 })
                result.Delisted = ApplyDelistedSweep(_manifest.Current, seenIds);

            _manifest.Current.LastSyncCompleted = DateTimeOffset.UtcNow;
            _manifest.Current.SyncLog.Insert(0, new SyncLogEntry
            {
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.UtcNow,
                GamesSeen = result.GamesSeen,
                Changes = result.NewGames + result.NewFiles + result.UpdatedFiles,
                Notes = $"new games {result.NewGames}, new files {result.NewFiles}, updated files {result.UpdatedFiles}, delisted {result.Delisted}, no-details {result.NoDetailsProducts}, parse failures {result.ParseFailures}",
            });
        }

        await _manifest.SaveAsync(ct);
        return result;
    }

    private sealed record Listing(IReadOnlyList<long> OwnedIds, IReadOnlyDictionary<long, Api.OwnedProduct> RichById,
                                  IReadOnlySet<long> ModIds, IReadOnlySet<string> ReferencedParentSlugs, bool RichOk,
                                  string? ModCatalogError);

    private async Task<Listing> FetchListingAsync(CancellationToken ct)
    {
        var ownedIds = await _api.GetOwnedProductIdsAsync(ct);
        IReadOnlyList<Api.OwnedProduct> richRows = System.Array.Empty<Api.OwnedProduct>();
        IReadOnlySet<long> modIds = new HashSet<long>();
        string? modCatalogError = null;
        bool richOk = true;
        try { richRows = await _api.GetLibraryProductsAsync(ct); } catch { richOk = false; }
        try { modIds = await _api.GetModTaggedProductIdsAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { modCatalogError = ex.Message; }   // mods classify as Game this scan; the summary says so
        var richById = richRows.ToDictionary(r => r.Id);
        var referencedParentSlugs = richRows
            .Where(r => r.IsChildOfPack)
            .Select(r => r.UrlSlug)
            .ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        return new Listing(ownedIds, richById, modIds, referencedParentSlugs, richOk, modCatalogError);
    }

    private sealed class FetchOutcome
    {
        public List<(long Id, GameDetails Details)> Fetched { get; } = new();
        public List<long> NoDetailIds { get; } = new();
        public List<GameDetailsParseException> ParseFailures { get; } = new();
        public List<long> FetchFailedIds { get; } = new();
        public string? FetchFailReason { get; set; }
    }

    private async Task<FetchOutcome> FetchDetailsAsync(IReadOnlyList<long> targetIds, CancellationToken ct)
    {
        var o = new FetchOutcome();
        using var throttle = new SemaphoreSlim(MaxParallelism);
        int done = 0;
        var progressTotal = targetIds.Count;
        var tasks = targetIds.Select(async id =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var details = await _api.GetGameDetailsAsync(id, ct);
                if (details is not null)
                {
                    lock (o.Fetched) o.Fetched.Add((id, details));
                    if (FetchArtAsync is not null)
                        try { await FetchArtAsync(id, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch { /* art is best-effort; a title still syncs without it */ }
                    // Preview for the UI
                    if (Discovered is { } onFound)
                        try { onFound(GameDetailsMapper.ToLibraryItem(id, details)); }
                        catch { /* a preview must never break a sync */ }
                }
                else
                {
                    // 404 or bare "[]"
                    lock (o.NoDetailIds) o.NoDetailIds.Add(id);
                }
            }
            catch (GameDetailsParseException pex)
            {
                // One malformed product must not abort the whole sync. Record and continue.
                lock (o.ParseFailures) o.ParseFailures.Add(pex);
                if (DumpFailuresToDirectory is { } dir)
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        await File.WriteAllTextAsync(Path.Combine(dir, $"parsefail_{id}.json"), pex.RawJson, ct);
                    }
                    catch { /* diagnostics are best-effort */ }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Auth.AuthExpiredException) { throw; }   // dead session: every later fetch fails too
            catch (Exception fex)
            {
                lock (o.FetchFailedIds) { o.FetchFailedIds.Add(id); o.FetchFailReason ??= fex.Message; }
            }
            finally
            {
                throttle.Release();
                var n = Interlocked.Increment(ref done);
                Progress?.Invoke(new SyncProgress(n, progressTotal));
            }
        });
        await Task.WhenAll(tasks);
        return o;
    }

    private static ExcludedProduct MakeExcluded(long id, Listing listing, string? unavailableReason)
    {
        listing.RichById.TryGetValue(id, out var row);
        bool referenced = row is not null && listing.ReferencedParentSlugs.Contains(row.Slug);
        return new ExcludedProduct
        {
            GogId = id,
            Title = row?.Title ?? "",
            Slug = row?.Slug ?? "",
            Category = row?.Category ?? "",
            Type = row is null ? Models.ProductType.Pack
                               : ProductTaxonomy.Classify(row, listing.ModIds, hasDownloads: false, referenced),
            IsChildOfPack = row?.IsChildOfPack ?? false,
            ReferencedAsParent = referenced,
            IsMovie = row?.IsMovie ?? false,
            DlcCount = row?.DlcCount ?? 0,
            UnavailableReason = unavailableReason ?? "",
        };
    }

    private const string FetchFailedReason =
        "GOG's servers could not return this product's details (it may be temporarily unavailable or no longer offered)";

    private void RecordExcluded(SyncResult result, FetchOutcome fetch, Listing listing, IReadOnlyCollection<long>? scopedTo = null)
    {
        result.ExcludedProducts = fetch.NoDetailIds
            .Select(id => MakeExcluded(id, listing, null))
            .OrderBy(e => e.Title, System.StringComparer.OrdinalIgnoreCase)
            .ToList();
        // FETCH-FAILED products join the excluded list
        result.ExcludedProducts.AddRange(fetch.FetchFailedIds.Select(id => MakeExcluded(id, listing, FetchFailedReason)));
        if (!listing.RichOk) result.ExcludedProducts = new List<ExcludedProduct>(_manifest.Current.Excluded);   // keep last known
        if (scopedTo is not null)
        {
            if (HostOwnsReconcile || !listing.RichOk) return;
            var asked = new HashSet<long>(scopedTo);
            _manifest.Current.Excluded = _manifest.Current.Excluded.Where(e => !asked.Contains(e.GogId))
                .Concat(result.ExcludedProducts.Where(e => asked.Contains(e.GogId)))
                .OrderBy(e => e.Title, System.StringComparer.OrdinalIgnoreCase).ToList();
            return;
        }
        if (!HostOwnsReconcile && listing.RichOk) _manifest.Current.Excluded = result.ExcludedProducts;
    }

    private HashSet<long> ApplyFetched(SyncResult result, FetchOutcome fetch, Listing listing)
    {
        _manifest.Gate.AssertHeld();   // canary: the caller holds the gate around the whole apply
        var existingById
 = _manifest.Current.Items.ToDictionary(i => i.GogId);
        var seenIds = new HashSet<long>();
        foreach (var (id, details) in fetch.Fetched)
        {
            seenIds.Add(id);
            var incoming = GameDetailsMapper.ToLibraryItem(id, details);
            Enrich(incoming, id, listing);

            if (existingById.TryGetValue(id, out var existing))
            {
                MergeInto(existing, incoming, result, AccountId);
                if (listing.RichOk) Enrich(existing, id, listing);   // no listing = no evidence to reclassify an existing item
                RecomputeStatus(existing);
            }
            else
            {
                foreach (var f in incoming.Files)
                {
                    if (!f.OwnerIds.Contains(AccountId)) f.OwnerIds.Add(AccountId);
                    MarkSizelessUnavailable(f);
                }
                incoming.IsNew = !BaselineScan;
                if (_stashedMeta.TryRemove(id, out var meta))
                {
                    if (meta.Languages.Count > 0) incoming.Languages = meta.Languages.ToList();
                    if (meta.AgeRatings.Count > 0) incoming.AgeRatings = new Dictionary<string, int>(meta.AgeRatings);
                }
                RecomputeStatus(incoming);
                _manifest.Current.Items.Add(incoming);
                _manifest.Current.ItemsChanged();
                result.NewGames++;
                result.NewGameIds.Add(incoming.GogId);
                result.NewFiles += incoming.Files.Count;
            }
        }

        // No-download products (packs / DLC shells) are intentionally NOT added as rows
        foreach (var id in fetch.NoDetailIds) seenIds.Add(id);
        foreach (var id in fetch.FetchFailedIds) seenIds.Add(id);
        foreach (var f in fetch.ParseFailures) seenIds.Add(f.ProductId);
        return seenIds;
    }

    private static void Enrich(LibraryItem item, long id, Listing listing)
    {
        if (listing.RichById.TryGetValue(id, out var row))
        {
            item.IsMovie = row.IsMovie;
            if (!string.IsNullOrEmpty(row.Image))
                item.ImageUrl = row.Image.StartsWith("//") ? "https:" + row.Image : row.Image;
            item.Category = string.IsNullOrEmpty(item.Category) ? row.Category : item.Category;
            item.ParentSlug = row.IsChildOfPack ? row.UrlSlug : "";
            bool referencedAsParent = listing.ReferencedParentSlugs.Contains(row.Slug);
            bool hasDownloads = item.Files.Count > 0;
            item.Type = ProductTaxonomy.Classify(row, listing.ModIds, hasDownloads, referencedAsParent);
        }
        else
        {
            item.Type = listing.ModIds.Contains(id) ? ProductType.Mod
                : item.Files.Count > 0 ? ProductType.Game
                : ProductType.Pack;
        }
    }

    private async Task EnrichProductDetailsAsync(CancellationToken ct)
    {
        // Snapshot the list
        foreach (var item in _manifest.Read(m => m.Items.ToList()))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var info = await _api.GetProductInfoAsync(item.GogId, ct);
                if (info is null) continue;
                using (_manifest.Gate.Enter())   // Languages is a list swap on the graph
                {
                    item.GameType = string.IsNullOrEmpty(info.GameType) ? item.GameType : info.GameType;
                    if (info.ReleaseDate is { } rd) item.ReleaseDate = rd;
                    item.InDevelopment = info.InDevelopment;
                    item.SupportsWindows = info.SupportsWindows;
                    item.SupportsMac = info.SupportsMac;
                    item.SupportsLinux = info.SupportsLinux;
                    if (info.Languages.Count > 0) item.Languages = info.Languages;
                    if (!string.IsNullOrEmpty(info.StoreUrl)) item.StorePageUrl = info.StoreUrl;
                }
            }
            catch { /* details are best-effort; never fail the sync over them */ }
        }

    }

    internal static void MergeIntoForTests(LibraryItem existing, LibraryItem incoming, string accountId = "")
        => MergeInto(existing, incoming, new SyncResult(), accountId);

    private static void MergeInto(LibraryItem existing, LibraryItem incoming, SyncResult result, string accountId)
    {
        existing.Title = incoming.Title;
        existing.Slug = incoming.Slug;
        existing.SerialKey = incoming.SerialKey;
        existing.LastApiRefresh = incoming.LastApiRefresh;

        // Duplicate-tolerant: GOG can emit two file objects that both lack a manualUrl
        var oldByKey = new Dictionary<string, GameFile>();
        foreach (var f in existing.Files) oldByKey.TryAdd(f.FileKey, f);
        var merged = new List<GameFile>();

        foreach (var inc in incoming.Files)
        {
            GameFile? old;
            if (!oldByKey.TryGetValue(inc.FileKey, out old))
            {
                old = existing.OldVersionFiles.FirstOrDefault(x => x.WithdrawnByGog && x.FileKey == inc.FileKey);
                if (old is not null) { existing.OldVersionFiles.Remove(old); old.IsOldVersion = false; old.WithdrawnByGog = false; }
            }
            if (old is not null)
            {
                // OWNERS MERGE
                inc.OwnerIds = old.OwnerIds;
                if (!inc.OwnerIds.Contains(accountId)) inc.OwnerIds.Add(accountId);
                // Same file slot
                bool versionChanged = !string.Equals(old.Version, inc.Version, StringComparison.Ordinal);
                inc.RootId = old.RootId;
                inc.LocalRelativePath = old.LocalRelativePath;
                inc.LocalSizeBytes = old.LocalSizeBytes;
                inc.ResolvedFileName = old.ResolvedFileName;
                inc.DownloadedAt = old.DownloadedAt;
                inc.LastVerifiedAt = old.LastVerifiedAt;
                inc.HasPartial = old.HasPartial;
                inc.PartialBytes = old.PartialBytes;
                inc.PartialRootId = old.PartialRootId;
                if (old.Version == inc.Version && old.ExpectedSizeBytes == inc.ExpectedSizeBytes) inc.WireSizeBytes = old.WireSizeBytes;
                inc.FailedAttempts = old.FailedAttempts;
                inc.ExpectedMd5 = versionChanged ? null : old.ExpectedMd5;

                if (versionChanged && BackupScope.IsPresent(old.State))
                {
                    inc.State = FileState.Outdated;
                    result.UpdatedFiles++;
                }
                else if (old.State == FileState.Unavailable)
                {
                    // SELF-HEAL
                    inc.State = FileState.NotBackedUp;
                    inc.UnavailableReason = null;
                }
                else
                {
                    inc.State = old.State;
                    inc.UnavailableReason = old.UnavailableReason;
                }
            }
            else
            {
                inc.State = FileState.NotBackedUp;
                if (!inc.OwnerIds.Contains(accountId)) inc.OwnerIds.Add(accountId);
                result.NewFiles++;
            }
            MarkSizelessUnavailable(inc);
            merged.Add(inc);
        }

        var keptKeys = new HashSet<string>(merged.Select(f => f.FileKey));
        foreach (var old in existing.Files)
        {
            if (keptKeys.Contains(old.FileKey) || !BackupScope.HoldsBytes(old) || old.State == FileState.Missing) continue;
            if (existing.OldVersionFiles.Any(x => x.FileKey == old.FileKey)) continue;
            old.IsOldVersion = true;
            old.WithdrawnByGog = true;
            existing.OldVersionFiles.Add(old);
            result.WithdrawnFiles++;
        }

        existing.Files = merged;
        existing.Dlcs = incoming.Dlcs; // DLC handled shallowly: replaced wholesale each sync
    }

    public const string NoSizeReason = "GOG lists no size for this file, so there is nothing to download.";
    internal static void MarkSizelessUnavailable(GameFile f)
    {
        if ((f.ExpectedSizeBytes ?? 0) > 0) return;
        if (BackupScope.HasLocalCopy(f) || f.State is FileState.Outdated or FileState.Corrupt or FileState.Missing) return;
        f.State = FileState.Unavailable;
        f.UnavailableReason = NoSizeReason;
    }

    public static void RecomputeStatus(LibraryItem item)
    {
        var files = item.Files.Where(BackupScope.CountsTowardCompleteness).ToList();
        item.Status = files.Count == 0 ? BackupStatus.Unknown : BackupScope.StatusOf(files);
    }
}

public sealed record SyncProgress(int Completed, int Total);

public sealed class SyncResult
{
    public static SyncResult Aggregate(System.Collections.Generic.IEnumerable<AccountSyncPass> passes)
    {
        var agg = new SyncResult();
        foreach (var p in passes)
        {
            if (p.Result is not { } r) continue;
            agg.GamesSeen += r.GamesSeen; agg.NewGames += r.NewGames; agg.NewFiles += r.NewFiles;
            agg.NewGameIds.AddRange(r.NewGameIds);
            agg.UpdatedFiles += r.UpdatedFiles; agg.NoDetailsProducts += r.NoDetailsProducts;
            agg.NoDetailProductIds.AddRange(r.NoDetailProductIds);
            agg.ParseFailures += r.ParseFailures; agg.FailedProductIds.AddRange(r.FailedProductIds);
            agg.FetchFailures += r.FetchFailures; agg.FetchFailReason ??= r.FetchFailReason;
            agg.FetchFailedNames.AddRange(r.FetchFailedNames);
            agg.Delisted += r.Delisted; agg.SeenIds.UnionWith(r.SeenIds);
            agg.ExcludedProducts.AddRange(r.ExcludedProducts);
            agg.RichListingUnavailable |= r.RichListingUnavailable;
            agg.ModCatalogError ??= r.ModCatalogError;
            agg.WithdrawnFiles += r.WithdrawnFiles;
        }
        // A product two accounts own was seen by both passes
        if (agg.SeenIds.Count > 0) agg.GamesSeen = agg.SeenIds.Count;
        agg.NoDetailProductIds = agg.NoDetailProductIds.Distinct().ToList();
        agg.NoDetailsProducts = agg.NoDetailProductIds.Count;
        return agg;
    }

    public int GamesSeen { get; set; }
    public bool RichListingUnavailable { get; set; }
    public string? ModCatalogError { get; set; }
    public System.Collections.Generic.HashSet<long> SeenIds { get; set; } = new();
    public System.Collections.Generic.List<ExcludedProduct> ExcludedProducts { get; set; } = new();
    public int NewGames { get; set; }
    public int WithdrawnFiles { get; set; }
    public System.Collections.Generic.List<long> NewGameIds { get; set; } = new();
    public int UpdatedFiles { get; set; }
    public int NewFiles { get; set; }
    public int Delisted { get; set; }
    public int NoDetailsProducts { get; set; }
    public System.Collections.Generic.List<long> NoDetailProductIds { get; set; } = new();
    public int ParseFailures { get; set; }
    public System.Collections.Generic.List<long> FailedProductIds { get; set; } = new();
    public int FetchFailures { get; set; }
    public string? FetchFailReason { get; set; }
    public System.Collections.Generic.List<string> FetchFailedNames { get; set; } = new();
}

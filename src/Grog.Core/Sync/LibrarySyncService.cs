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

/// <summary>
/// One "Sync Library" pass: fetch owned ids, fetch + flatten gameDetails (bounded parallelism), merge
/// into the manifest preserving local download state and flagging version changes, flag (never remove)
/// items the API stops listing, then recompute per-game BackupStatus, log, and save.
/// </summary>
public sealed class LibrarySyncService
{
    private readonly GogApiClient _api;
    private readonly IManifestStore _manifest;

    /// <summary>Max concurrent gameDetails requests. Keep low; we are guests on this API.</summary>
    // 6, up from 3 (owner call 09-01): scan wall time is ~products x RTT / parallelism, and the detail
    // calls are tiny -- doubling the overlap roughly halves a scan. The shared backoff gate still slows
    // everything politely if GOG answers 429, so the ceiling is self-correcting.
    public int MaxParallelism { get; init; } = 6;

    /// <summary>When set, the raw JSON of any product that fails to parse is written here
    /// (parsefail_{id}.json) for diagnosis. Null disables the dump.</summary>
    public string? DumpFailuresToDirectory { get; set; }

    /// <summary>When true, sync also fetches api.gog.com/products/{id} per game to capture
    /// release date, in-development flag, OS compat, languages, and store link. Costs one extra
    /// request per game, so it's opt-in (default sync stays fast). Fields are additive.</summary>
    public bool FetchProductDetails { get; set; }

    /// <summary>The account these items are synced from. Empty = default/single account.
    /// Stamped onto every item so multi-account merges can group/attribute.</summary>
    public string AccountId { get; set; } = "";

    /// <summary>(New items 09-09) The whole RUN started with an EMPTY library: this scan is the BASELINE
    /// and flags nothing New (owner 09-19; GOG's own `isNew` is not consulted). Set by the RUN's host, not
    /// per pass: account 2's pass already sees account 1's items, so a per-pass emptiness check is wrong.</summary>
    public bool BaselineScan { get; set; }

    /// <summary>The host owns whole-library reconciliation. REQUIRED when this service scans ONE account
    /// of several: the delisted sweep and the Excluded rebuild assume they saw the WHOLE library, so both
    /// are suppressed and surfaced on <see cref="SyncResult"/> for the host to union and apply ONCE.</summary>
    public bool HostOwnsReconcile { get; set; }

    /// <summary>The delisted sweep in one shared place: flag (never delete) every item NO scanned account
    /// was offered this run, and clear the flag on anything that reappeared. <paramref name="seenIds"/>
    /// must cover the WHOLE run -- one account's ids for a single-account sync, the union across accounts
    /// for a multi-account run. Corrupt items are left alone (their red means something else already).
    /// An item owned ONLY by accounts no longer registered is UNANSWERABLE: no connected account can ask
    /// GOG about it, so "unseen" says nothing -- it is never flagged, and a stale flag is withdrawn
    /// (owner-hit 2026-08-30: removing an account painted its 15 games "delisted by GOG", all false).
    /// Returns how many items were newly flagged.</summary>
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

    /// <summary>Optional per-product art hook (App supplies it; Core stays UI- and path-agnostic).
    /// Runs for each game AFTER its details are fetched and BEFORE the progress tick, so one tick means
    /// "this title's data and its art are both done." Best-effort: the sync ignores any art failure.</summary>
    public Func<long, CancellationToken, Task>? FetchArtAsync { get; set; }

    /// <summary>(09-19, walk pass 3) Store-page metadata (languages, age ratings) fetched for a product that is
    /// NOT IN THE LIBRARY YET. The art hook runs during the fetch phase, before the merge creates the item, so on
    /// a first scan the host had no item to write to and threw the metadata away for EVERY game: "Hide mature
    /// content" had nothing to act on and the detail pane showed no ratings until a second scan. The host stashes
    /// it here; the merge puts it on the new item.</summary>
    public void StashMeta(long gogId, IReadOnlyList<string> languages, IReadOnlyDictionary<string, int> ageRatings)
        => _stashedMeta[gogId] = (languages, ageRatings);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (IReadOnlyList<string> Languages, IReadOnlyDictionary<string, int> AgeRatings)> _stashedMeta = new();

    public event Action<SyncProgress>? Progress;

    /// <summary>Raised as each product's details arrive, with the mapped item. DISPLAY ONLY: the manifest
    /// is still written once at the end, so a canceled scan leaves nothing behind and rows built from
    /// this event are throwaway previews.</summary>
    public event Action<LibraryItem>? Discovered;

    public LibrarySyncService(GogApiClient api, IManifestStore manifest)
    {
        _api = api;
        _manifest = manifest;
    }

    public async Task<SyncResult> RunAsync(CancellationToken ct = default) => await RunAsync(null, ct);

    /// <summary>Sync the library. When <paramref name="onlyIds"/> is given, only those products are
    /// refreshed (a scoped "check selected") -- the expensive per-product detail fetch is limited to
    /// them, and the delisting pass is skipped so unchecked items aren't wrongly flagged.</summary>
    /// <summary>One scan, in stages: the listing (owned ids, rich rows, mod tags), the parallel detail
    /// fetch, the excluded list, the merge into the manifest, optional product enrichment, then the
    /// delisted sweep and the log entry. Was one 265-line method until 09-02; each stage is below.</summary>
    public async Task<SyncResult> RunAsync(IReadOnlyCollection<long>? onlyIds, CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var listing = await FetchListingAsync(ct);
        var result = new SyncResult { GamesSeen = listing.OwnedIds.Count, RichListingUnavailable = !listing.RichOk };

        var targetIds = onlyIds is { Count: > 0 } ? listing.OwnedIds.Where(onlyIds.Contains).ToList() : listing.OwnedIds;
        var fetch = await FetchDetailsAsync(targetIds, ct);

        // The merge is the scan's write to the graph: under the manifest gate (09-08), so the App's reads,
        // the settler and a concurrent save see either the old library or the new one, never a half-merge.
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
        // Name the casualties for the log: "Neverwinter Nights Diamond (1207658890)", never a bare id.
        result.FetchFailedNames = fetch.FetchFailedIds
            .Select(id => listing.RichById.TryGetValue(id, out var row) && row.Title.Length > 0
                ? $"{row.Title} ({id})" : id.ToString())
            .ToList();

        if (FetchProductDetails) await EnrichProductDetailsAsync(ct);

        // Flag (never delete) anything the API no longer lists. Skip entirely on a scoped check --
        // we only looked at a subset, so "unseen" says nothing about the rest. Also skipped when the
        // host owns the sweep (multi-account: one sweep over the union of all scanned accounts).
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

    /// <summary>What GOG says the account owns and how it classifies it: the owned id list, the rich library
    /// rows (title/slug/flags/parent url), the mod-tag set, and the slugs some child row names as its pack.
    /// Rich rows and mod tags are best-effort: without them items still sync and default to Game.</summary>
    private sealed record Listing(IReadOnlyList<long> OwnedIds, IReadOnlyDictionary<long, Api.OwnedProduct> RichById,
                                  IReadOnlySet<long> ModIds, IReadOnlySet<string> ReferencedParentSlugs, bool RichOk);

    private async Task<Listing> FetchListingAsync(CancellationToken ct)
    {
        var ownedIds = await _api.GetOwnedProductIdsAsync(ct);
        IReadOnlyList<Api.OwnedProduct> richRows = System.Array.Empty<Api.OwnedProduct>();
        IReadOnlySet<long> modIds = new HashSet<long>();
        bool richOk = true;
        try { richRows = await _api.GetLibraryProductsAsync(ct); } catch { richOk = false; }
        try { modIds = await _api.GetModTaggedProductIdsAsync(ct); } catch { /* mods default to Game; recoverable */ }
        // Without the rich listing every product would be reclassified from nothing (Type rewritten,
        // ParentSlug blanked, Excluded rebuilt with empty titles). Existing items therefore KEEP their
        // taxonomy this run and the excluded list is left as it was; the result says so.
        var richById = richRows.ToDictionary(r => r.Id);
        // Slugs referenced as a PARENT by some child row -> those parents are packs.
        var referencedParentSlugs = richRows
            .Where(r => r.IsChildOfPack)
            .Select(r => r.UrlSlug)
            .ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        return new Listing(ownedIds, richById, modIds, referencedParentSlugs, richOk);
    }

    /// <summary>The parallel gameDetails pass, sorted into its four outcomes. Nothing here touches the
    /// manifest; a failure of one product is recorded and the rest continue.</summary>
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
        // The progress denominator is what we will actually fetch (targetIds), not the whole owned library --
        // otherwise a scoped "check selected" run tops out at selectedCount/ownedCount and never reads 100%.
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
                    // ART FIRST, THEN THE PREVIEW ROW, so a row publishes ONCE, finished. Art is best-effort
                    // and bounded by the same throttle, so a slow image delays one row, never the scan.
                    if (FetchArtAsync is not null)
                        try { await FetchArtAsync(id, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch { /* art is best-effort; a title still syncs without it */ }
                    // Preview for the UI. Mapped here because the mapper is pure and the caller wants to see
                    // the library fill as it arrives; nothing is stored until the pass below.
                    if (Discovered is { } onFound)
                        try { onFound(GameDetailsMapper.ToLibraryItem(id, details)); }
                        catch { /* a preview must never break a sync */ }
                }
                else
                {
                    // 404 or bare "[]" -- owned but no downloadable details (DLC under a base
                    // game, bonus items). Recorded, not treated as an error.
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
                // Same rule as parse failures (owner-hit 09-01: ONE product 500ing at 157/158 aborted the
                // whole scan and threw away 157 fetched products): record, continue, report at the end.
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

    /// <summary>One excluded-product record, whichever bucket it comes from. The two buckets differ only by
    /// their reason: a 404/empty is GOG's DELIBERATE "nothing to download"; an HTTP error is GOG failing to
    /// answer -- possibly transient, so the list being rebuilt each scan self-heals (the product appears in
    /// the library the moment GOG serves it again).</summary>
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

    /// <summary>Persist the excluded products with the signals that classified them. Rebuilt every scan (never
    /// merged) so it cannot drift from what GOG last said; multi-account hosts rebuild the union instead.</summary>
    /// <para><paramref name="scopedTo"/> (a "check selected" pass): this scan asked about THOSE ids only, so it
    /// may only replace THEIR entries. Rebuilding the whole list from a subset wiped every other excluded
    /// product until the next full scan (sweep 2 #14).</para>
    private void RecordExcluded(SyncResult result, FetchOutcome fetch, Listing listing, IReadOnlyCollection<long>? scopedTo = null)
    {
        result.ExcludedProducts = fetch.NoDetailIds
            .Select(id => MakeExcluded(id, listing, null))
            .OrderBy(e => e.Title, System.StringComparer.OrdinalIgnoreCase)
            .ToList();
        // FETCH-FAILED products join the excluded list with their own reason (owner call 09-01, NWN
        // Diamond: a delisted promo claim whose gameDetails 500s server-side).
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

    /// <summary>Merge every fetched product into the manifest (new items are owned by the scanning account)
    /// and return the set of ids this scan has proof of: fetched, no-detail AND failed, because all of them
    /// came from the account's OWNED list, so a failed detail call must never narrow owners or flag a game
    /// delisted. Only genuinely-unlisted products stay unseen.</summary>
    private HashSet<long> ApplyFetched(SyncResult result, FetchOutcome fetch, Listing listing)
    {
        _manifest.Gate.AssertHeld();   // canary (manifest gate 09-08): the caller holds the gate around the whole apply
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
                // A brand-new item's files are owned by the account this scan runs as.
                // (09-19) The sizeless rule runs here too: MergeInto was its only caller, so on the FIRST scan
                // (every item is brand new) a file GOG lists with no size was queued as "Size unknown".
                foreach (var f in incoming.Files)
                {
                    if (!f.OwnerIds.Contains(AccountId)) f.OwnerIds.Add(AccountId);
                    MarkSizelessUnavailable(f);
                }
                // (New items 09-09) The one place newness is decided: the first scan of an empty library
                // is the baseline and flags nothing; every later scan flags what the manifest had never seen.
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

        // No-download products (packs / DLC shells) are intentionally NOT added as rows: packs are a
        // store construct and DLC installers roll into the parent game's download, so neither is a
        // separately backable artifact. We still record them as seen so reconcile doesn't treat them as
        // delisted, but we don't create file-less library entries for them.
        foreach (var id in fetch.NoDetailIds) seenIds.Add(id);
        foreach (var id in fetch.FetchFailedIds) seenIds.Add(id);
        foreach (var f in fetch.ParseFailures) seenIds.Add(f.ProductId);
        return seenIds;
    }

    /// <summary>Taxonomy from the rich listing (type, movie flag, art, category, pack parent); without a
    /// rich row, infer minimally: the mod set can still apply, else Game if it has files, else Pack.</summary>
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

    /// <summary>Opt-in per-game product detail capture (one extra call each). Done after the main pass so it
    /// never blocks the core sync; failures are ignored (fields just stay default).</summary>
    private async Task EnrichProductDetailsAsync(CancellationToken ct)
    {
        // Snapshot the list: the fetch below awaits, and Items must not be enumerated across it.
        foreach (var item in _manifest.Read(m => m.Items.ToList()))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var info = await _api.GetProductInfoAsync(item.GogId, ct);
                if (info is null) continue;
                using (_manifest.Gate.Enter())   // (manifest gate 09-08) Languages is a list swap on the graph
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

    /// <summary>Test seam for MergeInto's per-file state rules (update detection, local-state
    /// carry-forward, the Unavailable self-heal) without standing up a whole sync run.</summary>
    internal static void MergeIntoForTests(LibraryItem existing, LibraryItem incoming, string accountId = "")
        => MergeInto(existing, incoming, new SyncResult(), accountId);

    private static void MergeInto(LibraryItem existing, LibraryItem incoming, SyncResult result, string accountId)
    {
        existing.Title = incoming.Title;
        existing.Slug = incoming.Slug;
        existing.SerialKey = incoming.SerialKey;
        existing.LastApiRefresh = incoming.LastApiRefresh;

        // Duplicate-tolerant: GOG can emit two file objects that both lack a manualUrl, so FileKey (derived
        // from it) can collide. First-wins keeps local state for the slot rather than throwing on a plain
        // ToDictionary and aborting the whole sync.
        var oldByKey = new Dictionary<string, GameFile>();
        foreach (var f in existing.Files) oldByKey.TryAdd(f.FileKey, f);
        var merged = new List<GameFile>();

        foreach (var inc in incoming.Files)
        {
            GameFile? old;
            if (!oldByKey.TryGetValue(inc.FileKey, out old))
            {
                // A slot GOG withdrew and now re-lists comes back from the archive list with its local state.
                old = existing.OldVersionFiles.FirstOrDefault(x => x.WithdrawnByGog && x.FileKey == inc.FileKey);
                if (old is not null) { existing.OldVersionFiles.Remove(old); old.IsOldVersion = false; old.WithdrawnByGog = false; }
            }
            if (old is not null)
            {
                // OWNERS MERGE: a matching scanned file ADDS the scanning account to its owners; existing
                // owners carry forward -- another account's entitlement is not this scan's to revoke.
                // A file GOG stops offering disappears from `incoming`, so removal falls out of the replace.
                inc.OwnerIds = old.OwnerIds;
                if (!inc.OwnerIds.Contains(accountId)) inc.OwnerIds.Add(accountId);
                // Same file slot. If the version string changed and we had a local copy,
                // flag it as needing an update; otherwise keep the local state as-is.
                bool versionChanged = !string.Equals(old.Version, inc.Version, StringComparison.Ordinal);

                // Carry ALL local/on-disk state forward: `inc` came from the API feed, which knows nothing
                // about locations, hashes, or partial-resume state. Local truth stays; only API metadata refreshes.
                inc.RootId = old.RootId;
                inc.LocalRelativePath = old.LocalRelativePath;
                inc.LocalSizeBytes = old.LocalSizeBytes;
                inc.ResolvedFileName = old.ResolvedFileName;
                inc.DownloadedAt = old.DownloadedAt;
                inc.LastVerifiedAt = old.LastVerifiedAt;
                inc.HasPartial = old.HasPartial;
                inc.PartialBytes = old.PartialBytes;
                inc.PartialRootId = old.PartialRootId;
                // The real length stands while GOG describes the same build with the same label.
                if (old.Version == inc.Version && old.ExpectedSizeBytes == inc.ExpectedSizeBytes) inc.WireSizeBytes = old.WireSizeBytes;
                inc.FailedAttempts = old.FailedAttempts;
                // The integrity hash describes the bytes of the version we HAVE. Keep it while the version is
                // unchanged; drop it on a version change (the old hash no longer matches the new build, and the
                // new one is re-captured when the update downloads).
                inc.ExpectedMd5 = versionChanged ? null : old.ExpectedMd5;

                if (versionChanged && BackupScope.IsPresent(old.State))
                {
                    inc.State = FileState.Outdated;
                    result.UpdatedFiles++;
                }
                else if (old.State == FileState.Unavailable)
                {
                    // SELF-HEAL: sync is where GOG's view of the library refreshes, so Unavailable clears
                    // here and the file re-enters the normal path; if GOG still refuses, the next attempt
                    // re-flags it at the cost of one request per sync.
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

        // A slot GOG withdrew while we hold its bytes: keep the record in the archive list so the copy on disk
        // stays counted, movable and deletable. Nothing is deleted here; a slot with no bytes (or whose bytes
        // Verify already found gone) just goes.
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

    /// <summary>A file GOG lists with NO size (blank, or "0 MB") is nothing to download (owner, 09-13). It is
    /// treated exactly like a file GOG refuses to serve: <see cref="FileState.Unavailable"/>, so it is never
    /// queued, never enters a completeness number, and shows in the "can't be downloaded" list with its
    /// reason. Until then it sat in the queue as "Size unknown" and sorted as the smallest file. Sync
    /// re-evaluates it every pass: a size that appears later clears the flag (the Unavailable self-heal above
    /// runs first, then this re-flags only what is still sizeless). A copy we already HOLD is left alone.</summary>
    public const string NoSizeReason = "GOG lists no size for this file, so there is nothing to download.";
    internal static void MarkSizelessUnavailable(GameFile f)
    {
        if ((f.ExpectedSizeBytes ?? 0) > 0) return;
        if (BackupScope.HasLocalCopy(f) || f.State is FileState.Outdated or FileState.Corrupt or FileState.Missing) return;
        f.State = FileState.Unavailable;
        f.UnavailableReason = NoSizeReason;
    }

    /// <summary>Rolls per-file states up into the game's persisted traffic-light status. Scope-blind on
    /// purpose (the objective rollup the manifest stores); Unavailable files are excluded as in completeness.
    /// The rollup logic itself lives in BackupScope.StatusOf -- never a private copy.</summary>
    public static void RecomputeStatus(LibraryItem item)
    {
        var files = item.Files.Where(BackupScope.CountsTowardCompleteness).ToList();
        item.Status = files.Count == 0 ? BackupStatus.Unknown : BackupScope.StatusOf(files);
    }
}

public sealed record SyncProgress(int Completed, int Total);

public sealed class SyncResult
{
    /// <summary>The sum over a multi-account run's passes, every field. The App and CLI each kept a
    /// hand-rolled copy before 09-02 and the CLI's dropped five fields (it reported 0 fetch failures).</summary>
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
            agg.WithdrawnFiles += r.WithdrawnFiles;
        }
        // A product two accounts own was seen by both passes: the RUN saw it once. Summed, a 100-game library
        // shared by two accounts reported "200 products" and the "N non-downloadable" readout doubled with it
        // (sweep 2 #15). SeenIds already is the union; the no-detail ids are made one the same way.
        if (agg.SeenIds.Count > 0) agg.GamesSeen = agg.SeenIds.Count;
        agg.NoDetailProductIds = agg.NoDetailProductIds.Distinct().ToList();
        agg.NoDetailsProducts = agg.NoDetailProductIds.Count;
        return agg;
    }

    public int GamesSeen { get; set; }
    /// <summary>The rich library listing (titles/slugs/parent links) could not be fetched this run, so
    /// taxonomy and the excluded list were carried over rather than recomputed from nothing.</summary>
    public bool RichListingUnavailable { get; set; }
    /// <summary>Every product id this scan saw (offered to the scanned account). Multi-account hosts
    /// union these across accounts and run the delisted sweep once over the whole run.</summary>
    public System.Collections.Generic.HashSet<long> SeenIds { get; set; } = new();
    /// <summary>This scan's excluded (no-downloads-of-their-own) products. Multi-account hosts union
    /// these across accounts and write manifest.Excluded once for the run.</summary>
    public System.Collections.Generic.List<ExcludedProduct> ExcludedProducts { get; set; } = new();
    public int NewGames { get; set; }
    /// <summary>File slots GOG stopped listing while we held their bytes; their records moved to the archive list.</summary>
    public int WithdrawnFiles { get; set; }
    /// <summary>Ids of the games this scan added (not seen in the manifest before). The scan summary's
    /// "N new games since your last scan" line and its Back Up New button run off this list.</summary>
    public System.Collections.Generic.List<long> NewGameIds { get; set; } = new();
    public int UpdatedFiles { get; set; }
    public int NewFiles { get; set; }
    public int Delisted { get; set; }
    public int NoDetailsProducts { get; set; }
    public System.Collections.Generic.List<long> NoDetailProductIds { get; set; } = new();
    public int ParseFailures { get; set; }
    public System.Collections.Generic.List<long> FailedProductIds { get; set; } = new();
    /// <summary>Products whose detail fetch failed outright (HTTP error after backoff). Recorded per
    /// product so one flaky title never aborts a scan; ids are also in FailedProductIds.</summary>
    public int FetchFailures { get; set; }
    /// <summary>First fetch-failure message, for the scan summary's one-line "why".</summary>
    public string? FetchFailReason { get; set; }
    /// <summary>"Title (id)" per fetch-failed product, from the library listing's rich rows.</summary>
    public System.Collections.Generic.List<string> FetchFailedNames { get; set; } = new();
}

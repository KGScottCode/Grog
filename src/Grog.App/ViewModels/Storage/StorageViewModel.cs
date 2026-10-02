// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Download;
using Grog.Core.Format;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Verify;
using Grog.App.Runtime;

namespace Grog.App.ViewModels;

/// <summary>
/// The Storage page's view model (S3.1, 09-08): the backup locations and their capacity rows, the roots
/// (add / change / remove / re-point / make primary / lost), the import wizard, the layout migration and
/// the move jobs with their Activity pane, the two device grids, and the live volume watch. Split into
/// partials by those topics. It owns no manifest or engine state: that is the session's; what it still
/// needs from the shell (log, toasts, busy, navigation, the dashboard / health passes, the library scope)
/// it reaches through <c>_root</c>'s small internal surface, which S3.4 will retire.
/// </summary>
public sealed partial class StorageViewModel : ObservableObject
{
    private readonly MainWindowViewModel _root;
    private readonly GrogSession _session;

    public StorageViewModel(MainWindowViewModel root, GrogSession session)
    {
        _root = root;
        _session = session;
    }

    /// <summary>A primary backup folder is bound. Raised with <see cref="RaiseLocationState"/> whenever the
    /// root changes (bind, first run, DEV reset, re-point).</summary>
    public bool HasLocation => !string.IsNullOrEmpty(_session.BackupRoot);

    /// <summary>The HasLocation family: the shell calls this after every change of the backup root.</summary>
    internal void RaiseLocationState()
    {
        OnPropertyChanged(nameof(HasLocation));
        OnPropertyChanged(nameof(CanAddSecondary));
        OnPropertyChanged(nameof(PrimaryButtonText));
        OnPropertyChanged(nameof(ShowSystemDriveHint)); OnPropertyChanged(nameof(SystemDriveHintText));
    }

    // ---- Backup Locations screen (per-location capacity + add/remove) ----
    public ObservableCollection<BackupLocationRow> BackupLocations { get; } = new();

    /// <summary>An update archived a superseded build (KeepOldVersions on): record it in OldVersionFiles so
    /// it stays tracked. Raised on a worker thread, so marshal to the UI thread; idempotent by FileKey.</summary>
    internal void OnOldVersionArchived(Grog.Core.Models.GameFile entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = _root.Library.ItemById(entry.GameGogId);
            if (item is null) return;
            using (_session.Manifest!.Gate.Enter())   // (manifest gate 09-08)
            {
                // Core records it first now (BackupRun, 09-19); either way the page still saves and refreshes.
                if (!item.OldVersionFiles.Any(f => f.FileKey == entry.FileKey)) item.OldVersionFiles.Add(entry);
            }
            Runtime.ManifestSaves.Default.SaveInBackground(_session.Manifest, "old version archived");

            if (_root.ShowBackupLocations) RefreshBackupLocations();
        });
    }

    /// <summary>Everything the storage rows need from the library for ONE root, gathered by a single pass over
    /// Items x Files in RefreshBackupLocationsCore (UI-thread sweep 09-06): it used to be ~5 separate walks per
    /// root per 1 Hz tick (drive usage, file count, composition, coverage, health tally).</summary>
    private sealed class RootAgg
    {
        /// <summary>Bytes present per BackupScope.IsPresent (the "Grog" share of the drive), partials excluded.</summary>
        public long GrogBytes;
        /// <summary>Files with a local copy, superseded builds included.</summary>
        public int FileCount;
        /// <summary>Bytes on disk by content bucket (composition strip), old versions as their own slice.</summary>
        public readonly Dictionary<string, long> Buckets = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Coverage: in-scope files/bytes landed here, and games with any such file.</summary>
        public int HereN; public long HereB; public int HereGames;
    }

    private IReadOnlyList<ExtraTypeSegment> CompositionFrom(Dictionary<string, long> byBucket)
    {
        long total = byBucket.Values.Sum();
        if (total <= 0) return Array.Empty<ExtraTypeSegment>();
        var list = new List<ExtraTypeSegment>();
        foreach (var kv in byBucket.OrderByDescending(k => k.Value))
            list.Add(new ExtraTypeSegment(kv.Key, ExtraTypeSegment.ColorFor(kv.Key))
                { Col = new Avalonia.Controls.GridLength(100.0 * kv.Value / total, Avalonia.Controls.GridUnitType.Star) });
        return list;
    }

    /// <summary>Two rows say the same thing: every displayed value equal, composition compared segment by
    /// segment. The record's own equality is useless here because Composition is a list reference.</summary>
    // (1282) The one verify that can run at a time, for the card's "Verifying N / M" word. Progress arrives per file
    // from a pool thread; the card refresh is coalesced to twice a second.
    private string? _verifyingRootId; private int _verifyDone, _verifyTotal;
    private Services.UiCoalescer? _verifyRefresh;
    internal void SetVerifyProgress(string? rootId, int done, int total)
    {
        _verifyingRootId = rootId; _verifyDone = done; _verifyTotal = total;
        (_verifyRefresh ??= new Services.UiCoalescer(TimeSpan.FromMilliseconds(500), () => RefreshBackupLocations())).Request();
    }

    private static bool SameRow(BackupLocationRow a, BackupLocationRow b)
    {
        if (a.RootId != b.RootId || a.Label != b.Label || a.Path != b.Path || a.IsPrimary != b.IsPrimary
            || a.IsOnline != b.IsOnline || a.GrogBytes != b.GrogBytes || a.FreeBytes != b.FreeBytes
            || a.TotalBytes != b.TotalBytes || a.FileCount != b.FileCount || a.FitColor != b.FitColor
            || a.HoldsGames != b.HoldsGames || a.HoldsExtras != b.HoldsExtras
            || a.QueuedBytes != b.QueuedBytes || a.HasUnplaceableQueue != b.HasUnplaceableQueue
            || a.UnplaceableBytes != b.UnplaceableBytes || a.UnplaceableCount != b.UnplaceableCount
            || a.PolicyLine != b.PolicyLine || a.IsViewing != b.IsViewing || a.Removable != b.Removable
            || a.JustReattached != b.JustReattached || a.StatusKnown != b.StatusKnown
            || a.CoverageLine != b.CoverageLine || a.VerifyText != b.VerifyText
            || a.Name != b.Name || a.ShowName != b.ShowName) return false;   // (09-15) a rename must replace the row
        if (a.Composition.Count != b.Composition.Count) return false;
        for (int i = 0; i < a.Composition.Count; i++) if (a.Composition[i] != b.Composition[i]) return false;
        return true;
    }

    /// <summary>Where new files land. Not a rule with exceptions -- moving is always allowed.</summary>
    private string PolicyFor(string rootId, bool isPrimary)
    {
        // Purpose is not a field on the root: it derives from which roles are pinned to it.
        var roles = _session.Manifest?.Current.Routing.RoleRoots ?? new Dictionary<Grog.Core.Models.ContentRole, string>();
        bool extrasHere = roles.TryGetValue(Grog.Core.Models.ContentRole.Extras, out var er) && er == rootId;
        bool gamesHere = roles.TryGetValue(Grog.Core.Models.ContentRole.Games, out var gr) && gr == rootId;

        if (extrasHere && gamesHere) return "Everything lands here";
        if (extrasHere) return "Extras land here";
        if (gamesHere) return "Games land here";
        // Nothing pinned: the primary is the default target, a secondary just catches the spill.
        return isPrimary ? "Everything lands here" : "Overflow lands here";
    }

    /// <summary>(UX 09-08 #6) Storage rail second line: "{n} drive(s) · healthy|attention". Attention = any
    /// location whose dot is not green (offline fixed drive, unplugged removable, or status not yet known).
    /// The dots stay beside it: the text carries two states, the dots four.</summary>
    public string RailStorageSub
    {
        get
        {
            int n = BackupLocations.Count;
            if (n == 0) return "no storage yet";
            if (WaitingDrives.Length > 0) return $"waiting for {WaitingDrives}";   // (09-25) a drive slow to answer
            // (Rail exceptions 09-09) "healthy" is gone: green is assumed and never reported, and the dots
            // beside this line already carry connectivity. Trouble states now get their own line below.
            return $"{n} drive{(n == 1 ? "" : "s")}";
        }
    }

    /// <summary>(Rail exceptions 09-09) Storage's outstanding items. A drive whose status is not yet known
    /// is NOT reported: "unknown" is not a fault, and saying so on every launch would cry wolf.</summary>
    public IReadOnlyList<RailException> RailStorageExceptions
    {
        get
        {
            int off = BackupLocations.Count(r => r.StatusKnown && !r.IsOnline);
            return off == 0 ? System.Array.Empty<RailException>()
                            : new[] { new RailException($"{off} drive{(off == 1 ? "" : "s")} offline", Grog.Core.Sync.Severity.Fault) };
        }
    }

    private void RaiseLocationButtons()
    {
        OnPropertyChanged(nameof(RailStorageSub));   // (UX 09-08 #6)
        OnPropertyChanged(nameof(RailStorageExceptions));   // (Rail exceptions 09-09)
        OnPropertyChanged(nameof(CanAddSecondary));
        OnPropertyChanged(nameof(HasSecondary));
        // The Primary-first pill hides without a secondary (1117); a sort left on it would show no pill
        // selected and no glyph. Back to queue order, the resting state.
        OnPropertyChanged(nameof(PrimaryButtonText));
        OnPropertyChanged(nameof(PrimaryLocation));
        OnPropertyChanged(nameof(SecondaryLocation));
        OnPropertyChanged(nameof(PrimaryIsProblem)); OnPropertyChanged(nameof(PrimaryIsDisconnected));
        OnPropertyChanged(nameof(PrimaryJustReattached)); OnPropertyChanged(nameof(SecondaryIsProblem));
        OnPropertyChanged(nameof(SecondaryIsDisconnected)); OnPropertyChanged(nameof(SecondaryJustReattached));
    }

    /// <summary>True once a primary location is configured. Secondary is meaningless without one.</summary>
    public bool CanAddSecondary => HasLocation && BackupLocations.Count < 2;

    /// <summary>OTHER STORAGE (owner 09-14; was "Detached"): a drive Grog knows about that holds no slot. Its
    /// files stay recorded and counted as backed up; nothing downloads to it until it takes a slot. "Use as
    /// Secondary/Primary" when that slot is free, "Swap with ..." when it is held; Forget is the plain remove.</summary>
    public sealed partial class OtherStorageRow : ObservableObject
    {
        public OtherStorageRow(string rootId, string label, string path, int fileCount, long bytes, bool isConnected,
                               IReadOnlyList<OtherStorageGame> games)
        { RootId = rootId; Label = label; Path = path; FileCount = fileCount; Bytes = bytes; IsConnected = isConnected; Games = games; }
        public string RootId { get; } public string Label { get; } public string Path { get; }
        public int FileCount { get; } public long Bytes { get; } public bool IsConnected { get; }
        /// <summary>What is on it, by game: the read-only view of the drive.</summary>
        public IReadOnlyList<OtherStorageGame> Games { get; }
        public string Summary => $"{FileCount} file{(FileCount == 1 ? "" : "s")} · {Grog.Core.Format.ByteFormat.Size(Bytes)}";
        public string StateText => IsConnected ? "Connected" : "Unplugged";
        public Avalonia.Media.IBrush StateBrush => IsConnected ? Palette.SuccessGreen : Palette.InkMuted;
        public string GamesHeader => $"{Games.Count} game{(Games.Count == 1 ? "" : "s")} on this storage";
        [ObservableProperty] private bool _isExpanded;
        public string Caret => IsExpanded ? "▾" : "▸";
        partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Caret));
        // Slot-dependent labels, set by the page from the slot state at rebuild.
        public string SecondaryActionLabel { get; set; } = "Use as Secondary";
        public string PrimaryActionLabel { get; set; } = "Swap with Primary…";
    }
    public sealed record OtherStorageGame(string Title, int FileCount, long Bytes)
    {
        public string Summary => $"{FileCount} file{(FileCount == 1 ? "" : "s")} · {Grog.Core.Format.ByteFormat.Size(Bytes)}";
    }
    public ObservableCollection<OtherStorageRow> OtherStorage { get; } = new();
    public bool HasOtherStorage => OtherStorage.Count > 0;
    public string OtherStorageRailText => $"{OtherStorage.Count} other storage";   // number first, like "1 drive" above it
    public bool HasSecondary => BackupLocations.Count > 1;
    /// <summary>The two devices as individual cells, so the cards can sit in the same two columns as the
    /// grids below them (one shared, slideable divider lines everything up).</summary>
    public BackupLocationRow? PrimaryLocation => BackupLocations.FirstOrDefault(d => d.IsPrimary);
    public BackupLocationRow? SecondaryLocation => BackupLocations.FirstOrDefault(d => !d.IsPrimary);

    // Flattened state flags: binding through the nullable row logs an Avalonia error on the null hop
    // even with a FallbackValue; reading them here keeps the path non-null.
    public bool PrimaryIsProblem        => PrimaryLocation?.IsProblem ?? false;
    public bool PrimaryIsDisconnected   => PrimaryLocation?.IsDisconnected ?? false;
    public bool PrimaryJustReattached   => PrimaryLocation?.JustReattached ?? false;
    public bool SecondaryIsProblem      => SecondaryLocation?.IsProblem ?? false;
    public bool SecondaryIsDisconnected => SecondaryLocation?.IsDisconnected ?? false;
    public bool SecondaryJustReattached => SecondaryLocation?.JustReattached ?? false;
    public string PrimaryButtonText => HasLocation ? "Change Primary Storage…" : "Set Primary Storage…";

    /// <summary>Warm StorageReport's per-path drive sample for every known root OFF the UI thread (UI-thread
    /// sweep 09-06): the first DriveSample call for a path probes the volume synchronously, and the storage rows
    /// are built on the UI thread. Call (via Task.Run) at manifest bind time; later calls are cheap cache hits.</summary>
    internal void PreseedDriveSamples()
    {
        var m = _session.Manifest?.Current;
        if (m is null) return;
        var paths = new List<string>();
        foreach (var r in m.Roots) paths.Add(r.PathHint);
        paths.Add(_session.BackupRoot);
        foreach (var p in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(p)) continue;
            try { Grog.Core.Storage.StorageReport.DriveSample(p); } catch { /* best-effort warm-up */ }
        }
    }

    /// <summary>Rebuild the per-location rows (capacity, composition, coverage, the queue projection).</summary>
    /// <param name="dashboardPass">True from the shell's RaiseDashboardCore: RaiseHealth follows this rebuild in the
    /// same synchronous pass, so the health checks need not re-walk the library here (UI-thread sweep 09-06).</param>
    internal void RefreshBackupLocations(bool dashboardPass = false, bool forceOtherStorage = false)
        => Services.UiWatch.Time("RefreshBackupLocations", () => RefreshBackupLocationsCore(dashboardPass, forceOtherStorage));
    private void RefreshBackupLocationsCore(bool dashboardPass, bool forceOtherStorage)
    {
        if (_session.Manifest is null)
        {
            if (BackupLocations.Count > 0) BackupLocations.Clear();
            if (OtherStorage.Count > 0) { OtherStorage.Clear(); OnPropertyChanged(nameof(HasOtherStorage)); OnPropertyChanged(nameof(OtherStorageRailText)); OnPropertyChanged(nameof(OtherStorageSummary)); }
            RaiseLocationButtons(); return;
        }
        var m = _session.Manifest.Current;
        string primaryId = m.PrimaryRootId ?? "primary";
        // StorageReport.DrivesFor and CoverageLineFor resolved an unset RootId against the RAW PrimaryRootId
        // (null on a pre-root manifest) while the file count and composition used the "primary" fallback; the
        // one pass below keeps each figure on the rule it had, so no number moves (UI-thread sweep 09-06).
        string? rawPrimary = m.PrimaryRootId;

        var pairs = new List<(string RootId, string Path)>();
        foreach (var r in m.Roots.Where(r => r.State != RootState.Detached)) pairs.Add((r.Id, r.PathHint));   // shelved drives have no slot (09-13)
        // No primary and no chosen root (the only storage was forgotten): nothing to imply a card from.
        if (!pairs.Any(p => p.RootId == primaryId) && !string.IsNullOrEmpty(_session.BackupRoot))
            pairs.Insert(0, (primaryId, _session.BackupRoot));

        // Stable slots: left = Primary, right = Secondary; "Make primary" swaps content, not cards.
        // OrderByDescending is stable, so non-primary locations keep their relative order.
        pairs = pairs.OrderByDescending(p => p.RootId == primaryId).ToList();   // primary may be implicit (= chosen root)

        // Capacity bars read the QUEUE, not scope-wide pending: someone who knows a device is too small
        // queues deliberately, and judging them against everything they have not backed up yet would cry
        // wolf on a queue that fits perfectly well.
        var byGogId = new Dictionary<long, Grog.Core.Models.LibraryItem>();
        foreach (var it in m.Items) byGogId[it.GogId] = it;

        // ONE pass over Items x Files for every per-root figure (UI-thread sweep 09-06). Roots not in `pairs`
        // still get an entry so a file pointing at a forgotten root never throws; they are simply not rendered.
        var scope = _root.Library.EffectiveScope;
        var agg = new Dictionary<string, RootAgg>(StringComparer.Ordinal);
        foreach (var pr in pairs) agg[pr.RootId] = new RootAgg();
        RootAgg AggFor(string id) { if (!agg.TryGetValue(id, out var a)) { a = new RootAgg(); agg[id] = a; } return a; }
        int coverTotalN = 0; long coverTotalB = 0;
        foreach (var item in m.Items)
        {
            // Coverage counts only backup-eligible items (and legacy movies only when shown); the other figures
            // read every item, as their separate walks did.
            bool coverItem = Grog.Core.Sync.LibraryStats.IsBackupEligible(item)
                             && !(item.Type == ProductType.Movie && !_root.Library.ShowLegacyMovies);
            HashSet<string>? hereRoots = null;   // roots this item's in-scope files landed on (any one = "game is here")
            foreach (var f in item.Files)
            {
                bool present = Grog.Core.Sync.BackupScope.IsPresent(f);
                bool local = Grog.Core.Sync.BackupScope.HasLocalCopy(f);
                if (local)
                {
                    var a = AggFor(f.RootId ?? primaryId);
                    a.FileCount++;
                    // ONE rule for the card's file count, its Grog bytes and its composition strip: bytes on disk
                    // (HasLocalCopy, superseded copies included). GrogBytes used IsPresent while the count and the
                    // strip used HasLocalCopy, so an Outdated file was counted but its bytes were not, and the
                    // Other-storage games summed to more than their row (QA 09-18).
                    a.GrogBytes += Grog.Core.Sync.Rollups.HeldBytesOf(f);
                    // Fold fine classifier buckets onto the canonical vocabulary, so the strip reads the same
                    // as the chips, legend and detail tags instead of exposing raw bucket names.
                    var bucket = f.Kind == FileKind.Extra
                        ? ExtraTypeSegment.Label(Grog.Core.Sync.ExtraClassifier.Classify(f.Name, f.ExtraType)) : "Games";
                    a.Buckets.TryGetValue(bucket, out var b);
                    a.Buckets[bucket] = b + (Grog.Core.Sync.Rollups.HeldBytesOf(f));   // composition = bytes on disk, superseded copies included
                }
                if (coverItem && scope.Includes(f) && f.State != FileState.Unavailable)   // Unavailable: never a gap you can close
                {
                    long size = Grog.Core.Sync.Rollups.HeldBytesOf(f);
                    coverTotalN++; coverTotalB += size;
                    if (present && (f.RootId ?? rawPrimary) is { } cRoot)
                    {
                        var a = AggFor(cRoot);
                        a.HereN++; a.HereB += size;
                        if ((hereRoots ??= new HashSet<string>()).Add(cRoot)) a.HereGames++;   // a game counts as "here" once per root
                    }
                }
            }
            // Retained old versions get their own "Old versions" slice: never a content type, never completeness.
            foreach (var f in item.OldVersionFiles)
            {
                var a = AggFor(f.RootId ?? primaryId);
                a.Buckets.TryGetValue("Old versions", out var ob);
                a.Buckets["Old versions"] = ob + (f.LocalSizeBytes ?? 0);
                a.GrogBytes += f.LocalSizeBytes ?? 0;   // the archive is Grog's bytes on that drive too
            }
        }

        // Per-drive capacity from the cached one-second sample (never a disk touch on this thread after the
        // first call, which PreseedDriveSamples warms off-thread at bind time); same maths as StorageReport.DrivesFor.
        var drives = new List<Grog.Core.Storage.StorageReport.DriveUsage>();
        foreach (var (rootId, path) in pairs)
        {
            // Free space and the .grog-tmp size are read in ONE background pass so the pair is from the same
            // instant (owner-caught 09-04).
            var sample = Grog.Core.Storage.StorageReport.DriveSample(path);
            // "Backed up" = present on disk per BackupScope.IsPresent, plus what the engine holds in
            // <root>/.grog-tmp, MEASURED (a Pause/Resume re-plan can leave a .part behind on another device).
            long grog = agg[rootId].GrogBytes + sample.PartialBytes;
            drives.Add(new Grog.Core.Storage.StorageReport.DriveUsage(rootId, path, sample.Total, sample.Free, grog, sample.Online));
        }

        // IDLE: RE-PLAN, never trust the stored TargetRootId. Each queue entry carries the root it was assigned
        // when it was enqueued, which goes stale the moment anything changes -- app restarted, folder
        // switched, space consumed elsewhere. Reading it back showed a plan nobody had re-checked, so a
        // queue far larger than the drive could sit there looking perfectly placed.
        var queuedFiles = new List<GameFile>();
        foreach (var q in m.Downloads.Snapshot())
            if (byGogId.TryGetValue(q.GogId, out var qi)
                && qi.Files.FirstOrDefault(f => f.FileKey == q.FileKey) is { } gf)
                queuedFiles.Add(gf);

        var queuedByRoot = new Dictionary<string, long>();
        IReadOnlyList<Grog.Core.Volumes.DeviceSpace>? roomByRoot = null;   // from the plan when one ran here
        long unplaceableBytes = 0;
        int unplaceableCount = 0;
        // The live branch is for a run that is MOVING. Pause sets StopRequested and keeps the engine, so a paused
        // run takes the replan path below and must hand the engine over: with engine:null the records were
        // rewritten but the engine never reconciled (a row flagged won't-fit stayed Pending and downloaded on
        // Resume; a row that started fitting was never enqueued) -- review 09-16.
        if (queuedFiles.Count > 0 && _session.LiveEngine is { } live && !_session.StopRequested)
        {
            // A RUN IS LIVE: the plan was made once, at Back up, and the engine WILL put each file where its task
            // says. Re-planning every second against a free figure that falls as the .part grows (while the queue
            // still carries the whole file) double-counts the in-flight bytes and flips games near the cut-off
            // between devices - the bar breathed (owner, 09-04). Read the run's own assignment; what is queued but
            // not in the engine is what was held for space.
            // Completed tasks stay in the map with zero remaining: between the engine finishing a file and the
            // settler removing its queue entry, a tick would otherwise read it as "queued, fits nowhere" and
            // flash the segment red for a second on every small file.
            // Won't-fit is the persisted Fits flag here too, the same fact the rows paint (QA 09-30 A4): counting from
            // engine membership called a turned-away task (in the engine, red) placed, and a file waiting for its
            // unplugged drive (not in the engine, white) won't-fit. The engine only says which are done and where.
            var target = new Dictionary<(long, string), (string Root, bool Done)>();
            foreach (var t in live.Snapshot)
                target[(t.File.GameGogId, t.File.FileKey)] = (t.TargetRootId ?? primaryId, t.State == DownloadTaskState.Completed);
            var fitsOf = new Dictionary<(long, string), (bool Fits, string? Root)>();
            foreach (var q in m.Downloads.Snapshot()) fitsOf[(q.GogId, q.FileKey)] = (q.Fits, q.TargetRootId);
            foreach (var f in queuedFiles)
            {
                var key = (f.GameGogId, f.FileKey);
                bool fits = !fitsOf.TryGetValue(key, out var qf) || qf.Fits;
                if (!fits)
                { unplaceableBytes += Grog.Core.Volumes.PlacementPlanner.StillNeeds(f); unplaceableCount++; continue; }
                target.TryGetValue(key, out var tg);
                if (tg.Done) continue;
                var root = !string.IsNullOrEmpty(tg.Root) ? tg.Root : qf.Root ?? primaryId;
                long bytes = Math.Max(0, (f.ExpectedSizeBytes ?? 0) - (f.HasPartial ? f.PartialBytes ?? 0 : 0));   // the partial is already in Grog bytes
                queuedByRoot[root] = (queuedByRoot.TryGetValue(root, out var had) ? had : 0) + bytes;
            }
        }
        else if (queuedFiles.Count > 0)
        {
            // IDLE: one fact for every surface (09-13). The rows paint from the persisted Fits flag, so the banner
            // must count the SAME flag, not a fresh projection nobody wrote back: with the two apart, a queue
            // built before the first Back up showed "N won't fit" over zero red rows, and "Show me" / "Remove
            // them" had nothing to act on. ReplanWholeQueue with no engine rewrites target + Fits in order.
            // (09-25) NEVER re-plan here: this runs on the UI thread on every dashboard pass, and the same pass took
            // 1.7 s idle and 5 to 24 s from a sort (walk 09-25). Paint from the persisted flags; when the inputs a
            // plan reads have changed since the last request (drive free space and state, queue membership and
            // order), ask for ONE coalesced re-plan off the UI thread. It rewrites the flags and calls back here,
            // where the inputs now match, so it never loops.
            // Every input a plan reads can change here (free space, roots, routing, sizes, states), so every idle
            // refresh asks; the request is coalesced (one in flight, one trailing, at most one a second) and the
            // refresh that publishes a plan never asks again (no loop).
            if (!_root.Overview.PublishingPlan) _root.Overview.RequestIdleReplan();
            roomByRoot = _root.Overview.LastPlanRoom;   // the plan's own leftover, not free-minus-queued (09-16 contract)
            var fileByKey = new Dictionary<(long, string), GameFile>();
            foreach (var f in queuedFiles) fileByKey[(f.GameGogId, f.FileKey)] = f;
            foreach (var q in m.Downloads.Snapshot())
            {
                if (!fileByKey.TryGetValue((q.GogId, q.FileKey), out var f)) continue;
                if (!q.Fits || string.IsNullOrEmpty(q.TargetRootId)) { unplaceableBytes += Grog.Core.Volumes.PlacementPlanner.StillNeeds(f); unplaceableCount++; continue; }
                // The planner's price (what the file still needs): the partial is already out of the free figure.
                // Charging the full size read "0 B still free" over a queue the planner said fits (review 09-16).
                long bytes = Math.Max(0, (f.ExpectedSizeBytes ?? 0) - (f.HasPartial ? f.PartialBytes ?? 0 : 0));
                queuedByRoot[q.TargetRootId!] = (queuedByRoot.TryGetValue(q.TargetRootId!, out var had) ? had : 0) + bytes;
            }
        }

        // (09-16) What is still free once every fitting row has its room: the won't-fit divider shows it so the
        // user can see what a smaller file dragged above it (or Sort by Size) would fill. Same figures as the
        // rows: the per-root reservation just tallied against the drives' measured free space.
        // Per device (a fit is a per-device test): the plan's leftover when a plan ran here, else the live run's
        // reservation against the measured free space. Published unconditionally: the menus also key on flags
        // that change while the number does not (review 09-16).
        if (roomByRoot is null)
        {
            roomByRoot = drives
                .OrderByDescending(d => d.RootId == primaryId)   // fill order, primary first
                .Select(d => Grog.Core.Volumes.DeviceSpace.ForRoot(d.RootId,
                    Math.Max(0, d.FreeBytes - (queuedByRoot.TryGetValue(d.RootId, out var used) ? used : 0)), d.IsOnline, d.Path))
                .ToList();
        }
        _root.Overview.SetRoomLeft(roomByRoot);

        _root.QueueUnplaceableBytes = unplaceableBytes;   // (Rail exceptions 09-09) set BEFORE the flag: the flag raises the rail line that reads it
        _root.QueueHasUnplaceable = unplaceableBytes > 0;
        _root.Overview.SetUnplaceableQueue(unplaceableCount, unplaceableBytes);   // Files & issues gap breakdown
        _root.QueueUnplaceableLine = unplaceableBytes > 0 ? BackupLocationRow.FitsNowhereLine(unplaceableCount, unplaceableBytes) : "";

        // Rows are kept when nothing changed and swapped one at a time when something did (UI-thread sweep
        // 09-06): BackupLocationRow is an immutable record, so "update in place" means replacing the slot, and
        // a Clear + re-add every tick reset both device cards for nothing.
        // (Storage math 09-09) Grog bytes per VOLUME, so a row can take its siblings out of "other data".
        // Keyed by mount root; a path that will not resolve falls back to its own RootId, which makes it its
        // own volume and leaves the old single-folder behaviour exactly as it was.
        var grogByVolume = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var volumeOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in drives)
        {
            var vol = Grog.Core.Storage.DriveResolver.MountRootCached(u.Path) ?? ("rootid:" + u.RootId);   // UI thread: no mount-table scan
            volumeOf[u.RootId] = vol;
            grogByVolume[vol] = (grogByVolume.TryGetValue(vol, out var had) ? had : 0) + u.GrogBytes;
        }

        // OTHER STORAGE (09-13/14): on the shelf, files still counted, no slot. Rebuilt only when something changed
        // (the per-game list is built then, not on every dashboard pass).
        bool secondarySlotFree = pairs.Count < 2;
        var shelved = m.Roots.Where(r => r.State == RootState.Detached).ToList();
        // Cheap identity first; the per-game list and the disk probe only when something changed. The probe
        // (Directory.Exists on a drive letter) is skipped on the 1 Hz dashboard pass: an unplugged USB letter can
        // stall it, and the last answer stands until the next real refresh.
        bool otherChanged = forceOtherStorage || shelved.Count != OtherStorage.Count   // forced: the hidden rule changed (09-19)
            || shelved.Zip(OtherStorage).Any(z => z.First.Id != z.Second.RootId || z.First.Label != z.Second.Label || z.First.PathHint != z.Second.Path
                                                || AggFor(z.First.Id).FileCount != z.Second.FileCount || AggFor(z.First.Id).GrogBytes != z.Second.Bytes
                                                || (secondarySlotFree ? "Use as Secondary" : "Swap with Secondary…") != z.Second.SecondaryActionLabel
                                                || (pairs.Count == 0 ? "Use as Primary" : "Swap with Primary…") != z.Second.PrimaryActionLabel);
        // Presence comes from the 2 s watch's last map (it probes every root, shelved ones included, on a worker):
        // never a Directory.Exists on the dispatcher (review 09-15).
        bool ConnectedNow(BackupRoot r) => _prevOnline.TryGetValue(r.Id, out var on) && on;
        if (!otherChanged)
            otherChanged = shelved.Zip(OtherStorage).Any(z => ConnectedNow(z.First) != z.Second.IsConnected);
        if (otherChanged)
        {
            var expanded = OtherStorage.Where(o => o.IsExpanded).Select(o => o.RootId).ToHashSet();
            OtherStorage.Clear();
            foreach (var r in shelved)
            {
                var a = AggFor(r.Id);
                var hiddenPolicy = _root.Library.Hidden;   // (09-19) no hidden game's name in the per-drive list; the drive's totals keep its bytes
                var games = m.Items
                    .Where(hiddenPolicy.IsVisible)
                    .Select(it => (it.Title, Files: it.Files.Where(f => Grog.Core.Sync.BackupScope.HasLocalCopy(f) && f.RootId == r.Id).ToList()))
                    .Where(x => x.Files.Count > 0)
                    .Select(x => new OtherStorageGame(x.Title, x.Files.Count, x.Files.Sum(Grog.Core.Sync.Rollups.HeldBytesOf)))
                    .OrderByDescending(g => g.Bytes).ToList();
                bool connected = ConnectedNow(r);
                OtherStorage.Add(new OtherStorageRow(r.Id, r.Label, r.PathHint, a.FileCount, a.GrogBytes, connected, games)
                {
                    SecondaryActionLabel = secondarySlotFree ? "Use as Secondary" : "Swap with Secondary…",
                    PrimaryActionLabel = pairs.Count == 0 ? "Use as Primary" : "Swap with Primary…",
                    IsExpanded = expanded.Contains(r.Id),
                });
            }
            OnPropertyChanged(nameof(HasOtherStorage)); OnPropertyChanged(nameof(OtherStorageRailText)); OnPropertyChanged(nameof(OtherStorageSummary));
        }

        int slot = 0;
        foreach (var u in drives)
        {
            var root = m.Roots.FirstOrDefault(r => r.Id == u.RootId);
            bool isPrimary = u.RootId == primaryId;
            string label = isPrimary ? "Primary" : "Secondary";
            var ra = agg[u.RootId];
            int fileCount = ra.FileCount;
            long queuedHere = queuedByRoot.TryGetValue(u.RootId, out var qb) ? qb : 0;
            // Red when part of the queue fits NOWHERE. Spill means a root's own planned queue always fits,
            // so judging only this device would never go red no matter how hopeless the queue is.
            var fit = unplaceableBytes > 0
                ? Grog.Core.Storage.StorageReport.Headroom.WontFit
                : Grog.Core.Storage.StorageReport.Judge(queuedHere, u.FreeBytes);
            string gamesRoot = m.Routing.RoleRootOrPrimary(Grog.Core.Models.ContentRole.Games, primaryId)!;
            string extrasRoot = m.Routing.RoleRootOrPrimary(Grog.Core.Models.ContentRole.Extras, primaryId)!;
            var row = new BackupLocationRow(u.RootId, label, u.Path, isPrimary, u.IsOnline,
                u.GrogBytes, u.FreeBytes, u.TotalBytes, fileCount,
                Grog.Core.Storage.StorageReport.HeadroomColorHex(fit))
            {
                Name = root?.Label ?? "",
                ShowName = root is not null && Grog.Core.Volumes.VolumeService.HasCustomLabel(root),
                QueuedBytes = queuedHere,
                SiblingGrogBytes = Math.Max(0, (volumeOf.TryGetValue(u.RootId, out var vol0) && grogByVolume.TryGetValue(vol0, out var volTotal) ? volTotal : u.GrogBytes) - u.GrogBytes),
                HasUnplaceableQueue = unplaceableBytes > 0,
                UnplaceableBytes = unplaceableBytes,
                UnplaceableCount = unplaceableCount,
                Composition = CompositionFrom(ra.Buckets),
                CoverageLine = MainWindowViewModel.CoverageLineFrom(ra.HereGames, ra.HereN, ra.HereB, coverTotalN, coverTotalB),
                PolicyLine = PolicyFor(u.RootId, isPrimary),
                IsViewing = false,   // both device grids show at once; no single "viewed" device

                HoldsGames = u.RootId == gamesRoot,
                HoldsExtras = u.RootId == extrasRoot,
                Removable = root?.Removable ?? false,
                JustReattached = _justReattached.Contains(u.RootId),
                // Only paint online/offline once services are up and a real location exists: before that the
                // primary row probes an empty path and would read a false "offline". Nor before the watch's first
                // off-thread probe has landed.
                StatusKnown = _session.ServicesReady && HasLocation && _watchSeeded,
                VerifyText = _verifyingRootId == u.RootId ? $"Verifying {_verifyDone} / {_verifyTotal}" : "",
            };
            if (slot < BackupLocations.Count) { if (!SameRow(BackupLocations[slot], row)) BackupLocations[slot] = row; }
            else BackupLocations.Add(row);
            slot++;
        }
        while (BackupLocations.Count > slot) BackupLocations.RemoveAt(BackupLocations.Count - 1);
        RaiseLocationButtons();
        // The health line reads BackupLocations, so it MUST re-raise here: the Overview card renders
        // before locations load and would keep the empty-list value.
        _root.Overview.RaiseHealthStatus();
        // The Storage-connected check row derives from the IsProblem flags rebuilt ABOVE - without a
        // rebuild the card claims "1 of 1" while the lede reports a missing drive (owner-caught 08-31).
        _root.Overview.RebuildHealthChecks(retally: !dashboardPass);   // the dashboard pass re-tallies in RaiseHealth right after (UI-thread sweep 09-06)
        _root.Settings.RaisePolicyState();
        UpdateDeviceProblem();
        OnPropertyChanged(nameof(ShowSystemDriveHint)); OnPropertyChanged(nameof(SystemDriveHintText));
    }

    // ---- Storage hint (design, 09-01): skipping the tour lands backups in Documents, i.e. the OS drive.
    // When the primary storage is that drive AND the in-scope library will not fit in what is free, say so
    // once, calmly, with the plan (a second storage - spill is automatic). Dismissible; never red.
    private BackupLocationRow? PrimaryOnSystemDrive()
    {
        var p = BackupLocations.FirstOrDefault(r => r.IsPrimary && r.IsOnline && r.TotalBytes > 0);
        if (p is null || string.IsNullOrEmpty(p.Path)) return null;
        try
        {
            string? sysRoot = OperatingSystem.IsWindows()
                ? System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System))
                : "/";
            var root = new System.IO.DriveInfo(p.Path).Name;
            return string.Equals(root, sysRoot, StringComparison.OrdinalIgnoreCase) ? p : null;
        }
        catch { return null; }   // unresolvable path: no hint rather than a wrong one
    }
    /// <summary>Assistant-only: the harness's temp root is always the container's "/" with plenty free, so
    /// the render forces the condition to check the banner's layout.</summary>
    internal bool ForceSystemDriveHint { get; set; }
    private BackupLocationRow? HintRow()
        => PrimaryOnSystemDrive() ?? (ForceSystemDriveHint ? BackupLocations.FirstOrDefault(r => r.IsPrimary) : null);
    public bool ShowSystemDriveHint =>
        !_session.Settings.SystemDriveHintDismissed && HasLocation && _root.Library.Stats.AllSizeBytes > 0
        && HintRow() is { } p && (ForceSystemDriveHint || _root.Library.Stats.AllSizeBytes - _root.Library.Stats.AllDoneBytes > p.FreeBytes);
    public string SystemDriveHintText
    {
        get
        {
            var p = HintRow();
            if (p is null) return "";
            var need = Grog.Core.Format.ByteFormat.Size(_root.Library.Stats.AllSizeBytes - _root.Library.Stats.AllDoneBytes);
            var free = Grog.Core.Format.ByteFormat.Size(p.FreeBytes);
            return $"Your primary storage is on the system drive, which has {free} free; the rest of your library needs "
                 + $"about {need}. Add a second storage on a bigger drive - whatever does not fit here goes there automatically.";
        }
    }
    [RelayCommand]
    private void DismissSystemDriveHint()
    {
        _session.Settings.SystemDriveHintDismissed = true; _session.Settings.SaveSoon();
        OnPropertyChanged(nameof(ShowSystemDriveHint));
    }

    /// <summary>The rail "Folders" problem dot: field-backed so it can be DEBOUNCED -- a real problem persists,
    /// while the startup blip (rows built from an empty path probe "offline") must not light the dot.</summary>
    public bool HasDeviceProblem => _hasDeviceProblem;
    private bool _hasDeviceProblem;

    /// <summary>The instantaneous problem condition, gated on a known location + settled services.</summary>
    private bool RawDeviceProblem => _session.ServicesReady && HasLocation && BackupLocations.Any(d => d.IsProblem);

    private DispatcherTimer? _deviceProblemDebounce;

    /// <summary>Recompute the debounced rail dot: clearing is immediate; asserting waits ~3s of the condition
    /// staying true, so transient flashes are swallowed while a genuinely-missing drive still surfaces.</summary>
    private void UpdateDeviceProblem()
    {
        if (!RawDeviceProblem)
        {
            _deviceProblemDebounce?.Stop();
            if (_hasDeviceProblem) { _hasDeviceProblem = false; OnPropertyChanged(nameof(HasDeviceProblem)); }
            return;
        }
        if (_hasDeviceProblem) return;                              // already shown
        if (_deviceProblemDebounce is { IsEnabled: true }) return;  // already counting down
        _deviceProblemDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _deviceProblemDebounce.Tick += (_, _) =>
        {
            _deviceProblemDebounce!.Stop();
            if (RawDeviceProblem && !_hasDeviceProblem) { _hasDeviceProblem = true; OnPropertyChanged(nameof(HasDeviceProblem)); }
        };
        _deviceProblemDebounce.Start();
    }

    /// <summary>Folders-tab "Rescan &amp; Verify", read-frugal over BOTH devices: size-check every settled file,
    /// MD5 only unverified files with a checksum; skips offline drives; serialized via the activity slot.</summary>
    /// <summary>(QA 09-30 B7) A verify reads files a running (or paused, half-moved) move is relocating: resolved at the
    /// old place they read Missing and were re-queued. Every verify entry point asks here first.</summary>
    internal bool VerifyBlockedByMove()
    {
        if (!(IsReorgRunning || ReorgPaused)) return false;
        _root.ShowToast("A move is in progress. Verify once it has finished or been stopped.", 1);
        return true;
    }

    [RelayCommand]
    private async Task Verify()
    {
        if (_root.Busy || VerifyBlockedByMove()) return;
        await _root.RunBusy("Verifying…", async () =>
        {
            // (S2.2) Core's VerifyRun; size-only, as this button always ran (RunAsync(full: false)). Core logs the
            // "Verify: n ok, ..." summary through the host.
            var request = Grog.Core.Runs.VerifyRequest.LibrarySizeOnly() with
            {
                FetchCurrentServerMd5 = _root.ServerChecksumFetcher(),
                Progress = p => Dispatcher.UIThread.Post(() =>
                {
                    _root.ProgressVisible = true;
                    _root.ProgressValue = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
                }),
            };
            await Grog.Core.Runs.VerifyRun.RunAsync(_session.Manifest, _session.BackupRoot, request, new MainWindowViewModel.AppRunHost(_root, LogCategory.Verify), _session.Cts?.Token ?? default);
            await _root.ReconcileAndRefresh();
        });
    }
}

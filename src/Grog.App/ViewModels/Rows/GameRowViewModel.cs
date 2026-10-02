// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
// Row and support types the views bind to; shares the MainWindowViewModel namespace on purpose.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Models;
using Grog.Core.Download;
using Grog.Core.Sync;
using Grog.Core.Format;

namespace Grog.App.ViewModels;

public partial class GameRowViewModel : ObservableObject
{
    /// <summary>THE "this game has an update" rule, used by the rail count, the Status bucket and the
    /// Updates quick-filter alike - three sites that used to carry their own copy of it.</summary>
    /// <para>SCOPED (sweep 2 #16): an outdated extra with extras out of scope is not an update the next run
    /// will fetch, and counting it lit the rail's Updates figure for work that never happens.</para>
    public bool HasUpdate => Status == BackupStatus.Outdated
        || ScopedFiles.Any(f => f.State == FileState.Outdated);

    public long GogId { get; init; }
    public string Title { get; init; } = "";
    /// <summary>(New items 09-09) This item is new to the library -- the small "New" chip beside the title
    /// and the Status dropdown's "New" bucket both read it. From the item; a rebuild is what refreshes it.</summary>
    public bool IsNew => Item?.IsNew ?? false;
    /// <summary>(New items 09-09) The "New" chip's ground and hairline -- the SAME construction as the Cloud
    /// Saves status pill (translucent amber over the row), so the two chips read as one idea.</summary>
    public IBrush NewChipBg => RowBrushes.Translucent(Palette.AccentAmberSoft, 0x22);
    public IBrush NewChipBorder => RowBrushes.Translucent(Palette.AccentAmberSoft, 0x55);
    public ProductType Type { get; init; }
    public string OsSummary { get; init; } = "";
    /// <summary>Formatted from TotalSizeBytes - the SAME value the column sorts by, so display and sort agree.
    /// Under a gigabyte it reads in MB ("0.0 GB" is a rounding artifact, not a size); sorting stays numeric.</summary>
    public string TotalSizeText => TotalSizeBytes <= 0 ? ""
        : TotalSizeBytes >= 1L << 30 ? $"{TotalSizeBytes / (double)(1L << 30):F1} GB"
        : $"{TotalSizeBytes / (double)(1L << 20):F0} MB";
    /// <summary>Files this row COUNTS: the scoped set, not every file GOG lists -- matching the Status column,
    /// detail pane, queue, health tally and progress bar. A count you cannot act on is worse than no count.</summary>
    /// <para>THE scoped set is <see cref="Grog.Core.Sync.BackupScope.Scoped"/>, the same one Status reads: a file
    /// GOG refuses to serve (Unavailable) is in neither. A hand-rolled Scope.Includes here counted it, so a
    /// Backed Up game read "4/5" beside a green tick (sweep 2 #13).</para>
    private IEnumerable<GameFile> ScopedFiles =>
        Item is null ? Enumerable.Empty<GameFile>() : Grog.Core.Sync.BackupScope.Scoped(Item, Scope);

    /// <summary>Total bytes, for correct numeric sorting. Scoped for the same reason as the counts:
    /// otherwise the Size column advertises bytes that will never be downloaded.</summary>
    public long TotalSizeBytes => ScopedFiles.Sum(f => Grog.Core.Sync.Rollups.TotalBytesOf(f));
    public string ExtrasSummary { get; init; } = "";

    /// <summary>Owner discs for the grid's Owner column. Empty (and the column hidden) on single-account
    /// machines. Order is registration order, primary first; the tooltip carries the full names.</summary>
    public IReadOnlyList<AccountBadge> OwnerBadges =>
        Item is null ? Array.Empty<AccountBadge>() : ViewModels.OwnerBadges.For(Item.OwnerIds);
    /// <summary>Side by side while there is room, overlapping as the count grows (owner-approved look):
    /// up to three discs sit with a small gap; four or more tuck under each other.</summary>
    public double OwnerSpacing => OwnerBadges.Count <= 3 ? 3 : -7;

    /// <summary>Every cell reserves the FULL-STACK width (all registered accounts), so the column's Auto
    /// measurement can never undershoot when co-owned stacks scroll in later. Cell-side MinWidth is layout-native.</summary>
    public double OwnerCellMinWidth
    {
        get
        {
            var n = ViewModels.OwnerBadges.Map.Count;
            return n <= 3 ? n * 22 + Math.Max(0, n - 1) * 3 : 3 * 22 + 2 * 3 + (n - 3) * 15;
        }
    }
    public string OwnerNames => string.Join(", ", OwnerBadges.Select(b => b.Name));
    /// <summary>Sort key for the Owner column: initials joined in registration order, so single-owner
    /// games group by account and co-owned games sort after their first owner's solo games.</summary>
    public string OwnerSortKey => string.Join("|", OwnerBadges.Select(b => b.Initials));

    /// <summary>Owner-column cells are computed off the static registry/filter state, so a filter change
    /// must tell every visible row to re-read them.</summary>
    public void RaiseOwnersChanged()
    {
        OnPropertyChanged(nameof(OwnerBadges));
        OnPropertyChanged(nameof(OwnerSpacing));
        OnPropertyChanged(nameof(OwnerNames));
        OnPropertyChanged(nameof(OwnerSortKey));
        OnPropertyChanged(nameof(OwnerCellMinWidth));
    }

    public int FileCount { get; init; }
    public int GameFileCount => ScopedFiles.Count(f => f.Kind != FileKind.Extra);
    public int ExtraFileCount => ScopedFiles.Count(f => f.Kind == FileKind.Extra);
    public int TotalFileCount => Item is null ? FileCount : ScopedFiles.Count();

    // ---- Grid refresh: terse Folder value, type badge, extras square-swatches, idle-bar hide ----
    /// <summary>Folder-column value: full words ("Primary"/"Secondary"/"Split"), with a clean dash when none.</summary>
    public string LocationShort
    {
        get
        {
            var s = LocationText;
            if (string.IsNullOrEmpty(s) || s == "--" || s == "\u2014") return "\u2014";
            if (s.StartsWith("Prim", StringComparison.OrdinalIgnoreCase)) return "Primary";
            if (s.StartsWith("Sec",  StringComparison.OrdinalIgnoreCase)) return "Secondary";
            if (s.StartsWith("Mix",  StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("Spl",  StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("Both", StringComparison.OrdinalIgnoreCase)) return "Split";
            return s;
        }
    }

    /// <summary>Type badge for the rare non-Game rows (the Type column is gone). Blank for plain games.</summary>
    public string TypeBadgeText => Type switch { ProductType.Mod => "MOD", ProductType.Movie => "MOVIE", _ => "" };
    public bool ShowTypeBadge => TypeBadgeText.Length > 0;
    /// <summary>Badge color = the same content-type color used by the chips, squares and legends, so a Mod
    /// row's badge is Mod-teal and a Movie row's is Movie-magenta.</summary>
    public IBrush TypeBadgeBrush => ExtraTypeSegment.ColorFor(Type == ProductType.Mod ? "Mod" : Type == ProductType.Movie ? "Movie" : "Other");

    /// <summary>Hide the per-row progress bar on idle rows (0% and not running) -- empty gray troughs read
    /// as "stuck". Backed-up (green) and in-flight rows still show it.</summary>
    public bool ShowRowProgress => BarValue > 0 || RowBusy;

    /// <summary>One square per extras TYPE the game has (bucketed via ExtraClassifier), biggest-first, with
    /// its file count. Color = the shared legend swatch; `Dim` is set by the active Extras filter.</summary>
    public sealed record ExtraChip(string Name, IBrush Brush, int Count, bool Dim);
    /// <summary>The shared content-bucket key for a file, matching the filter chips exactly. Installers key
    /// by product type ("Mod" for a mod, else "Game"); extras collapse to the 5 extra chip buckets.</summary>
    public static string ContentKey(GameFile f, ProductType type) => f.Kind != FileKind.Extra
        ? (type == ProductType.Mod ? "Mod" : "Game")
        : Grog.Core.Sync.ExtraClassifier.ShortLabel(Grog.Core.Sync.ExtraClassifier.Classify(f.Name, f.ExtraType));
    /// <summary>Selected content-chip keys while a filter is active (null = all selected). When set, per-row
    /// squares NOT in it render dimmed AND sort after the lit ones, so the matched type reads at a glance.</summary>
    public static IReadOnlySet<string>? ActiveExtraFilter;
    /// <summary>Selected backup languages (null/empty = every language), set from the VM on grid rebuild.
    /// An extra for a language NOT in this set leaves the per-row squares; language-neutral extras never do.</summary>
    public static IReadOnlyCollection<string>? ActiveLanguages;
    // Deliberately language-ONLY (not the full Scope): the chips are a "what does this game contain" inventory.
    // LATENT DRIFT: if ExtraClassifier ever tags an extra with a platform, switch to the row's full Scope.Includes.
    private static bool LanguageInScope(GameFile f)
    {
        var langs = ActiveLanguages;
        if (langs is null || langs.Count == 0) return true;
        var lang = Grog.Core.Sync.ExtraClassifier.LanguageOf(f.Name);
        return lang is null || langs.Contains(lang, StringComparer.OrdinalIgnoreCase);
    }
    public IReadOnlyList<ExtraChip> ExtraChips
    {
        get
        {
            if (Item is null) return System.Array.Empty<ExtraChip>();
            var active = ActiveExtraFilter;
            return Item.Files
                .Where(f => f.Kind == FileKind.Extra && LanguageInScope(f))
                .GroupBy(f => ContentKey(f, Type))
                .Select(g => new ExtraChip(g.Key, ExtraTypeSegment.ColorFor(g.Key), g.Count(),
                    active is { Count: > 0 } && !active.Contains(g.Key)))
                .OrderBy(c => c.Dim ? 1 : 0).ThenByDescending(c => c.Count).ThenBy(c => c.Name)
                .ToList();
        }
    }
    public void RaiseExtraChips() => OnPropertyChanged(nameof(ExtraChips));

    // live per-row backup progress (0-100), shown as a thin bar during a sync
    [ObservableProperty] private double _rowProgress;
    [ObservableProperty] private bool _rowBusy;

    /// <summary>Whether extras count toward "complete" (mirrors the backup scope setting).</summary>
    public bool IncludeExtras { get; set; } = true;
    public bool IncludeGames { get; set; } = true;
    /// <summary>The full backup scope this row was built for; drives the scope-aware Status dot +
    /// CompletionPercent so the row agrees with the hero / Status / queue. Defaults to Both.</summary>
    public Grog.Core.Sync.Scope Scope { get; set; } = Grog.Core.Sync.Scope.Both;

    /// <summary>FILE-COUNT completion for this game, scope-aware: present in-scope files over in-scope files
    /// (owner 09-16; was byte-weighted, which drew a 1/5 game whose one file is small as an empty bar). The
    /// fraction beside the bar and the bar now say the same thing.</summary>
    public double CompletionPercent
    {
        get
        {
            int total = FilesInScope;
            return total == 0 ? 0 : 100.0 * FilesPresent / total;
        }
    }

    /// <para>Both operands are FILE-COUNT percents (RowProgress is <c>Rollups.GameFilePercent</c>): the bar
    /// never mixes units (sweep 2 #18).</para>
    /// <summary>Live bar value: never less than the standing on-disk completion, rising to the live figure while
    /// a sync feeds RowProgress. The Max covers paused and per-file paths, where RowProgress may be 0/stale.</summary>
    public double BarValue => System.Math.Max(CompletionPercent, RowBusy ? RowProgress : 0);

    /// <summary>Bar fill color encodes state: red=at-risk, green=complete, amber=partial, gray track=empty.</summary>
    public IBrush BarBrush
    {
        get
        {
            if (IsDelisted) return Palette.ErrorRed;
            var p = BarValue;
            if (p >= 99.5) return Palette.SuccessGreen;   // complete = green
            if (p > 0)     return Palette.AccentAmber;   // partial = amber
            return Palette.StrokeSubtle;                  // empty = quiet track
        }
    }

    // ---- STATUS VOCABULARY: ONE mark per state, shared by the Library column, Folders badge and rail.
    //      COLOR = health, GLYPH = what is happening, and the BAR only appears when it VARIES.
    //      Dim is NOT a failure; amber = a gap you can close; red = bytes you thought you had are not there.

    /// <summary>True when this row's progress is worth drawing: incomplete, or live.</summary>
    /// <summary>A bar only once something has landed or is landing: a queued or paused game with 0 files and
    /// no bytes yet rests with the dash + count like any other 0/N row (owner 09-17: an empty track down the
    /// column said nothing the fraction did not).</summary>
    public bool ShowRowBar => RowBusy
        || (StatusText is "Downloading" or "Paused" or "Partial" or "Moving" && (FilesPresent > 0 || BarValue > 0))
        || (BarValue > 0 && BarValue < 99.5);
    /// <summary>Complete and idle: a check, not a full bar.</summary>
    public bool ShowRowMark => !ShowRowBar;

    // ONE vocabulary, shared with the Folders grid's InventoryRow: see ConditionVocabulary.
    public string RowMarkGlyph => ConditionVocabulary.Glyph(StatusText);
    public IBrush RowMarkBrush => ConditionVocabulary.Brush(StatusText);

    /// <summary>Files present / files in scope: the one figure the row bar and its label both use (09-16).</summary>
    public int FilesInScope
    {
        get { int n = 0; foreach (var _ in ScopedFiles) n++; return n; }
    }
    public int FilesPresent
    {
        get { int n = 0; foreach (var f in ScopedFiles) if (Grog.Core.Sync.BackupScope.IsPresent(f)) n++; return n; }
    }
    /// <summary>The incomplete row's whole story: "8/10 at 62%". Empty when the bar is not shown.</summary>
    /// <summary>The fraction alone when idle (the percent would repeat it); the live percent joins it while a
    /// download feeds RowProgress.</summary>
    public string RowPercentText => !ShowRowBar ? "" : RowBusy ? $"{FilesPresent}/{FilesInScope} · {System.Math.Round(BarValue)}%" : $"{FilesPresent}/{FilesInScope}";

    /// <summary>The two figures SPLIT, so each sits in its own fixed, right-aligned cell and the digits
    /// line up down the column.</summary>
    /// <summary>The fraction beside the bar, and beside the resting dash too (owner 09-17: the count must stay
    /// visible on a row nothing has happened to; only the "Not in Scope" word has no figure).</summary>
    public string RowCountText => ShowRowCount ? $"{FilesPresent}/{FilesInScope}" : "";
    /// <summary>The fraction shows on every row that has files in scope, done ones included (owner 09-17: "users
    /// should know how many files there are"); only the "Not in Scope" word stands alone.</summary>
    public bool ShowRowCount => ShowRowBar || ((ShowRowIdle || ShowRowDone) && !NotInScope);
    /// <summary>The resting dash spans only the bar's cell when a count sits beside it; the "Not in Scope" word
    /// keeps the whole column.</summary>
    public int RowIdleSpan => NotInScope ? 3 : 1;
    /// <summary>The done tick spans only the bar's cell, like the resting dash, so its count sits beside it.</summary>
    public int RowDoneSpan => 1;
    /// <summary>The live percent only while a download feeds the row; idle, the fraction says it all (09-16).</summary>
    public string RowPctText   => ShowRowBar && RowBusy ? $"{System.Math.Round(BarValue)}%" : "";
    public bool ShowRowPct => ShowRowBar && RowBusy;

    /// <summary>The row is fully backed up: the tick alone, CENTERD in the status column between two fading
    /// rules -- "Backed Up" beside a green tick would say the same thing twice.</summary>
    public bool ShowRowDone => ShowRowMark && StatusText == "Backed Up";
    /// <summary>Nothing has happened to this row yet -- a RESTING state like "done": the word centered between
    /// two fading rules, no glyph. "Not in Scope" is deliberately at rest too, so it wears the same treatment.</summary>
    public bool ShowRowIdle => ShowRowMark && (StatusText is "Not Downloaded" or "Not in Scope"
                                               || (FilesPresent == 0 && StatusText is "Queued" or "Paused" or "Downloading"));

    /// <summary>Anything wrong: Missing, Corrupt, Update, Disconnected, Unavailable. NOT the centered, ruled
    /// treatment -- an exception must not read as restful. Colored glyph + word, right-aligned.</summary>
    public bool ShowRowIssue => ShowRowMark && !ShowRowDone && !ShowRowIdle;

    public bool ShowRowLabel => ShowRowMark && StatusText != "Backed Up";
    public string RowLabelText => ShowRowLabel ? StatusText : "";

    /// <summary>(UX 09-08 #7) The resting cell's text: a dim dash for a row nothing has happened to yet
    /// ("Not Downloaded" reads as a verdict when repeated down a column; the tooltip keeps the word), the
    /// "Not in Scope" word for the deliberate exclusion. Active, partial and issue states are untouched.</summary>
    public string RowIdleText => NotInScope ? RowLabelText : "\u2014";
    /// <summary>The idle cell's ink: dim for both resting states (UX 09-08 #7; the dash carries no urgency).</summary>
    public IBrush RowIdleBrush => Palette.InkDim;

    private void RaiseRowMark()
    {
        OnPropertyChanged(nameof(ShowRowBar)); OnPropertyChanged(nameof(ShowRowMark));
        OnPropertyChanged(nameof(ShowRowDone));   // derived like the others; a bound property with no raise never updates
        OnPropertyChanged(nameof(ShowRowIdle)); OnPropertyChanged(nameof(ShowRowIssue));
        OnPropertyChanged(nameof(RowMarkGlyph)); OnPropertyChanged(nameof(RowMarkBrush));
        OnPropertyChanged(nameof(RowPercentText)); OnPropertyChanged(nameof(RowCountText)); OnPropertyChanged(nameof(ShowRowCount)); OnPropertyChanged(nameof(RowIdleSpan));
        OnPropertyChanged(nameof(RowPctText)); OnPropertyChanged(nameof(ShowRowPct)); OnPropertyChanged(nameof(ShowRowLabel));
        OnPropertyChanged(nameof(RowLabelText)); OnPropertyChanged(nameof(RowIdleBrush)); OnPropertyChanged(nameof(RowIdleText)); OnPropertyChanged(nameof(FilesPresent)); OnPropertyChanged(nameof(FilesInScope));
    }

    partial void OnRowProgressChanged(double value) { OnPropertyChanged(nameof(BarValue)); OnPropertyChanged(nameof(BarBrush)); OnPropertyChanged(nameof(ShowRowProgress)); RaiseRowMark(); }
    partial void OnRowBusyChanged(bool value) { OnPropertyChanged(nameof(BarValue)); OnPropertyChanged(nameof(BarBrush)); OnPropertyChanged(nameof(ShowRowProgress)); RaiseRowMark(); }
    /// <summary>Displayed status is DERIVED from files + scope at read time (domain invariant). It was set once
    /// in FromItem, so a game whose last file landed mid-run kept "Partial" (amber, in the Partial bucket)
    /// until a full rebuild (sweep 2 #8). Every completion tick re-derives it from the same rule FromItem uses.</summary>
    public void RaiseCompletion() { if (Item is not null) Status = Grog.Core.Sync.BackupScope.Status(Item, Scope); OnPropertyChanged(nameof(CompletionPercent)); OnPropertyChanged(nameof(BarValue)); OnPropertyChanged(nameof(BarBrush)); OnPropertyChanged(nameof(ShowRowProgress)); RaiseRowMark(); }

    // --- cached portrait cover (grid column) ---
    public static string? ArtDir { get; set; }
    private Avalonia.Media.Imaging.Bitmap? _cover; private bool _coverTried;
    // (UI-thread sweep 09-06) _coverGen stamps each load; RaiseCover/DropCover bump it so a decode that lands
    // after the row was re-armed or released (or the cover swapped) is discarded instead of applied stale.
    private int _coverGen;
    public Avalonia.Media.Imaging.Bitmap? CoverBitmap
    {
        get
        {
            if (_coverTried) return _cover;
            _coverTried = true;   // one attempt per arm, hit or miss; RaiseCover re-arms
            if (ArtDir is null) return null;
            var p = Services.ArtCache.CoverPath(ArtDir, GogId);
            var gen = _coverGen;
            // (UI-thread sweep 09-06) File.Exists + decode happen on a worker: the art dir sits under the backup
            // root (often a slow/external drive) and this getter runs as rows scroll in. The decoded bitmap is
            // posted back to the UI thread; the getter returns null until then and the binding refreshes.
            // Decode to ~2x the 44px grid slot, NOT full box-art resolution: 600 full-res bitmaps in memory
            // was needless RAM.
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                Avalonia.Media.Imaging.Bitmap? bmp = null;
                try
                {
                    if (System.IO.File.Exists(p))
                        using (var fs = System.IO.File.OpenRead(p)) bmp = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(fs, 88);
                }
                catch { bmp = null; }
                if (bmp is null) return;   // a miss changes nothing: null was already returned and cached
                try
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (gen != _coverGen || !_coverTried) { bmp.Dispose(); return; }   // stale: row re-armed/released meanwhile
                        var prev = _cover;   // (UI-thread sweep 09-06 r2) normally null (RaiseCover cleared it); never leak a replaced one
                        _cover = bmp;
                        OnPropertyChanged(nameof(CoverBitmap));
                        prev?.Dispose();
                    });
                }
                catch { /* dispatcher gone (shutdown): drop the bitmap */ }
            });
            return _cover;
        }
    }
    // (UI-thread sweep 09-06 r2) the old bitmap is disposed on the UI thread AFTER the property change lands, so a
    // still-bound Image never paints a disposed bitmap: null the field, raise, then dispose.
    public void RaiseCover() { _coverGen++; _coverTried = false; var old = _cover; _cover = null; OnPropertyChanged(nameof(CoverBitmap)); old?.Dispose(); }
    /// <summary>Drop the decoded cover so it can be collected while Grog sits hidden in the tray. Keeps
    /// _coverTried set so the hidden binding returns null WITHOUT re-decoding; RaiseCover re-arms the decode.</summary>
    public void DropCover() { _coverGen++; if (_cover is null) return; var old = _cover; _cover = null; OnPropertyChanged(nameof(CoverBitmap)); old.Dispose(); }   // (UI-thread sweep 09-06 r2)
    public string LocationText { get; init; } = "";

    /// <summary>The source item, for count/logic access from the parent VM (not bound directly).</summary>
    public LibraryItem? Item { get; init; }

    /// <summary>True when the current scope includes NONE of this game's files. The derived status is
    /// Complete, but the DISPLAY must not claim a backup that never happened, so the row reads "Not in Scope".</summary>
    public bool NotInScope { get; init; }
    public string StoreUrl => Item?.StorePageUrl ?? "";
    public bool HasStoreUrl => !string.IsNullOrEmpty(StoreUrl);

    /// <summary>Delisted games get a permanent subtle red row tint -- a per-row "at risk, don't delete
    /// this" warning that lives on the game itself rather than as a dashboard number. A game whose owning
    /// account is removed/signed out is a DIFFERENT, quieter state: a subtle ORANGE tint (owner 08-30) --
    /// nothing is missing from GOG, it just can't be re-fetched until that account is reconnected.</summary>
    public bool IsDelisted => Item?.IsDelisted ?? false;
    public bool OwnerDisconnected { get; init; }
    /// <summary>The removed/signed-out account id(s) the game belongs to, named in the tooltip when known.</summary>
    public string DisconnectedOwners { get; init; } = "";
    // OwnerDisconnected OUTRANKS a delisted flag: with the owning account gone, "delisted" is a claim
    // nobody can verify any more (the next sweep withdraws it). The full-row orange tint was dropped
    // 08-31 (owner call: with a whole zero-account library flagged it was wallpaper, not signal) --
    // the orange "(account removed)" caption under the status word carries the state instead.
    public IBrush RowBackground => !ShowOwnerCaption && IsDelisted
        ? Palette.SurfaceErrorDark2   // subtle red tint
        : new SolidColorBrush(Colors.Transparent);
    /// <summary>Registered but signed out (no readable token): re-fetch needs only a Log In.</summary>
    public bool OwnerLoggedOut { get; init; }
    public bool ShowOwnerCaption => OwnerDisconnected || OwnerLoggedOut;
    /// <summary>Second line under the status cell, naming WHY the row is flagged so the orange is
    /// never a guess. Removed (needs re-add) and logged out (needs Log In) are named apart.</summary>
    public string OwnerCaption => OwnerDisconnected ? "(account removed)"
        : OwnerLoggedOut ? "(account logged out)" : "";
    public string? DelistedTip => OwnerLoggedOut
        ? $"This game belongs to a GOG account{(DisconnectedOwners.Length > 0 ? $" ({DisconnectedOwners})" : "")} that is logged out. Your backup is safe; log back in to download the rest."
        : OwnerDisconnected
        ? $"This game belongs to a GOG account{(DisconnectedOwners.Length > 0 ? $" ({DisconnectedOwners})" : "")} that is removed or signed out. Your backup is safe, but it can't be re-downloaded until that account is reconnected."
        : IsDelisted
            ? "At risk: this game is no longer available on GOG's store. You still own it, so keep your backup safe; you may not be able to re-download it later."
            : null;

    [ObservableProperty] private BackupStatus _status;

    /// <summary>The game's DERIVED activity: NOT stored state, with exactly ONE writer -- the VM's central
    /// RecomputeActivity, driven by the persisted queues. Idle = show the on-disk Status.</summary>
    private Grog.Core.Sync.FileActivity _derivedActivity = Grog.Core.Sync.FileActivity.Idle;
    public bool HasActivity => _derivedActivity != Grog.Core.Sync.FileActivity.Idle;

    /// <summary>Push the freshly-derived activity in (the single writer). Refreshes the dot/label + bar.</summary>
    public void SetActivity(Grog.Core.Sync.FileActivity a)
    {
        if (_derivedActivity == a) return;
        _derivedActivity = a;
        RowBusy = a is Grog.Core.Sync.FileActivity.Downloading or Grog.Core.Sync.FileActivity.Moving
                    or Grog.Core.Sync.FileActivity.Verifying or Grog.Core.Sync.FileActivity.Active;
        RaiseStatusDisplay();
    }

    partial void OnStatusChanged(BackupStatus value) => RaiseStatusDisplay();
    private void RaiseStatusDisplay()
    {
        OnPropertyChanged(nameof(HasActivity));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
    }

    public IBrush StatusBrush => _derivedActivity switch
    {
        Grog.Core.Sync.FileActivity.Downloading     => Palette.AccentAmber,
        Grog.Core.Sync.FileActivity.QueuedToDownload => Palette.InkMuted,
        Grog.Core.Sync.FileActivity.DownloadPaused   => Palette.AccentOrange,
        Grog.Core.Sync.FileActivity.Moving           => Palette.AccentAmber,
        Grog.Core.Sync.FileActivity.QueuedToMove     => Palette.InkMuted,
        Grog.Core.Sync.FileActivity.MovePaused       => Palette.AccentOrange,
        Grog.Core.Sync.FileActivity.Verifying        => Palette.AccentAmber,
        Grog.Core.Sync.FileActivity.Active           => Palette.AccentAmber,
        _ => Status switch   // Idle -> the on-disk condition
        {
            // A game with NOTHING IN SCOPE is not an achievement: green would claim work that never
            // happened. Quiet gray, matching its "Not in Scope" word.
            BackupStatus.Complete when NotInScope => Palette.InkDim,
            BackupStatus.Complete        => Palette.SuccessGreenVivid,
            BackupStatus.Partial         => Palette.AccentAmberDeep,
            BackupStatus.Outdated => Palette.AccentOrange,
            BackupStatus.Missing         => Palette.AccentAmber,
            BackupStatus.Corrupt           => Palette.ErrorRedBright,
            _                            => Palette.InkMuted,
        },
    };

    public string StatusText => _derivedActivity switch
    {
        Grog.Core.Sync.FileActivity.Downloading     => "Downloading",
        Grog.Core.Sync.FileActivity.QueuedToDownload => "Queued",
        Grog.Core.Sync.FileActivity.DownloadPaused   => "Paused",
        Grog.Core.Sync.FileActivity.Moving           => "Moving",
        Grog.Core.Sync.FileActivity.QueuedToMove     => "Queued to move",
        Grog.Core.Sync.FileActivity.MovePaused       => "Move Paused",
        Grog.Core.Sync.FileActivity.Verifying        => "Verifying",
        Grog.Core.Sync.FileActivity.Active           => "Active",
        _ => ConditionVocabulary.Word(Status, NotInScope),   // Idle -> the on-disk condition
    };

    public static GameRowViewModel FromItem(LibraryItem i, bool includeExtras = true, bool includeGames = true,
        string? primaryRootId = null, Grog.Core.Sync.Scope? scope = null,
        IReadOnlyCollection<string>? registeredAccounts = null,
        IReadOnlyCollection<string>? connectedAccounts = null)
    {
        // Owned only by accounts no longer registered/signed in: a DIFFERENT state from delisted
        // (owner 08-30) - GOG did not pull it, the owning account just is not connected any more.
        bool ownerDisconnected = false, ownerLoggedOut = false;
        string disconnectedOwners = "";
        // null = the caller has no account knowledge (harness/tests) - never flag. An EMPTY list is real
        // knowledge (last account removed) and still flags (owner 08-31). Fully-backed-up games stay
        // plain - the flag marks files that can no longer be FETCHED, and a game with no gap needs none.
        if (registeredAccounts is not null)
        {
            var owners = new HashSet<string>(i.Files.SelectMany(f => f.OwnerIds));
            bool gap = Grog.Core.Sync.BackupScope.Scoped(i, scope ?? Grog.Core.Sync.Scope.Both)
                .Any(f => !Grog.Core.Sync.BackupScope.HasLocalCopy(f));
            // With ZERO accounts registered NOTHING can be fetched, so every gap flags -- including
            // files with pre-account "" owner stamps, which is why the owner's zero-account library
            // only partially flagged (owner 08-31). With accounts present, "" stays unflagged (it is
            // rewritten to a real account by the account service and can't be proven orphaned).
            bool orphaned = registeredAccounts.Count == 0
                || (owners.Count > 0 && !owners.Contains("") && !owners.Overlaps(registeredAccounts));
            ownerDisconnected = orphaned && gap;
            // Registered but SIGNED OUT (no readable token): the quieter cousin - re-fetch needs only a
            // Log In, not a re-add. Same zero/"" rules as above, against the connected set (owner 08-31).
            if (!ownerDisconnected && gap && connectedAccounts is not null)
                ownerLoggedOut = connectedAccounts.Count == 0
                    || (owners.Count > 0 && !owners.Contains("") && !owners.Overlaps(connectedAccounts));
            if (ownerDisconnected || ownerLoggedOut)
                disconnectedOwners = string.Join(", ", owners.Where(o => o.Length > 0).OrderBy(o => o));
        }
        var oses = string.Join("/", i.Files.Where(f => f.Kind is FileKind.Installer)
            .Select(f => f.Os).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s));
        int extras = i.Files.Count(f => f.Kind == FileKind.Extra);
        // Where the game's files actually live. Role is the name -- an id is never the answer. Routing can
        // legitimately split a game across both drives, so say so rather than picking one.
        var roots = i.Files
            .Where(Grog.Core.Sync.BackupScope.IsPresent)
            .Select(f => f.RootId ?? primaryRootId)
            .Where(r => !string.IsNullOrEmpty(r))
            .Distinct()
            .ToList();
        string driveText = roots.Count switch
        {
            0 => "--",
            1 => roots[0] == primaryRootId ? "Primary" : "Secondary",
            _ => "Split",
        };
        return new GameRowViewModel
        {
            GogId = i.GogId,
            Title = i.Title,
            Type = i.Type,
            Status = Grog.Core.Sync.BackupScope.Status(i, scope ?? Grog.Core.Sync.Scope.Both),   // full scope
            NotInScope = !Grog.Core.Sync.BackupScope.Scoped(i, scope ?? Grog.Core.Sync.Scope.Both).Any(),
            Scope = scope ?? Grog.Core.Sync.Scope.Both,
            Item = i,
            OsSummary = oses,
            ExtrasSummary = extras > 0 ? $"{extras}" : "",
            FileCount = Grog.Core.Sync.BackupScope.Scoped(i, scope ?? Grog.Core.Sync.Scope.Both).Count(),
            IncludeExtras = includeExtras,
            IncludeGames = includeGames,
            LocationText = driveText,
            OwnerDisconnected = ownerDisconnected,
            OwnerLoggedOut = ownerLoggedOut,
            DisconnectedOwners = disconnectedOwners,
        };
    }
}

/// <summary>A selectable library game in the Find-match picker.</summary>
public sealed record GamePick(long GogId, string Title);

public sealed record SerialEntry(string Name, string Key)
{
    public bool HasName => !string.IsNullOrEmpty(Name);
}

/// <summary>One age-rating board row in the detail pane's Ratings section: Board acronym, Age display text,
/// hover Tooltip; HasRating is false when GOG lists no rating for this board (rendered "NA").</summary>
public sealed record RatingEntry(string Board, string Age, string Tooltip, bool HasRating);

/// <summary>Static facts about the age-rating boards GOG reports, keyed by the lowercase board key
/// harvested from the v2 response (pegi/esrb/usk/br/gog). Drives the acronym label and the (?) tooltip.</summary>
public static class RatingBoards
{
    // key -> (display acronym, tooltip)
    private static readonly Dictionary<string, (string Acronym, string Tip)> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["pegi"] = ("PEGI", "PEGI - Pan European Game Information. The European age-rating system."),
            ["esrb"] = ("ESRB", "ESRB - Entertainment Software Rating Board. The North American (US, Canada, Mexico) system."),
            ["usk"]  = ("USK",  "USK - Unterhaltungssoftware Selbstkontrolle. Germany's age-rating system."),
            ["br"]   = ("BR",   "BR - ClassInd (DJCTQ). Brazil's advisory rating system."),
            ["gog"]  = ("GOG",  "GOG - GOG.com's own store age rating."),
        };

    /// <summary>Fixed display order. Every game's card lists all of these; a board GOG doesn't rate shows "NA".</summary>
    private static readonly string[] Order = { "pegi", "esrb", "usk", "br", "gog" };

    public static string Acronym(string key) => Map.TryGetValue(key, out var v) ? v.Acronym : key.ToUpperInvariant();
    public static string Tip(string key) => Map.TryGetValue(key, out var v) ? v.Tip : key.ToUpperInvariant();

    /// <summary>Every canonical board in display order, plus any board GOG adds later that we don't yet know
    /// about (appended after the known set so nothing the API returns is silently dropped).</summary>
    public static IEnumerable<string> All(IEnumerable<string> presentKeys)
    {
        var extra = new HashSet<string>(presentKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var k in Order) { extra.Remove(k); yield return k; }
        foreach (var k in extra) yield return k;
    }

    /// <summary>The canonical five board keys, in display order.</summary>
    public static IReadOnlyList<string> Keys => Order;
}

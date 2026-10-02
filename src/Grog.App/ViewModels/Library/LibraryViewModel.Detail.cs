// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Download;
using Grog.Core.Format;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.App.Runtime;

namespace Grog.App.ViewModels;

// Library page (S3.2): the detail pane (meta, ratings, serials, per-section file lists, changelog), its art, the
// conditional chips and the file-type legend, and the missing-files notice.
public sealed partial class LibraryViewModel
{
    // ---- Detail pane ----
    [ObservableProperty] private GameRowViewModel? _selectedRow;

    partial void OnSelectedRowChanged(GameRowViewModel? value) => RaiseDetail();

    /// <summary>The guide's example game, held so the pane keeps showing it after the tour clears the
    /// selection (queueing the tour's file must clear it, or the primary reads "Back up . 1 item").</summary>

    internal GameRowViewModel? DetailSource => SelectedRow ?? _root.Guide.DetailKeep ?? Games.FirstOrDefault();

    // The pane has content whenever there is a game to show (selected or previewed); only a truly empty
    // library falls back to the quiet placeholder.
    public bool DetailHasSelection => DetailSource is not null;
    private double _detailWidth = 470;   // room for art + details
    /// <summary>Detail-pane width: stored and TwoWay-bound to the splitter, so a manual resize sticks
    /// across row selections.</summary>
    public Avalonia.Controls.GridLength DetailColumnWidth
    {
        get => new(_detailWidth);
        set
        {
            if (!value.IsAbsolute) return;                 // the splitter only produces absolute widths here
            var w = Math.Clamp(value.Value, DetailMinWidth, 760);
            if (Math.Abs(w - _detailWidth) < 0.5) return;
            _detailWidth = w;
            OnPropertyChanged();
        }
    }
    // Floor matches the default width: the bottom trigger cell (Back up / Rescan buttons) shares this
    // column, so the pane can't shrink below where that button row fits without clipping the primary CTA.
    public double DetailMinWidth => 340;

    public string DetailTitle => DetailSource?.Title ?? "";
    public string DetailStatus => DetailSource?.StatusText ?? "";
    public IBrush DetailStatusBrush => DetailSource?.StatusBrush ?? new SolidColorBrush(Colors.Gray);
    public string DetailMeta
    {
        get
        {
            var it = DetailSource?.Item;
            if (it is null) return "";
            var bits = new List<string>();
            if (it.ReleaseDate is { } rd) bits.Add(rd.Year.ToString());
            if (!string.IsNullOrEmpty(DetailSource!.OsSummary)) bits.Add(DetailSource.OsSummary);
            if (it.InDevelopment) bits.Add("in development");
            return string.Join(" · ", bits);
        }
    }
    public string DetailDrive
    {
        get
        {
            var item = DetailSource?.Item;
            if (item is null || _manifest is null) return "--";
            var rid = item.Files.Select(f => f.RootId).FirstOrDefault(r => !string.IsNullOrEmpty(r))
                      ?? _manifest.Current.PrimaryRootId;
            if (string.IsNullOrEmpty(rid)) return "--";
            if (rid == _manifest.Current.PrimaryRootId) return "Primary";
            var root = _manifest.Current.Roots.FirstOrDefault(r => r.Id == rid);
            return !string.IsNullOrEmpty(root?.Label) ? root.Label! : "Secondary";
        }
    }
    public string DetailSize => DetailSource?.TotalSizeText ?? "";

    /// <summary>When the product entered the library, from GOG's order history (<see cref="Grog.Core.Runs.PurchaseDatesRun"/>).
    /// Local date, no time: the hour money moved is noise. Some owned items appear in no order at all
    /// (Connect imports, bundled DLC, pre-order grants), so the row HIDES rather than showing "--" -- an
    /// absent date is a gap in GOG's history, not a fact about the game worth a line of its own.</summary>
    public string DetailAcquired => DetailSource?.Item?.DateAcquired is { } d ? d.ToLocalTime().ToString("d MMM yyyy") : "";
    public bool DetailHasAcquired => DetailSource?.Item?.DateAcquired is not null;
    public string DetailLanguages => DetailSource?.Item?.Languages is { Count: >0 } l ? string.Join(", ", l.Take(6)) : "--";
    /// <summary>A section with nothing to say is hidden, not shown as "--".</summary>
    public bool DetailHasLanguages => DetailSource?.Item?.Languages is { Count: > 0 };

    /// <summary>The pane's plain-words ownership line: ALL owners, ignoring the grid's owner filter -- the
    /// pane states attribution truth, not the current view. Hidden with a single account.</summary>
    public bool DetailShowOwners => OwnerBadges.Show && DetailSource?.Item is not null;
    public string DetailOwners
    {
        get
        {
            if (DetailSource?.Item is not { } item) return "";
            var names = new List<string>();
            foreach (var b in OwnerBadges.Map.Values)
                if (item.OwnerIds.Contains(b.OwnerId)) names.Add(b.Name);
            return names.Count == 0 ? "--" : string.Join(", ", names);
        }
    }

    /// <summary>Per-board age ratings for the selected game, in canonical PEGI/ESRB/USK/BR order, each with
    /// an acronym chip, an "N+" age, and a (?) tooltip. Empty when GOG lists no boards for the product.</summary>
    public IReadOnlyList<RatingEntry> DetailRatings
    {
        get
        {
            var r = DetailSource?.Item?.AgeRatings;
            if (r is null || r.Count == 0) return System.Array.Empty<RatingEntry>();
            var list = new List<RatingEntry>();
            foreach (var key in RatingBoards.All(r.Keys))
            {
                bool has = r.TryGetValue(key, out var age) && age > 0;
                list.Add(new RatingEntry(RatingBoards.Acronym(key), has ? $"{age}+" : "", RatingBoards.Tip(key), has));
            }
            return list;
        }
    }
    public bool DetailHasRatings => DetailRatings.Count > 0;
    public string DetailStoreUrl => DetailSource?.Item?.StorePageUrl ?? "";
    public bool DetailHasStoreUrl => !string.IsNullOrEmpty(DetailStoreUrl);
    public string DetailSerial => DetailSource?.Item?.SerialKey ?? "";
    public bool DetailHasSerial => !string.IsNullOrEmpty(DetailSerial);

    /// <summary>Serial keys split into (product name, key) so the UI can bold the name apart from the key.
    /// Handles the "Name: KEY" per-line form; a bare key becomes an entry with an empty name.</summary>
    public IReadOnlyList<SerialEntry> DetailSerials
    {
        get
        {
            var raw = DetailSource?.Item?.SerialKey;
            if (string.IsNullOrWhiteSpace(raw)) return System.Array.Empty<SerialEntry>();
            var list = new List<SerialEntry>();
            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int i = line.IndexOf(':');
                if (i > 0 && i < line.Length - 1) list.Add(new SerialEntry(line[..i].Trim(), line[(i + 1)..].Trim()));
                else list.Add(new SerialEntry("", line.Trim()));
            }
            return list;
        }
    }

    // ---- Changelog display in the detail pane ----
    [ObservableProperty] private string _detailChangelog = "";
    [ObservableProperty] private bool _changelogVisible;

    public ObservableCollection<DetailFileRow> DetailFiles { get; } = new();
    public ObservableCollection<DetailFileRow> DetailGameFiles { get; } = new();
    /// <summary>(09-25) One row per platform this game HAS installers for when none of them is in scope (the badge only).</summary>
    public ObservableCollection<DetailFileRow> DetailOutOfScopeOs { get; } = new();
    public bool HasOutOfScopeInstallers => DetailOutOfScopeOs.Count > 0;
    public ObservableCollection<DetailFileRow> DetailExtraFiles { get; } = new();
    public bool HasExtraFiles => DetailExtraFiles.Count > 0;
    /// <summary>Downloaded files the current scope excludes: they hold bytes, so they stay listed (collapsed) for per-file Delete.</summary>
    public ObservableCollection<DetailFileRow> DetailOutOfScopeOnDisk { get; } = new();
    public bool HasOutOfScopeOnDisk => DetailOutOfScopeOnDisk.Count > 0;
    public string OutOfScopeOnDiskHeader => $"On disk, outside scope ({DetailOutOfScopeOnDisk.Count})";
    [ObservableProperty] private bool _outOfScopeOnDiskExpanded;
    [RelayCommand] private void ToggleOutOfScopeOnDisk() => OutOfScopeOnDiskExpanded = !OutOfScopeOnDiskExpanded;
    public bool HasGameFiles => DetailGameFiles.Count > 0;
    // A section's header shows a green check when every file under it is downloaded.
    public bool GameFilesAllDone => DetailGameFiles.Count > 0 && DetailGameFiles.All(r => Grog.Core.Sync.BackupScope.IsPresent(r.State));
    public bool ExtrasAllDone => DetailExtraFiles.Count > 0 && DetailExtraFiles.All(r => Grog.Core.Sync.BackupScope.IsPresent(r.State));
    // Show the section's "download all" only while at least one file under it still needs queuing.
    public bool GameFilesShowDownload => DetailGameFiles.Any(r => r.ShowDownloadButton);
    public bool ExtrasShowDownload => DetailExtraFiles.Any(r => r.ShowDownloadButton);
    internal void RaiseSectionDone()
    {
        OnPropertyChanged(nameof(GameFilesAllDone)); OnPropertyChanged(nameof(ExtrasAllDone));
        OnPropertyChanged(nameof(GameFilesShowDownload)); OnPropertyChanged(nameof(ExtrasShowDownload));
    }

    internal void RaiseDetail()
    {
        OnPropertyChanged(nameof(DetailLogoBitmap)); OnPropertyChanged(nameof(HasDetailArt));
        OnPropertyChanged(nameof(DetailColumnWidth)); OnPropertyChanged(nameof(DetailMinWidth));
        OnPropertyChanged(nameof(DetailHasSelection));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(DetailStatus));
        OnPropertyChanged(nameof(DetailStatusBrush));
        OnPropertyChanged(nameof(DetailMeta));
        OnPropertyChanged(nameof(DetailDrive));
        OnPropertyChanged(nameof(DetailSize));
        OnPropertyChanged(nameof(DetailAcquired));
        OnPropertyChanged(nameof(DetailHasAcquired));
        OnPropertyChanged(nameof(DetailLanguages));
        OnPropertyChanged(nameof(DetailHasLanguages));
        OnPropertyChanged(nameof(DetailShowOwners));
        OnPropertyChanged(nameof(DetailOwners));
        OnPropertyChanged(nameof(DetailRatings));
        OnPropertyChanged(nameof(DetailHasRatings));
        OnPropertyChanged(nameof(DetailStoreUrl));
        OnPropertyChanged(nameof(DetailHasStoreUrl));
        OnPropertyChanged(nameof(DetailSerial));
        OnPropertyChanged(nameof(DetailSerials));
        OnPropertyChanged(nameof(DetailHasSerial));
        DetailFiles.Clear();
        DetailGameFiles.Clear();
        DetailExtraFiles.Clear();
        DetailOutOfScopeOnDisk.Clear();
        if (DetailSource?.Item is { } it)
        {
            foreach (var f in it.Files)
            {
                var row = new DetailFileRow(f) { GogId = it.GogId };
                // Push the file's derived activity onto the fresh row from the current map (single source).
                row.SetActivity(Grog.Core.Sync.ActivityMap.ForFile(new Grog.Core.Sync.FileRef(it.GogId, f.FileKey), _root.ActivityMap));
                DetailFiles.Add(row);
                // Only list files IN SCOPE -- the SAME predicate the queue + health tally use. Installers are
                // platform- AND language-scoped; extras are language-scoped + Extras-toggle gated.
                bool inScope = row.IsExtra ? _includeExtras && EffectiveScope.IncludesLanguage(f) : EffectiveScope.Includes(f);
                if (inScope) { if (row.IsExtra) DetailExtraFiles.Add(row); else DetailGameFiles.Add(row); }
                // Out of scope but holding bytes: a scope change must not hide a file the user may want to delete.
                // A copy Verify found gone is not on disk, so it is not listed here.
                else if (Grog.Core.Sync.BackupScope.HoldsBytes(f) && f.State != FileState.Missing) { row.OutOfScope = true; DetailOutOfScopeOnDisk.Add(row); }
            }
            // A slot GOG withdrew while we hold its bytes lives in the archive list; it is still the user's to delete.
            foreach (var f in it.OldVersionFiles.Where(BackupScope.IsWithdrawnOnDisk))
                DetailOutOfScopeOnDisk.Add(new DetailFileRow(f) { GogId = it.GogId, OutOfScope = true });
        }
        // (owner 09-25) A game whose installers are ALL outside the scope still lists its extras: say why the
        // installers are missing, name the platforms they exist for, and point at the setting.
        DetailOutOfScopeOs.Clear();
        // Only when installers are in scope at all, and only platforms the scope leaves out (review 09-25: a game
        // whose installers were excluded by LANGUAGE listed in-scope platforms as "out of scope").
        if (DetailGameFiles.Count == 0 && _includeGames)
        {
            var scope = EffectiveScope;
            foreach (var r in DetailFiles.Where(r => !r.IsExtra && r.ShowOsBadge && !scope.IncludesPlatform(r.File))
                                         .GroupBy(r => r.OsBadge).Select(g => g.First()))
                DetailOutOfScopeOs.Add(r);
        }
        OnPropertyChanged(nameof(HasExtraFiles));
        OnPropertyChanged(nameof(HasGameFiles));
        OnPropertyChanged(nameof(HasOutOfScopeInstallers));
        OnPropertyChanged(nameof(HasOutOfScopeOnDisk)); OnPropertyChanged(nameof(OutOfScopeOnDiskHeader));
        RaiseSectionDone();   // refresh the per-section download-all / done state for the new selection
        ChangelogVisible = false;
        DetailChangelog = "";
    }

    /// <summary>True when the in-scope library contains localization (language-pack) content -- the same
    /// signal the Library extras breakdown uses, so both surfaces agree.</summary>
    public bool HasLocalizationContent =>
        _stats.ExtraTypes.Any(t => string.Equals(t.Name, "Localizations", StringComparison.OrdinalIgnoreCase));

    /// <summary>Alt versions (language-less alternate builds) present in the library -- drives its chip's
    /// visibility. Unlike Localizations it is NOT scope-gated, so it shows whenever any such content exists.</summary>
    public bool HasAltVersionContent =>
        _stats.ExtraTypes.Any(t => string.Equals(t.Name, "Alternate versions", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(t.Name, "Alt Versions", StringComparison.OrdinalIgnoreCase));

    /// <summary>Show the Localizations chip only when in-scope localization content exists; show Alt versions
    /// whenever the library has any. Called after the chip list is built and on stats/scope refresh.</summary>
    internal void RefreshConditionalChips()
    {
        foreach (var c in ExtraFilterChips)
        {
            if (string.Equals(c.Value, "Localizations", StringComparison.OrdinalIgnoreCase)) c.IsVisible = HasLocalizationContent;
            else if (string.Equals(c.Value, "Alt Versions", StringComparison.OrdinalIgnoreCase)) c.IsVisible = HasAltVersionContent;
        }
    }

    /// <summary>The extras legend and the two conditional chips follow the stats: raised by the Overview's segment rebuild.</summary>
    internal void RaiseLegend()
    {
        OnPropertyChanged(nameof(HasLocalizationContent)); OnPropertyChanged(nameof(HasAltVersionContent)); OnPropertyChanged(nameof(TypeLegend));
        RefreshConditionalChips();
    }

    /// <summary>Legend for the file-type colors under the two grids. Localizations appears only when real
    /// localization content is in scope, matching the Library extras breakdown.</summary>
    public IReadOnlyList<ExtraTypeSegment> TypeLegend
    {
        get
        {
            var list = new List<ExtraTypeSegment>
            {
                new("Game", Palette.AccentAmber),
                new("Soundtracks", ExtraTypeSegment.ColorFor("Soundtracks")),
                new("Videos", ExtraTypeSegment.ColorFor("Videos")),
                new("Art", ExtraTypeSegment.ColorFor("Art")),
                new("Manuals", ExtraTypeSegment.ColorFor("Manuals")),
            };
            if (HasLocalizationContent)
                list.Add(new ExtraTypeSegment("Localizations", ExtraTypeSegment.ColorFor("Localizations")));
            list.Add(new ExtraTypeSegment("Alt Versions", ExtraTypeSegment.ColorFor("Alt Versions")));
            list.Add(new ExtraTypeSegment("Other", ExtraTypeSegment.ColorFor("Other")));
            return list;
        }
    }

    // --- cover art: download+cache pipeline + display toggles (evaluation) ---
    public bool ShowDetailArt
    {
        get => _settings.ShowDetailArt;
        set { if (_settings.ShowDetailArt == value) return; _settings.ShowDetailArt = value; _settings.SaveSoon(); OnPropertyChanged(); OnPropertyChanged(nameof(DetailLogoBitmap)); OnPropertyChanged(nameof(HasDetailArt)); }
    }
    public bool ShowGridArt
    {
        get => _settings.ShowGridArt;
        set { if (_settings.ShowGridArt == value) return; _settings.ShowGridArt = value; _settings.SaveSoon(); OnPropertyChanged(); foreach (var r in _allGames) r.RaiseCover(); }
    }

    /// <summary>No art (or art switched off) means no band at all, rather than an empty gradient block.</summary>
    public bool HasDetailArt => DetailLogoBitmap is not null;

    // (UI-thread sweep 09-06) The decode used to run inline in this getter (File.Exists + OpenRead + decode on
    // the backup drive, on every selection change). Now: the getter answers from a per-GogId cache and, when
    // the cache is for another game or the last probe missed, kicks a worker probe that posts back to the UI
    // thread and raises the property. A miss never raises (null was already the answer), so probe -> raise ->
    // probe cannot loop; the next selection/refresh raise simply re-probes, as the old getter re-read disk.
    private long _detailArtId = -1;
    private Avalonia.Media.Imaging.Bitmap? _detailArt;
    private bool _detailArtProbing;
    private int _detailArtGen;
    /// <summary>Art for the open item's detail banner (from the local cover cache).</summary>
    public Avalonia.Media.Imaging.Bitmap? DetailLogoBitmap
    {
        get
        {
            if (!ShowDetailArt || DetailSource is null || GameRowViewModel.ArtDir is null || _root.WindowHidden)
            {
                // Nothing to show: release the cached decode so tray-hiding actually frees it.
                _detailArtGen++; _detailArtProbing = false; _detailArtId = -1; _detailArt = null;
                return null;
            }
            var id = DetailSource.GogId;
            if (_detailArtId == id && _detailArt is not null) return _detailArt;   // cache hit
            if (_detailArtProbing && _detailArtId == id) return null;             // already in flight for this game
            var p = Services.ArtCache.GalaxyPath(GameRowViewModel.ArtDir, id);
            var gen = ++_detailArtGen;
            _detailArtId = id; _detailArt = null; _detailArtProbing = true;
            _ = Task.Run(() =>
            {
                Avalonia.Media.Imaging.Bitmap? bmp = null;
                try
                {
                    // Decode at display width, not source (~1600px banners): less RGBA memory and a faster decode.
                    // 520 covers a widened detail pane.
                    if (File.Exists(p))
                        using (var fs = File.OpenRead(p)) bmp = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(fs, 520);
                }
                catch { bmp = null; }
                try
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (gen != _detailArtGen) { bmp?.Dispose(); return; }   // stale: selection moved on
                        _detailArtProbing = false;
                        if (bmp is null) return;                                // miss: cached as "tried", no raise
                        _detailArt = bmp;
                        OnPropertyChanged(nameof(DetailLogoBitmap)); OnPropertyChanged(nameof(HasDetailArt));
                    });
                }
                catch { bmp?.Dispose(); /* dispatcher gone (shutdown) */ }
            });
            return null;
        }
    }

    /// <summary>Open the selected game's GOG store page in the default browser.</summary>
    [RelayCommand]
    private void OpenStorePage()
    {
        _root.OpenUrl(DetailStoreUrl);
    }

    /// <summary>Set by the startup existence pass: files the manifest holds as backed up but that are no
    /// longer on disk. Lives on the storage card, next to the drives it is about.</summary>
    [ObservableProperty] private string _missingNotice = "";
    public bool HasMissingNotice => !string.IsNullOrEmpty(MissingNotice);
    partial void OnMissingNoticeChanged(string value) => OnPropertyChanged(nameof(HasMissingNotice));
}

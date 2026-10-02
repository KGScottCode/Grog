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

public partial class DetailFileRow : ObservableObject
{
    private readonly GameFile _f;
    public DetailFileRow(GameFile f) => _f = f;

    public GameFile File => _f;
    public long GogId { get; init; }
    public string Name => TitleCase(string.IsNullOrWhiteSpace(_f.Name) ? _f.Kind.ToString() : _f.Name);

    /// <summary>Installer language (e.g. "English") -- the axis that distinguishes a game's repeated per-language
    /// builds. Empty for extras and single-shape entries.</summary>
    public string LanguageText => IsExtra || string.IsNullOrWhiteSpace(_f.Language) ? "" : TitleCase(_f.Language);

    /// <summary>Row identity line. Installers read by LANGUAGE (the game name is already the pane header);
    /// extras keep their descriptive name. Falls back to Name when an installer carries no language.</summary>
    public string RowTitle => IsExtra ? Name : (LanguageText.Length > 0 ? LanguageText : Name);

    /// <summary>Secondary line: the installer's version when GOG supplies one, else empty (row stays one line).</summary>
    public string RowSubtitle
    {
        get
        {
            var v = IsExtra || string.IsNullOrWhiteSpace(_f.Version) ? "" : $"Version {_f.Version!.Trim()}";
            // A slot GOG no longer lists: the copy on disk is all that is left of it, so the row says so.
            if (!_f.WithdrawnByGog) return v;
            return v.Length == 0 ? "Withdrawn by GOG" : v + " · withdrawn by GOG";
        }
    }
    public bool ShowSubtitle => RowSubtitle.Length > 0;

    /// <summary>Capitalize the first letter of each word, preserving the rest (so "soundtrack" -> "Soundtrack"
    /// and "HD wallpaper" -> "HD Wallpaper" without mangling acronyms).</summary>
    private static string TitleCase(string s)
    {
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
            words[i] = words[i].Length == 0 ? words[i] : char.ToUpperInvariant(words[i][0]) + words[i][1..];
        return string.Join(' ', words);
    }
    public bool IsExtra => _f.Kind == FileKind.Extra;
    /// <summary>Badge column floor in the details row: 48 holds an OS badge, 116 a type chip ("Soundtracks").
    /// Measured 08-2x; the shared row template reads it per row.</summary>
    public double BadgeColumnMinWidth => IsExtra ? 116 : 48;
    /// <summary>Per-file OS badge (installers carry an OS; extras are OS-neutral). Shows which platform this
    /// installer is for, since the library holds Windows/Mac/Linux builds side by side.</summary>
    public string OsBadge => PlatformNames.Badge(_f.Os);
    public bool ShowOsBadge => OsBadge.Length > 0;
    // The OS reads in its own bright platform hue (a separate axis from the content-type colors).
    public IBrush OsBadgeBrush => ExtraTypeSegment.ColorFor(_f.Os ?? "");
    /// <summary>Byte progress for this file, 0..100. Only meaningful while it's in flight.</summary>
    public double Percent => (_f.ExpectedSizeBytes is > 0 && _f.LocalSizeBytes is > 0)
        ? System.Math.Clamp(100.0 * _f.LocalSizeBytes.Value / _f.ExpectedSizeBytes.Value, 0, 100) : 0;

    /// <summary>The extra's bucket, classified exactly as the Extras legend does, so the inline pill and
    /// the dashboard legend always name (and color) a file the same way.</summary>
    private string ExtraBucket => Grog.Core.Sync.ExtraClassifier.Classify(_f.Name, _f.ExtraType);

    public string Descriptor => IsExtra ? Grog.Core.Sync.ExtraClassifier.ShortLabel(ExtraBucket) : "";

    /// <summary>The legend's color for this extra's bucket, so the inline pill's dot matches the legend swatch.</summary>
    public IBrush ExtraTypeBrush => IsExtra ? ExtraTypeSegment.ColorFor(ExtraBucket) : Brushes.Transparent;

    // Type badge = EXACTLY the active filter-pill treatment (fill = base hue @0x2E, border = base brush, text
    // = base hue full opacity); same ColorFor source, so a type's badge and filter pill are pixel-identical.
    private Color ExtraColor => (ExtraTypeBrush as ISolidColorBrush)?.Color ?? Colors.Gray;
    public IBrush ExtraTypeFill   => new SolidColorBrush(Color.FromArgb(46, ExtraColor.R, ExtraColor.G, ExtraColor.B));
    public IBrush ExtraTypeBorder => ExtraTypeBrush;
    public IBrush ExtraTypeText   => new SolidColorBrush(Color.FromArgb(255, ExtraColor.R, ExtraColor.G, ExtraColor.B));

    public string SizeText
    {
        get
        {
            var b = Grog.Core.Sync.Rollups.HeldBytesOf(_f);
            if (b <= 0) return "";
            return b >= 1L<<30 ? $"{b/(double)(1L<<30):0.0} GB"
                 : b >= 1L<<20 ? $"{b/(double)(1L<<20):0} MB" : $"{b/(double)(1L<<10):0} KB";
        }
    }

    // --- Derived activity. Pushed in by the VM's RecomputeActivity -- the single writer -- from the persisted
    //     download queue + live engine. DownloadPercent is the live byte readout, refreshed while a transfer runs.
    private Grog.Core.Sync.FileActivity _activity = Grog.Core.Sync.FileActivity.Idle;
    [ObservableProperty] private double _downloadPercent;

    public void SetActivity(Grog.Core.Sync.FileActivity a)
    {
        if (_activity == a) return;
        _activity = a;
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(ShowDownloadButton));
        OnPropertyChanged(nameof(ShowHaveIt));
        OnPropertyChanged(nameof(ArrowBrush));
        OnPropertyChanged(nameof(ArrowEnabled));
        OnPropertyChanged(nameof(ShowInlineBar));
        OnPropertyChanged(nameof(InlinePercent));
        OnPropertyChanged(nameof(ShowPauseGlyph));
    }

    /// <summary>Paused files show the same gray double-bar Pause glyph as the Pause button, not a down
    /// arrow -- the arrow reads "click to fetch", which is wrong for something that's paused mid-fetch.</summary>
    public bool ShowPauseGlyph => _activity is Grog.Core.Sync.FileActivity.DownloadPaused
                                            or Grog.Core.Sync.FileActivity.MovePaused;

    partial void OnDownloadPercentChanged(double value)
    {
        OnPropertyChanged(nameof(ShowInlineBar));
        OnPropertyChanged(nameof(InlinePercent));
    }

    /// <summary>Show the inline progress bar whenever this file has real progress -- actively downloading OR
    /// holding a persisted partial (paused / queued / idle), so a half-downloaded paused file keeps its bar.</summary>
    public bool ShowInlineBar => IsDownloading || (_f.PartialBytes ?? 0) > 0;

    /// <summary>Inline bar value: the live percent while transferring, otherwise the persisted partial
    /// percent (accurate immediately, no wait for the engine).</summary>
    public double InlinePercent
    {
        get
        {
            if (IsDownloading && DownloadPercent > 0) return DownloadPercent;
            long partial = _f.PartialBytes ?? 0, total = _f.ExpectedSizeBytes ?? 0;
            if (total > 0 && partial > 0) return System.Math.Clamp(100.0 * partial / total, 0, 100);
            return IsDownloading ? DownloadPercent : 0;
        }
    }

    /// <summary>Derived: a live transfer is in flight for this file (drives the inline bar).</summary>
    public bool IsDownloading => _activity == Grog.Core.Sync.FileActivity.Downloading;
    /// <summary>Derived: queued or paused (arrow grayed, not actionable).</summary>
    public bool IsQueued => _activity is Grog.Core.Sync.FileActivity.QueuedToDownload
                                      or Grog.Core.Sync.FileActivity.DownloadPaused;

    public FileState State => _f.State;

    // --- Verify surfacing (zero disk reads -- state already in the manifest). The engine MD5-verifies each
    //     file at download time; the status dot + its tooltip carry the verify state, never a row badge.
    /// <summary>Full verify status for the status-dot tooltip, including when it was last verified.</summary>
    public string VerifyTooltip
    {
        get
        {
            var when = _f.LastVerifiedAt is { } t ? $" · {t.ToLocalTime():MMM d, yyyy}" : "";
            return _f.State switch
            {
                FileState.Verified => $"Verified against GOG's checksum{when}",
                FileState.Corrupt  => "Checksum mismatch - re-download to repair this file.",
                FileState.Present => string.IsNullOrEmpty(_f.ExpectedMd5)
                    ? "GOG published no checksum for this file - verified by size only."
                    : "Downloaded, not yet checksum-verified.",
                _ => "",
            };
        }
    }

    /// <summary>Resolves this file's device (RootId) to availability so the dot can tell Detached from Missing.
    /// Set by the VM; defaults online-fixed so a file with no known device still reads a genuine Missing.</summary>
    public static Grog.Core.Sync.ConditionRules.DeviceLookup? DeviceLookup { get; set; }

    /// <summary>The domain Condition axis for this file: the durable on-disk truth, with Missing-vs-Detached
    /// resolved against the device. The dot reads this, not the raw FileState.</summary>
    public Grog.Core.Sync.FileCondition Condition
        => Grog.Core.Sync.ConditionRules.Of(_f, DeviceLookup ?? (_ => Grog.Core.Sync.DeviceAvailability.OnlineFixed));

    /// <summary>Status dot, keyed off the Condition axis: green=Present/Verified, amber=Outdated or
    /// Missing, red=Corrupt, dim=Detached (safe on an unplugged drive), gray=NotPresent, amber=Partial.</summary>
    public IBrush StateBrush => Condition switch
    {
        Grog.Core.Sync.FileCondition.Present or Grog.Core.Sync.FileCondition.Verified => Palette.SuccessGreen,
        Grog.Core.Sync.FileCondition.Outdated => Palette.AccentAmber,
        Grog.Core.Sync.FileCondition.Missing  => Palette.AccentAmber,
        Grog.Core.Sync.FileCondition.Partial  => Palette.AccentAmber,
        Grog.Core.Sync.FileCondition.Corrupt  => Palette.ErrorRed,
        Grog.Core.Sync.FileCondition.Detached => Palette.InkDim,   // have it, just on an unplugged drive
        _                                     => Palette.InkMuted,   // NotPresent
    };

    // Download control left of the name: amber arrow = fetch, gray = queued, pause = paused, green check = have
    // it. The arrow shows ONLY when a queue action remains -- a queued file is still NotDownloaded by state.
    public bool ShowDownloadButton => !OutOfScope && !IsDownloading && !IsQueued && !ShowPauseGlyph
        && _f.State is FileState.NotBackedUp or FileState.Missing or FileState.Outdated;
    /// <summary>Downloaded, but the current scope leaves it out: listed under "On disk, outside scope" so it can
    /// still be deleted per file. Read-only otherwise (no download, no state marks).</summary>
    public bool OutOfScope { get; set; }
    public bool ShowStateActions => !OutOfScope;
    public bool ShowHaveIt => !IsDownloading && !IsQueued && !ShowPauseGlyph && Grog.Core.Sync.BackupScope.IsPresent(_f.State);   // shares the glyph column with the pause bars (a MovePaused present file drew both)

    public IBrush ArrowBrush => (IsQueued || IsDownloading)
        ? Palette.InkMuted   // queued / in-flight = gray
        : Palette.AccentAmber;  // actionable = amber
    public bool ArrowEnabled => !IsQueued && !IsDownloading;

    public void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Condition));
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(ShowDownloadButton));
        OnPropertyChanged(nameof(ShowHaveIt));
        OnPropertyChanged(nameof(ArrowBrush));
        OnPropertyChanged(nameof(ArrowEnabled));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(VerifyTooltip));
    }

}

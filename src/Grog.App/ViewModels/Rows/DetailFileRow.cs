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

    public string LanguageText => IsExtra || string.IsNullOrWhiteSpace(_f.Language) ? "" : TitleCase(_f.Language);

    public string RowTitle => IsExtra ? Name : (LanguageText.Length > 0 ? LanguageText : Name);

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

    private static string TitleCase(string s)
    {
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
            words[i] = words[i].Length == 0 ? words[i] : char.ToUpperInvariant(words[i][0]) + words[i][1..];
        return string.Join(' ', words);
    }
    public bool IsExtra => _f.Kind == FileKind.Extra;
    public double BadgeColumnMinWidth => IsPatch ? 128 : IsExtra ? 116 : 48;
    public string OsBadge => PlatformNames.Badge(_f.Os);
    public bool ShowOsBadge => OsBadge.Length > 0;
    // The OS reads in its own bright platform hue (a separate axis from the content-type colors).
    public IBrush OsBadgeBrush => ExtraTypeSegment.ColorFor(_f.Os ?? "");
    public double Percent => (_f.ExpectedSizeBytes is > 0 && _f.LocalSizeBytes is > 0)
        ? System.Math.Clamp(100.0 * _f.LocalSizeBytes.Value / _f.ExpectedSizeBytes.Value, 0, 100) : 0;

    private string ExtraBucket => Grog.Core.Sync.ExtraClassifier.Classify(_f.Name, _f.ExtraType);

    // The type chip: an extra's bucket, or "Patches" for a patch. A plain installer draws none (it is the default).
    private const string PatchesLabel = "Patches";
    public bool IsPatch => _f.Kind == FileKind.Patch;
    public string Descriptor => IsExtra ? Grog.Core.Sync.ExtraClassifier.ShortLabel(ExtraBucket) : IsPatch ? PatchesLabel : "";

    public IBrush ExtraTypeBrush => IsExtra ? ExtraTypeSegment.ColorFor(ExtraBucket)
                                  : IsPatch ? ExtraTypeSegment.ColorFor(PatchesLabel) : Brushes.Transparent;

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

    public bool ShowPauseGlyph => _activity is Grog.Core.Sync.FileActivity.DownloadPaused
                                            or Grog.Core.Sync.FileActivity.MovePaused;

    partial void OnDownloadPercentChanged(double value)
    {
        OnPropertyChanged(nameof(ShowInlineBar));
        OnPropertyChanged(nameof(InlinePercent));
    }

    public bool ShowInlineBar => IsDownloading || (_f.PartialBytes ?? 0) > 0;

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

    public bool IsDownloading => _activity == Grog.Core.Sync.FileActivity.Downloading;
    public bool IsQueued => _activity is Grog.Core.Sync.FileActivity.QueuedToDownload
                                      or Grog.Core.Sync.FileActivity.DownloadPaused;

    public FileState State => _f.State;

    // --- Verify surfacing (zero disk reads -- state already in the manifest). The engine MD5-verifies each
    //     file at download time; the status dot + its tooltip carry the verify state, never a row badge.
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

    public static Grog.Core.Sync.ConditionRules.DeviceLookup? DeviceLookup { get; set; }

    public Grog.Core.Sync.FileCondition Condition
        => Grog.Core.Sync.ConditionRules.Of(_f, DeviceLookup ?? (_ => Grog.Core.Sync.DeviceAvailability.OnlineFixed));

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

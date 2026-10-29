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

public sealed partial class QueueRow : ObservableObject, IDropTargetRow
{
    private bool _dropAbove, _dropBelow;
    public bool DropLineAbove { get => _dropAbove; set { if (_dropAbove == value) return; _dropAbove = value; OnPropertyChanged(nameof(DropLineAbove)); } }
    public bool DropLineBelow { get => _dropBelow; set { if (_dropBelow == value) return; _dropBelow = value; OnPropertyChanged(nameof(DropLineBelow)); } }

    public QueueRow(DownloadTask task, bool live) { Task = task; IsLiveTask = live; _nameHidden = IsHiddenGame(task.File.GameGogId); }
    public DownloadTask Task { get; private set; }
    public bool IsLiveTask { get; private set; }

    public void Rebind(DownloadTask task, bool live)
    {
        if (ReferenceEquals(Task, task) && IsLiveTask == live) return;
        Task = task; IsLiveTask = live;
        Refresh();
        OnPropertyChanged(nameof(SizeText)); OnPropertyChanged(nameof(RemainingBytes));
    }

    public static Func<long, bool> IsHiddenGame { get; set; } = _ => false;
    public static Func<string?, string> RootLabel { get; set; } = _ => "";
    public string TargetText { get { var l = RootLabel(Task.TargetRootId); return l.Length == 0 ? "" : "\u2192 " + l; } }
    public const string HiddenLabel = "Hidden item";
    private bool _nameHidden;
    public bool IsNameHidden => _nameHidden;

    public static Func<Grog.Core.Models.GameFile, bool> NeedsSignInFor { get; set; } = _ => false;
    public bool NeedsSignIn => !WontFit && NeedsSignInFor(Task.File);

    public string Name => IsNameHidden ? HiddenLabel : Task.ResolvedFileName ?? Task.File.Name;
    public string GameTitle => IsNameHidden ? HiddenLabel : Task.GameTitle;
    public string LabelText => IsNameHidden ? HiddenLabel : Task.File.Name;
    public string TypeLabel => Grog.Core.Sync.ExtraClassifier.TypeLabelOf(Task.File);
    public string FileNameText
    {
        get
        {
            if (IsNameHidden) return "";   // ShowFileLine is false on an empty name: no second line at all
            if (!string.IsNullOrWhiteSpace(Task.ResolvedFileName)) return Task.ResolvedFileName!;
            if (!string.IsNullOrWhiteSpace(Task.File.ResolvedFileName)) return Task.File.ResolvedFileName!;
            var rel = Task.File.LocalRelativePath;
            if (!string.IsNullOrWhiteSpace(rel))
            {
                var leaf = rel!.Replace('\\', '/');
                int i = leaf.LastIndexOf('/');
                if (i >= 0 && i < leaf.Length - 1) leaf = leaf[(i + 1)..];
                if (!string.IsNullOrWhiteSpace(leaf)) return leaf;
            }
            // Nothing resolved yet. GOG's label is often just the product name again; when it is, fall back to
            // the FileKey's last segment ("en1installer0") -- ugly but REAL, and it distinguishes twin labels.
            var label = Task.File.Name;
            if (!string.IsNullOrWhiteSpace(label) && !SameAsTitle(label)) return label;
            var key = Task.File.FileKey?.TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(key))
            {
                int k = key!.LastIndexOf('/');
                var leaf = k >= 0 && k < key.Length - 1 ? key[(k + 1)..] : key;
                if (!string.IsNullOrWhiteSpace(leaf)) return leaf;
            }
            return label;
        }
    }
    private bool SameAsTitle(string s) =>
        string.Equals(s.Trim(), GameTitle?.Trim(), StringComparison.OrdinalIgnoreCase);
    public bool ShowFileLine => !string.IsNullOrWhiteSpace(FileNameText) && !SameAsTitle(FileNameText);
    public IBrush TypeBrush => ExtraTypeSegment.ColorFor(TypeLabel);
    // The queue row uses Library's detail-pane vocabulary: a colored TYPE badge for extras, a gray OS
    // badge for installers -- never a tinted filename.
    public bool IsExtra => Task.File.Kind == FileKind.Extra;
    public string TypeBadgeText => ExtraTypeSegment.Label(TypeLabel);
    // Queue TYPE chip = EXACTLY the details-pane / filter-pill treatment (fill = base hue @0x2E, border =
    // base brush, text = base hue full opacity), so a type's queue chip, badge and pill are pixel-identical.
    private Color QueueTypeColor => (TypeBrush as ISolidColorBrush)?.Color ?? Colors.Gray;
    public IBrush ExtraTypeFill   => new SolidColorBrush(Color.FromArgb(46, QueueTypeColor.R, QueueTypeColor.G, QueueTypeColor.B));
    public IBrush ExtraTypeBorder => TypeBrush;
    public IBrush ExtraTypeText   => new SolidColorBrush(Color.FromArgb(255, QueueTypeColor.R, QueueTypeColor.G, QueueTypeColor.B));
    public string OsBadge => PlatformNames.Badge(Task.File.Os);
    public IBrush OsBadgeBrush => ExtraTypeSegment.ColorFor(Task.File.Os ?? "");
    public bool GameSpansPlatforms { get; set; }
    public bool ShowOsBadge => !IsExtra && OsBadge.Length > 0 && GameSpansPlatforms;
    internal long RemainingBytes => Math.Max(0, (Task.BytesTotal ?? Task.File.PlanSizeBytes ?? 0) - Task.BytesReceived);

    // The real size once known (the transfer's own total, else the sizer's CDN figure), GOG's rounded label before that.
    internal long? KnownSizeBytes => (Task.BytesTotal ?? Task.File.PlanSizeBytes) is { } s and > 0 ? s : null;
    public string SizeText => KnownSizeBytes is { } s ? ByteFormat.Size(s) : "Size unknown";
    private bool _wontFit;
    public bool WontFit
    {
        get => _wontFit;
        set
        {
            if (_wontFit == value) return;
            _wontFit = value;
            if (!value) _fileLimit = null;
            OnPropertyChanged(nameof(WontFit)); OnPropertyChanged(nameof(IsActive)); OnPropertyChanged(nameof(ShowWontFitShort)); OnPropertyChanged(nameof(ShowWontFitLong));
        }
    }
    private string? _fileLimit;
    public string? FileLimit { get => _fileLimit; set { if (_fileLimit == value) return; _fileLimit = value; OnPropertyChanged(nameof(FileLimit)); OnPropertyChanged(nameof(WontFitText)); OnPropertyChanged(nameof(ShowWontFitShort)); OnPropertyChanged(nameof(ShowWontFitLong)); OnPropertyChanged(nameof(WontFitTip)); } }
    public string WontFitText => _fileLimit is null ? "WON'T FIT" : $"TOO BIG FOR THIS DRIVE ({_fileLimit.ToUpperInvariant()})";
    public bool ShowWontFitShort => WontFit && _fileLimit is null;
    public bool ShowWontFitLong => WontFit && _fileLimit is not null;
    public string? WontFitTip => _fileLimit is null ? null : $"Too big for this drive ({_fileLimit}): one file here cannot be larger than 4 GB.";
    private bool _justMoved;
    public bool JustMoved { get => _justMoved; set { if (_justMoved == value) return; _justMoved = value; OnPropertyChanged(nameof(JustMoved)); } }
    public bool IsActive => !WontFit && Task.State == DownloadTaskState.Active;
    public double Percent => Task.BytesTotal is { } t && t > 0 ? Task.PercentComplete
        : Task.File.PlanSizeBytes is { } e && e > 0 ? Math.Min(100.0, 100.0 * Task.BytesReceived / e) : 0;
    public bool HasProgress => Task.PercentComplete > 0.01;
    public bool ShowBar => HasProgress || IsActive;
    public string ProgressText => $"{ByteFormat.Size(Task.BytesReceived)} / {SizeText} · {Percent:F0}%";
    public static bool QueuePaused { get; set; }
    public string PhaseText => QueuePaused ? "PAUSED" : Task.FinishingPhase is { } p ? p.ToUpperInvariant() : "DOWNLOADING";
    public string RateText
    {
        get
        {
            if (QueuePaused || Task.FinishingPhase is not null) return "";
            // Smoothed per file (FileEta), not remaining bytes over the half-second speed, which swung by minutes.
            if (Task.SecondsLeft is not { } secs) return "";
            var eta = TimeSpan.FromSeconds(secs);
            var etaText = eta.TotalHours >= 1 ? $"{(int)eta.TotalHours}h {eta.Minutes}m"
                        : eta.TotalMinutes >= 1 ? $"{(int)eta.TotalMinutes}m" : $"{eta.Seconds}s";
            // Time left only: the live rate is the Overview's big readout, and on the card it crowded the percent.
            return $"{etaText} left";
        }
    }

    public void Refresh()
    {
        _nameHidden = IsHiddenGame(Task.File.GameGogId);
        OnPropertyChanged(nameof(Percent)); OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(RateText)); OnPropertyChanged(nameof(IsActive)); OnPropertyChanged(nameof(PhaseText)); OnPropertyChanged(nameof(WontFit));
        OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(HasProgress)); OnPropertyChanged(nameof(ShowBar));
        OnPropertyChanged(nameof(NeedsSignIn)); OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(GameTitle)); OnPropertyChanged(nameof(LabelText)); OnPropertyChanged(nameof(FileNameText)); OnPropertyChanged(nameof(ShowFileLine));
    }
}

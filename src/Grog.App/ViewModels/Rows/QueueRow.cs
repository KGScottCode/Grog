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

/// <summary>One row of the download queue. Wraps the engine's task, because the queue's unit of work is a
/// task (with position, progress and its own cancellation), not a manifest entry.</summary>
public sealed partial class QueueRow : ObservableObject, IDropTargetRow
{
    private bool _dropAbove, _dropBelow;
    public bool DropLineAbove { get => _dropAbove; set { if (_dropAbove == value) return; _dropAbove = value; OnPropertyChanged(nameof(DropLineAbove)); } }
    public bool DropLineBelow { get => _dropBelow; set { if (_dropBelow == value) return; _dropBelow = value; OnPropertyChanged(nameof(DropLineBelow)); } }

    public QueueRow(DownloadTask task, bool live) { Task = task; IsLiveTask = live; _nameHidden = IsHiddenGame(task.File.GameGogId); }
    public DownloadTask Task { get; private set; }
    /// <summary>True when <see cref="Task"/> is the engine's own instance; false for a display task derived from the
    /// manifest. A display row is REUSED across rebuilds (its task refreshed in place): before 09-16 every rebuild
    /// with no live task built a new task and so a new row, and the whole tail was torn down and re-realized on
    /// each pass -- the "my row vanished" drop (owner-hit).</summary>
    public bool IsLiveTask { get; private set; }

    /// <summary>(09-19) The same file's row takes another task: the engine's instance when a run starts, a display
    /// task when it ends. The row used to be REPLACED at both edges, so Pause and Resume tore down and re-realized
    /// the whole tail (measured: RebuildQueue 586 ms on ~400 rows, a 780 ms UI stall on Pause).</summary>
    public void Rebind(DownloadTask task, bool live)
    {
        if (ReferenceEquals(Task, task) && IsLiveTask == live) return;
        Task = task; IsLiveTask = live;
        Refresh();
        OnPropertyChanged(nameof(SizeText)); OnPropertyChanged(nameof(RemainingBytes));
    }

    /// <summary>(09-19) The Library's hidden rule (mature toggle, per-game Hide), set once by the window's view
    /// model. A hidden game's row STAYS -- the queue's order, counts and bytes are facts -- but it is not NAMED:
    /// title and file name read <see cref="HiddenLabel"/>. Hiding conceals names, never bytes.</summary>
    public static Func<long, bool> IsHiddenGame { get; set; } = _ => false;
    /// <summary>Root id -> drive label, set by the shell; the active card names where the file is going.</summary>
    public static Func<string?, string> RootLabel { get; set; } = _ => "";
    /// <summary>"→ Small": where this file lands, bottom-right of the active card.</summary>
    public string TargetText { get { var l = RootLabel(Task.TargetRootId); return l.Length == 0 ? "" : "\u2192 " + l; } }
    public const string HiddenLabel = "Hidden item";
    /// <summary>Read once per <see cref="Refresh"/> (every rebuild pass and every hide/unhide), not per bound property.</summary>
    private bool _nameHidden;
    public bool IsNameHidden => _nameHidden;

    /// <summary>(09-19, owner) This file cannot download until an account that owns it is signed in: nobody is
    /// connected, or every owner stamped on it has been removed. The row keeps its place (the order is the
    /// user's; nothing clears a queue but the user) and says why it waits, in the WON'T FIT slot, in the warning
    /// orange the Library's "(account removed)" caption uses. Red stays "has nowhere to land".</summary>
    public static Func<Grog.Core.Models.GameFile, bool> NeedsSignInFor { get; set; } = _ => false;
    public bool NeedsSignIn => !WontFit && NeedsSignInFor(Task.File);

    public string Name => IsNameHidden ? HiddenLabel : Task.ResolvedFileName ?? Task.File.Name;
    public string GameTitle => IsNameHidden ? HiddenLabel : Task.GameTitle;
    /// <summary>GOG's label for the file ("Agony (Part 2 of 5)"): the tooltip. Line two is the real file name
    /// (<see cref="FileNameText"/>) on every row, active or waiting, so a file never changes its name the
    /// moment it starts, and the Moving pane reads the same way (owner 09-04).</summary>
    public string LabelText => IsNameHidden ? HiddenLabel : Task.File.Name;
    /// <summary>Extras show their classified bucket ("Soundtracks"), not the bare word "Extra".</summary>
    public string TypeLabel => Task.File.Kind == FileKind.Extra
        ? Grog.Core.Sync.ExtraClassifier.Classify(Task.File.Name, Task.File.ExtraType)
        : "Game";
    /// <summary>The real file name, WITH its extension. Best source first: the CDN name resolved for this
    /// task, the manifest-cached one, the on-disk name, and only then GOG's display label.</summary>
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
    /// <summary>Hide line two entirely when it would only echo line one.</summary>
    public bool ShowFileLine => !string.IsNullOrWhiteSpace(FileNameText) && !SameAsTitle(FileNameText);
    /// <summary>Color for the type badge: the Extra's bucket hue, matching Library's detail-pane badges.</summary>
    public IBrush TypeBrush => ExtraTypeSegment.ColorFor(TypeLabel);
    // The queue row uses Library's detail-pane vocabulary: a colored TYPE badge for extras, a gray OS
    // badge for installers -- never a tinted filename.
    public bool IsExtra => Task.File.Kind == FileKind.Extra;
    /// <summary>Short badge label, the same word the legend/chips/grid squares use for this type.</summary>
    public string TypeBadgeText => ExtraTypeSegment.Label(TypeLabel);
    // Queue TYPE chip = EXACTLY the details-pane / filter-pill treatment (fill = base hue @0x2E, border =
    // base brush, text = base hue full opacity), so a type's queue chip, badge and pill are pixel-identical.
    private Color QueueTypeColor => (TypeBrush as ISolidColorBrush)?.Color ?? Colors.Gray;
    public IBrush ExtraTypeFill   => new SolidColorBrush(Color.FromArgb(46, QueueTypeColor.R, QueueTypeColor.G, QueueTypeColor.B));
    public IBrush ExtraTypeBorder => TypeBrush;
    public IBrush ExtraTypeText   => new SolidColorBrush(Color.FromArgb(255, QueueTypeColor.R, QueueTypeColor.G, QueueTypeColor.B));
    public string OsBadge => PlatformNames.Badge(Task.File.Os);
    public IBrush OsBadgeBrush => ExtraTypeSegment.ColorFor(Task.File.Os ?? "");
    /// <summary>Set by the queue builder: true when this game has queued files for more than one platform.
    /// A badge on a single-platform game reports a fact you cannot act on, and 600 of them is a wall.</summary>
    public bool GameSpansPlatforms { get; set; }
    public bool ShowOsBadge => !IsExtra && OsBadge.Length > 0 && GameSpansPlatforms;
    /// <summary>Known byte count once we have one -- GOG's approximate size up front, the real
    /// Content-Length once the download resolves. Zero/absent means genuinely unknown, not empty.</summary>
    /// <summary>What this row still has to download: the ONE price every queue figure uses (owner 09-19: the dialog
    /// read 531.93 GB queued over 527.92 GB to back up, the gap being a partial counted on one line only). Same
    /// rule as PlacementPlanner.StillNeeds: the real size once known, less what is already on the drive.</summary>
    internal long RemainingBytes => Math.Max(0, (Task.BytesTotal ?? Task.File.PlanSizeBytes ?? 0) - Task.BytesReceived);

    internal long? KnownSizeBytes => (Task.BytesTotal ?? Task.File.ExpectedSizeBytes) is { } s and > 0 ? s : null;
    /// <summary>Never prints "0 B" for a real file whose size GOG didn't give us; says so instead.</summary>
    public string SizeText => KnownSizeBytes is { } s ? ByteFormat.Size(s) : "Size unknown";
    /// <summary>The planner found no device with room for this file. It keeps its position in the queue
    /// (order is the user's; we inform, never reorder) and says so in place of its size. The engine skips
    /// it and takes the next row that fits. Lifts on the next replan once space frees.</summary>
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
    /// <summary>(09-19) What the red row says: "WON'T FIT", or "TOO BIG FOR THIS DRIVE (FAT32)" when a per-file
    /// ceiling is the reason (<see cref="Grog.Core.Runs.QueueShaper.FileLimitBlocking"/>). Set by the queue rebuild.</summary>
    private string? _fileLimit;
    public string? FileLimit { get => _fileLimit; set { if (_fileLimit == value) return; _fileLimit = value; OnPropertyChanged(nameof(FileLimit)); OnPropertyChanged(nameof(WontFitText)); OnPropertyChanged(nameof(ShowWontFitShort)); OnPropertyChanged(nameof(ShowWontFitLong)); OnPropertyChanged(nameof(WontFitTip)); } }
    public string WontFitText => _fileLimit is null ? "WON'T FIT" : $"TOO BIG FOR THIS DRIVE ({_fileLimit.ToUpperInvariant()})";
    public bool ShowWontFitShort => WontFit && _fileLimit is null;
    public bool ShowWontFitLong => WontFit && _fileLimit is not null;
    public string? WontFitTip => _fileLimit is null ? null : $"Too big for this drive ({_fileLimit}): one file here cannot be larger than 4 GB.";
    /// <summary>(09-16) Lit for a moment after a drop or a menu move, so the row that moved is unmistakable where it
    /// landed. Set and cleared by the Overview view-model; the view paints it.</summary>
    private bool _justMoved;
    public bool JustMoved { get => _justMoved; set { if (_justMoved == value) return; _justMoved = value; OnPropertyChanged(nameof(JustMoved)); } }
    public bool IsActive => !WontFit && Task.State == DownloadTaskState.Active;
    /// <summary>The engine's percent once the total is known; before the CDN answers a resume request the
    /// expected size stands in, so a 476-of-486 MB partial never reads "0%" (owner-hit 09-04).</summary>
    public double Percent => Task.BytesTotal is { } t && t > 0 ? Task.PercentComplete
        : Task.File.ExpectedSizeBytes is { } e && e > 0 ? Math.Min(100.0, 100.0 * Task.BytesReceived / e) : 0;
    /// <summary>The row has partial progress worth a bar (e.g. a paused file that was mid-download) -- used
    /// so a waiting/paused row keeps its progress bar instead of dropping to a bare line.</summary>
    public bool HasProgress => Task.PercentComplete > 0.01;
    /// <summary>Bar for anything moving or partly fetched; an active file at 0% still shows its (empty) track.</summary>
    public bool ShowBar => HasProgress || IsActive;
    public string ProgressText => $"{ByteFormat.Size(Task.BytesReceived)} / {SizeText} · {Percent:F0}%";
    /// <summary>The in-flight eyebrow: DOWNLOADING while bytes arrive, then the finishing phase the engine
    /// reports (drive flush, hash) so a file sitting at 100% says what it is waiting on.</summary>
    /// <summary>The queue's Paused fact, mirrored by the host: while it is set every in-flight row says PAUSED,
    /// never one row PAUSED and its neighbours DOWNLOADING with a live rate (owner 09-08). Workers take a
    /// moment to stop, so the task state alone lags the button.</summary>
    public static bool QueuePaused { get; set; }
    public string PhaseText => QueuePaused ? "PAUSED" : Task.FinishingPhase is { } p ? p.ToUpperInvariant() : "DOWNLOADING";
    public string RateText
    {
        get
        {
            if (QueuePaused || Task.FinishingPhase is not null) return "";
            if (Task.BytesPerSecond <= 0) return "";
            long left = Math.Max(0, (Task.BytesTotal ?? 0) - Task.BytesReceived);
            var eta = left > 0 ? TimeSpan.FromSeconds(left / Task.BytesPerSecond) : TimeSpan.Zero;
            var etaText = eta.TotalHours >= 1 ? $"{(int)eta.TotalHours}h {eta.Minutes}m"
                        : eta.TotalMinutes >= 1 ? $"{(int)eta.TotalMinutes}m" : $"{eta.Seconds}s";
            // Time left only: the live rate is the Overview's big readout, and on the card it crowded the percent
            // ("60%6.09 MB/s", walk 10-01).
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

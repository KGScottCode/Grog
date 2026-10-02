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

/// <summary>One row in the live reorg checklist: a file being moved, and its state.</summary>
public partial class ReorgMoveRow : ObservableObject, IDropTargetRow
{
    private bool _dropAbove, _dropBelow;
    public bool DropLineAbove { get => _dropAbove; set { if (_dropAbove == value) return; _dropAbove = value; OnPropertyChanged(nameof(DropLineAbove)); } }
    public bool DropLineBelow { get => _dropBelow; set { if (_dropBelow == value) return; _dropBelow = value; OnPropertyChanged(nameof(DropLineBelow)); } }

    public string Title { get; init; } = "";
    public string PathText { get; init; } = "";
    /// <summary>The actual file name (line two), e.g. "setup_eye_of_the_beholder_2.0.0.3.exe".</summary>
    public string FileName { get; init; } = "";
    /// <summary>Color for the subtext: the game's accent for installers, the Extra's bucket hue for extras.</summary>
    public IBrush TypeBrush { get; init; } = Brushes.Transparent;
    // Identity back to the ReorgMove, so a drag-reorder can map this row to the plan entry.
    public long GogId { get; init; }
    public string FileKey { get; init; } = "";
    public string SizeText { get; init; } = "";
    public long SizeBytes { get; init; }
    [ObservableProperty] private string _stateText = "Waiting";
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private double _percent;   // for the active (in-flight) move card
    /// <summary>The same OS chip the download card shows; empty for extras.</summary>
    public string OsBadge { get; init; } = "";
    public bool ShowOsBadge => OsBadge.Length > 0;
    /// <summary>"Primary → Small": bottom-right of the active card.</summary>
    [ObservableProperty] private string _routeText = "";
    /// <summary>"1.3 GB / 2.38 GB · 55%": the download card's readout, on the move card too.</summary>
    [ObservableProperty] private string _progressText = "";
}

/// <summary>A problem folder in the import results screen (ambiguous or unmatched), resolvable via Find-match.</summary>
public partial class ImportResultRow : ObservableObject
{
    public string Path { get; init; } = "";
    public string FolderName => System.IO.Path.GetFileName(Path.TrimEnd('/', '\\'));
    public string Kind { get; init; } = "";       // "Ambiguous" / "Unmatched"
    public string Detail { get; init; } = "";     // candidate titles (ambiguous) or a short reason
    [ObservableProperty] private string _resolvedTitle = "";
    public bool IsResolved => !string.IsNullOrEmpty(ResolvedTitle);
    public bool ShowFindMatch => !IsResolved;
    partial void OnResolvedTitleChanged(string value)
    {
        OnPropertyChanged(nameof(IsResolved));
        OnPropertyChanged(nameof(ShowFindMatch));
    }
}

/// <summary>A weekday choice for the scheduler's Weekly dropdown.</summary>
public sealed record WeekdayOption(System.DayOfWeek Day, string Label);

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

public partial class ReorgMoveRow : ObservableObject, IDropTargetRow
{
    private bool _dropAbove, _dropBelow;
    public bool DropLineAbove { get => _dropAbove; set { if (_dropAbove == value) return; _dropAbove = value; OnPropertyChanged(nameof(DropLineAbove)); } }
    public bool DropLineBelow { get => _dropBelow; set { if (_dropBelow == value) return; _dropBelow = value; OnPropertyChanged(nameof(DropLineBelow)); } }

    public string Title { get; init; } = "";
    public string PathText { get; init; } = "";
    public string FileName { get; init; } = "";
    public IBrush TypeBrush { get; init; } = Brushes.Transparent;
    // Identity back to the ReorgMove, so a drag-reorder can map this row to the plan entry.
    public long GogId { get; init; }
    public string FileKey { get; init; } = "";
    public string SizeText { get; init; } = "";
    public long SizeBytes { get; init; }
    [ObservableProperty] private string _stateText = "Waiting";
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private double _percent;   // for the active (in-flight) move card
    public string OsBadge { get; init; } = "";
    public bool ShowOsBadge => OsBadge.Length > 0;
    [ObservableProperty] private string _routeText = "";
    [ObservableProperty] private string _progressText = "";
}

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

public sealed record WeekdayOption(System.DayOfWeek Day, string Label);

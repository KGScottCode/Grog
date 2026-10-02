// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Grog.Core.Format;

namespace Grog.App.ViewModels;

/// <summary>The one red row in the Downloading pane that stands for every queued file that won't fit
/// (owner 09-16). The rows that WILL download are drawn first, in queue order; this row follows them and
/// folds/unfolds the won't-fit rows beneath it, also in queue order. VIEW-ONLY grouping: the persisted
/// queue order is untouched, and a sort or a re-plan regroups on the next rebuild. Exists only while at
/// least one queued row is flagged; there is one instance per pane so the bound tail sees it as stable.</summary>
public sealed partial class WontFitDividerRow : ObservableObject, IDropTargetRow
{
    private bool _dropAbove, _dropBelow;
    public bool DropLineAbove { get => _dropAbove; set { if (_dropAbove == value) return; _dropAbove = value; OnPropertyChanged(nameof(DropLineAbove)); } }
    public bool DropLineBelow { get => _dropBelow; set { if (_dropBelow == value) return; _dropBelow = value; OnPropertyChanged(nameof(DropLineBelow)); } }

    [ObservableProperty] private int _count;
    [ObservableProperty] private long _bytes;
    [ObservableProperty] private bool _expanded = true;   // open by default (owner 09-19): the red rows have no other home
    /// <summary>Free space left per online device after every fitting row has its room (the plan's leftover).
    /// <see cref="RoomLeft"/> is the LARGEST single device's, because a file fits on one drive or not at all;
    /// the sum across drives gated moves that could never land (review 09-16).</summary>
    public IReadOnlyList<Grog.Core.Volumes.DeviceSpace> RoomByRoot { get; private set; } = System.Array.Empty<Grog.Core.Volumes.DeviceSpace>();
    public long RoomLeft { get; private set; }
    public void SetRoom(IReadOnlyList<Grog.Core.Volumes.DeviceSpace> byRoot)
    {
        RoomByRoot = byRoot;
        long max = 0; foreach (var d in byRoot) if (d.IsOnline && d.FreeBytes > max) max = d.FreeBytes;
        if (max == RoomLeft) return;
        RoomLeft = max; OnPropertyChanged(nameof(RoomLeft)); OnPropertyChanged(nameof(Text));
    }

    public string Text => $"{Count} won't fit as queued · {ByteFormat.Size(Bytes)} · {ByteFormat.Size(RoomLeft)} still free";
    public string Caret => Expanded ? "▾" : "▸";
    public string Tip => Expanded ? "Hide the files that won't fit" : "Show the files that won't fit";

    /// <summary>(09-19) Names its scope so it cannot be read as "Clear queue": only these rows go.</summary>
    public string RemoveLabel => $"Remove these {Count}";
    partial void OnCountChanged(int value) { OnPropertyChanged(nameof(Text)); OnPropertyChanged(nameof(RemoveLabel)); }
    partial void OnBytesChanged(long value) => OnPropertyChanged(nameof(Text));
    partial void OnExpandedChanged(bool value) { OnPropertyChanged(nameof(Caret)); OnPropertyChanged(nameof(Tip)); }
}

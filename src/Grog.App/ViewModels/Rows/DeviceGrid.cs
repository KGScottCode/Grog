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

public sealed partial class InventoryRow : ObservableObject
{
    public InventoryRow(LibraryItem item, GameFile file) { Item = item; File = file; }
    public LibraryItem Item { get; }
    public GameFile File { get; }

    public InventoryGroupRow? Owner { get; set; }

    [ObservableProperty] private bool _isSelected;

    public long GogId => Item.GogId;
    public string FileName
    {
        get
        {
            // Prefer the real on-disk name; GOG's label is often just the game title again.
            var path = File.LocalRelativePath;
            if (!string.IsNullOrEmpty(path))
            {
                var leaf = System.IO.Path.GetFileName(path.Replace('\\', '/'));
                if (!string.IsNullOrEmpty(leaf)) return leaf;
            }
            return string.IsNullOrWhiteSpace(File.Name) ? File.Kind.ToString() : File.Name;
        }
    }
    public string GameTitle => Item.Title;
    public long SizeBytes => Grog.Core.Sync.Rollups.HeldBytesOf(File);
    public string SizeText => ByteFormat.Size(SizeBytes);

    public string TypeLabel => File.Kind == FileKind.Extra
        ? Grog.Core.Sync.ExtraClassifier.Classify(File.Name, File.ExtraType) : "Game";
    // Type chip: EXACTLY the shared treatment of the details file-row, download pill and queue row (fill = base
    // hue @0x2E, border = base brush, text = full opacity). EXTRAS ONLY -- color marks the game/extra distinction.
    public bool IsExtra => File.Kind == FileKind.Extra;
    public string TypeBadgeText => ExtraTypeSegment.Label(TypeLabel);
    private Color InvTypeColor => (ExtraTypeSegment.ColorFor(TypeLabel) as ISolidColorBrush)?.Color ?? Colors.Gray;
    public IBrush ExtraTypeFill   => new SolidColorBrush(Color.FromArgb(46, InvTypeColor.R, InvTypeColor.G, InvTypeColor.B));
    public IBrush ExtraTypeBorder => ExtraTypeSegment.ColorFor(TypeLabel);
    public IBrush ExtraTypeText   => new SolidColorBrush(Color.FromArgb(255, InvTypeColor.R, InvTypeColor.G, InvTypeColor.B));

    public bool IsMoving { get; set; }

    public static Grog.Core.Sync.ConditionRules.DeviceLookup? DeviceLookup { get; set; }
    public Grog.Core.Sync.FileCondition Condition
        => Grog.Core.Sync.ConditionRules.Of(File, DeviceLookup ?? (_ => Grog.Core.Sync.DeviceAvailability.OnlineFixed));

    // ONE vocabulary, shared with the Library row: see ConditionVocabulary.
    public string StatusText => IsMoving ? "Moving" : ConditionVocabulary.Word(Condition);
    public IBrush StatusBrush => ConditionVocabulary.Brush(StatusText);
}

public enum GridSortKey { Size, Name }

public sealed partial class DeviceGridViewModel : ObservableObject
{
    public string RootId { get; set; } = "";
    [ObservableProperty] private string _label = "";

    public Services.BulkObservableCollection<object> GroupedRows { get; } = new();
    public Services.BulkObservableCollection<InventoryRow> FileRows { get; } = new();
    public ObservableCollection<InventoryRow> Selected { get; } = new();
    public object? SelAnchor { get; set; }

    // ---- Sort (parallels the download queue's sortable header). The game GROUPS are re-sequenced; each
    // game's files stay grouped under it. Sticky per device: the chosen key/direction survives a rebuild.
    private readonly List<InventoryGroupRow> _groups = new();
    // Size/Files default descending (biggest/most first); Name defaults A->Z. Shared scaffold: SortState.
    private readonly SortState<GridSortKey> _sort = new(GridSortKey.Size, k => k == GridSortKey.Name, asc: false);
    public GridSortKey SortKey => _sort.Key;

    public void SetGroups(IEnumerable<InventoryGroupRow> groups)
    {
        _groups.Clear();
        _groups.AddRange(groups);
        _allFiles.Clear();
        foreach (var g in _groups) _allFiles.AddRange(g.Files);
        RebuildTypeOptions();
        LayoutGroupedRows();
        // The options list is built HERE, so raise visibility/label now -- otherwise the IsVisible binding
        // keeps the stale false it read before the first inventory arrived.
        RaiseTypeFilter();
    }

    // ---- Per-grid TYPE FILTER: a SELECTION tool -- a move must never carry files the user cannot see. So a
    // filter change CLEARS the selection, and FileRows holds only the VISIBLE rows (_allFiles keeps the full set).
    private readonly List<InventoryRow> _allFiles = new();
    private readonly HashSet<string> _hiddenTypes = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<FilterChip> TypeOptions { get; } = new();

    private void RebuildTypeOptions()
    {
        var present = _groups.SelectMany(g => g.Files).Select(f => f.TypeLabel)
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Drop hidden entries whose type no longer exists here, or the label would count a type that is gone.
        _hiddenTypes.RemoveWhere(t => !present.Contains(t, StringComparer.OrdinalIgnoreCase));
        TypeOptions.Clear();
        foreach (var t in present.OrderBy(TypeOrder).ThenBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            var chip = new FilterChip
            {
                Label = ExtraTypeSegment.Label(t),
                Value = t,
                Swatch = ExtraTypeSegment.ColorFor(t),
            };
            chip.SetActiveQuiet(!_hiddenTypes.Contains(t));
            TypeOptions.Add(chip);
        }
        // Wire AFTER the quiet seed so seeding cannot fire a filter change.
        foreach (var c in TypeOptions) c.Changed2 = OnTypeToggled;
    }

    private static readonly string[] TypeSequence =
        { "Game", "Mod", "Soundtracks", "Manuals", "Videos", "Art", "Localizations", "Alt Versions", "Movie", "Old versions", "Other" };
    private static int TypeOrder(string t)
    {
        int i = Array.FindIndex(TypeSequence, s => string.Equals(s, t, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? TypeSequence.Length : i;
    }

    private void OnTypeToggled(FilterChip chip)
    {
        if (chip.IsActive) _hiddenTypes.Remove(chip.Value); else _hiddenTypes.Add(chip.Value);
        ApplyTypeFilter();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ShowAllTypes()
    {
        if (_hiddenTypes.Count == 0) return;
        _hiddenTypes.Clear();
        foreach (var c in TypeOptions) c.SetActiveQuiet(true);
        ApplyTypeFilter();
    }

    private void ApplyTypeFilter()
    {
        foreach (var r in _allFiles) r.IsSelected = false;
        Selected.Clear();
        SelAnchor = null;
        LayoutGroupedRows();
        RaiseTypeFilter();
        Raise();
    }

    private bool TypeVisible(InventoryRow r) => !_hiddenTypes.Contains(r.TypeLabel);
    public bool TypeFilterActive => _hiddenTypes.Count > 0;
    public string TypeFilterLabel =>
        _hiddenTypes.Count == 0 ? "All types" : $"{TypeOptions.Count - _hiddenTypes.Count} of {TypeOptions.Count} types";
    private void RaiseTypeFilter()
    {
        OnPropertyChanged(nameof(TypeFilterActive)); OnPropertyChanged(nameof(TypeFilterLabel));
        OnPropertyChanged(nameof(HasTypeChoices));
    }
    public bool HasTypeChoices => TypeOptions.Count > 1;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void Sort(string? key)
    {
        var k = key switch { "Name" => GridSortKey.Name, _ => GridSortKey.Size };
        _sort.Toggle(k);
        LayoutGroupedRows();
        RaiseSortGlyphs();
    }

    private void LayoutGroupedRows()
    {
        IEnumerable<InventoryGroupRow> ordered = SortKey switch
        {
            GridSortKey.Name  => _sort.Asc ? _groups.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
                                          : _groups.OrderByDescending(g => g.Title, StringComparer.OrdinalIgnoreCase),
            _                 => _sort.Asc ? _groups.OrderBy(g => g.SizeBytes).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
                                          : _groups.OrderByDescending(g => g.SizeBytes).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase),
        };
        // ONE notification for the whole list: per-row Add re-laid the grid thousands of times.
        var grouped = new List<object>();
        var files = new List<InventoryRow>();
        foreach (var g in ordered)
        {
            // A game whose every file is filtered out drops entirely: an empty game header would claim the
            // drive holds something for it that this view can act on, and it cannot.
            var visible = g.Files.Where(TypeVisible).ToList();
            if (visible.Count == 0) continue;
            grouped.Add(g);
            foreach (var f in visible) { grouped.Add(f); files.Add(f); }
        }
        GroupedRows.ReplaceAll(grouped);
        FileRows.ReplaceAll(files);
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand] private void ExpandAll() => SetAllExpanded(true);
    [CommunityToolkit.Mvvm.Input.RelayCommand] private void CollapseAll() => SetAllExpanded(false);

    public bool AllExpanded => _groups.Count > 0 && _groups.All(g => g.IsExpanded);
    public string ExpandAllCaret => AllExpanded ? "\u2303" : "\u2304";
    public string ExpandAllTip => AllExpanded ? "Collapse every game" : "Expand every game";
    public string ExpandAllLabel => AllExpanded ? "Collapse all" : "Expand all";
    [CommunityToolkit.Mvvm.Input.RelayCommand] private void ToggleExpandAll() => SetAllExpanded(!AllExpanded);
    private void SetAllExpanded(bool v)
    {
        foreach (var g in _groups) g.IsExpanded = v;
        RaiseExpandAll();
    }
    public void RaiseExpandAll()
    {
        OnPropertyChanged(nameof(AllExpanded)); OnPropertyChanged(nameof(ExpandAllCaret));
        OnPropertyChanged(nameof(ExpandAllTip)); OnPropertyChanged(nameof(ExpandAllLabel));
    }

    public string SortLabel => SortKey switch { GridSortKey.Name => "Name", _ => "Size" } + (_sort.Asc ? " ▲" : " ▼");   // the direction glyph doubles as the menu caret
    public string SortMenuName  => "Name" + SortGlyphName;
    public string SortMenuSize  => "Size" + SortGlyphSize;
    public string SortGlyphName  => _sort.Glyph(GridSortKey.Name);
    public string SortGlyphSize  => _sort.Glyph(GridSortKey.Size);
    private void RaiseSortGlyphs()
    {
        OnPropertyChanged(nameof(SortGlyphName)); OnPropertyChanged(nameof(SortGlyphSize));
        OnPropertyChanged(nameof(SortLabel));
        OnPropertyChanged(nameof(SortMenuName)); OnPropertyChanged(nameof(SortMenuSize));
    }

    public bool HasFiles => FileRows.Count > 0;
    public bool HasSelection => Selected.Count > 0;
    public int FileCount => FileRows.Count;
    public int SelectedCount => Selected.Count;
    public bool IsEmpty => FileRows.Count == 0;

    private long TotalBytes => FileRows.Sum(r => r.SizeBytes);
    private long SelectedBytes => Selected.Sum(r => r.SizeBytes);

    public string HeaderText
    {
        get
        {
            if (Selected.Count > 0)
            {
                int games = Selected.Select(r => r.Owner).Where(o => o is not null).Distinct().Count();
                return $"{games} game{(games == 1 ? "" : "s")} selected · {Selected.Count} file{(Selected.Count == 1 ? "" : "s")} · {ByteFormat.Size(SelectedBytes)}";
            }
            // Counts the VISIBLE games, not every group: with a type filter on, the summary must describe the
            // rows on screen -- it sits directly above them and beside the buttons that act on them.
            int all = GroupedRows.OfType<InventoryGroupRow>().Count();
            return $"{all} game{(all == 1 ? "" : "s")} · {FileRows.Count} file{(FileRows.Count == 1 ? "" : "s")} · {ByteFormat.Size(TotalBytes)}";
        }
    }
    public bool HeaderIsSelection => Selected.Count > 0;
    public IBrush HeaderBrush => Selected.Count > 0 ? Palette.AccentAmber : Palette.InkPrimary;

    public void Clear()
    {
        GroupedRows.Clear(); FileRows.Clear(); Selected.Clear(); _groups.Clear(); _allFiles.Clear();
        // The hidden set SURVIVES a clear: BuildGridRows clears before every rebuild (one per landed file during a
        // run), and wiping it here reset "Show: Game only" to "All types" two seconds after the user picked it
        // . RebuildTypeOptions prunes a hidden type the device no longer holds, so a drive that lost
        // all its files still comes back unfiltered.
        TypeOptions.Clear();
        SelAnchor = null; Raise(); RaiseTypeFilter();
    }

    public void Raise()
    {
        OnPropertyChanged(nameof(HasFiles)); OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(HeaderIsSelection)); OnPropertyChanged(nameof(HeaderBrush));
    }
}

public sealed partial class InventoryGroupRow : ObservableObject
{
    public InventoryGroupRow(string title, long gogId, IReadOnlyList<InventoryRow> files, int haveCount, int ofCount,
                             IReadOnlyList<InventoryElsewhereRow>? elsewhere = null)
    {
        Title = title; GogId = gogId; Files = files; PresentCount = haveCount; TotalCount = ofCount;
        Elsewhere = elsewhere ?? Array.Empty<InventoryElsewhereRow>();
        foreach (var f in files) f.Owner = this;
        foreach (var e in Elsewhere) e.Owner = this;
    }

    public string Title { get; }
    public long GogId { get; }
    public IReadOnlyList<InventoryRow> Files { get; }
    public int PresentCount { get; }
    public int TotalCount { get; }
    public IReadOnlyList<InventoryElsewhereRow> Elsewhere { get; }

    public string DeviceName { get; set; } = "this device";

    // ---- consolidate: bring the game's files from the other device HERE.
    public bool HasElsewhere => Elsewhere.Count > 0;
    public bool ShowPlacementBlock => !IsComplete || HasElsewhere;
    public long ElsewhereBytes => Elsewhere.Sum(e => Grog.Core.Sync.Rollups.HeldBytesOf(e.File));
    public string? ConsolidateBlockedReason { get; set; }
    public bool ConsolidateCanRun => ConsolidateBlockedReason is null;
    public string ConsolidateLabel => $"Bring the rest of this game here ({Elsewhere.Count} file{(Elsewhere.Count == 1 ? "" : "s")}, {ByteFormat.Size(ElsewhereBytes)})";
    public string ConsolidateTip => ConsolidateBlockedReason ?? "Moves the files from the other storage onto this one.";
    public System.Windows.Input.ICommand? ConsolidateCommand { get; set; }

    public DeviceGridViewModel? Grid { get; set; }

    public System.Windows.Input.ICommand? QueueRemainingFilteredCommand { get; set; }
    public System.Windows.Input.ICommand? QueueRemainingAllCommand { get; set; }
    public System.Windows.Input.ICommand? MoveRemainingTopCommand { get; set; }
    public System.Windows.Input.ICommand? VerifyGameCommand { get; set; }
    public System.Windows.Input.ICommand? OpenGameFolderCommand { get; set; }
    public System.Windows.Input.ICommand? CopyGamePathsCommand { get; set; }
    public System.Windows.Input.ICommand? RedownloadGameCommand { get; set; }
    public System.Windows.Input.ICommand? DeleteGameCommand { get; set; }
    public string DeleteAllLabel => Files.Count == 1
        ? "Delete downloaded file"
        : $"Delete downloaded files ({Files.Count}, {Grog.Core.Format.ByteFormat.Size(SizeBytes)})";
    public string RedownloadAllLabel => $"Re-download everything ({Grog.Core.Format.ByteFormat.Size(SizeBytes)})";
    public string CopyPathsLabel => Files.Count == 1 ? "Copy full path" : $"Copy full paths ({Files.Count})";

    public bool CanVerify => PresentCount > 0;

    // COLLAPSED by default: a fresh Folders inventory reads as a tidy game
    // list, and the "Expand all" button's label agrees with what is on screen.
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public long SizeBytes => Files.Sum(f => f.SizeBytes);
    public string SizeText => ByteFormat.Size(SizeBytes);

    public bool IsComplete => PresentCount >= TotalCount;
    public string CompletenessText => IsComplete ? "● Complete" : "◐ Partial";
    public string CountText
    {
        get
        {
            var text = $"{PresentCount} / {TotalCount}";
            if (Elsewhere.Count == 0) return text;
            // Spelled out, in parentheses: the title column takes the squeeze (it trims with an ellipsis).
            return text + " (" + string.Join(", ", Elsewhere.GroupBy(e => e.DeviceLabel).Select(g => $"{g.Count()} on {g.Key}")) + ")";
        }
    }
    public IBrush CompletenessBrush => IsComplete ? Palette.SuccessGreen : Palette.AccentAmber;
    public IBrush CompletenessBg
    {
        get
        {
            var c = (CompletenessBrush as ISolidColorBrush)?.Color ?? Colors.Gray;
            return new SolidColorBrush(Color.FromArgb(0x24, c.R, c.G, c.B));
        }
    }
    public IBrush RowTint
    {
        get
        {
            var c = (CompletenessBrush as ISolidColorBrush)?.Color ?? Colors.Gray;
            return new SolidColorBrush(Color.FromArgb(0x24, c.R, c.G, c.B));   // 36/255 = 14%
        }
    }
    public string Caret => IsExpanded ? "▾" : "▸";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Caret));

    public void ReflectSelection() => IsSelected = Files.Count > 0 && Files.All(f => f.IsSelected);
}

public sealed class InventoryElsewhereRow
{
    public InventoryElsewhereRow(GameFile file, string deviceLabel) { File = file; DeviceLabel = deviceLabel; }
    public GameFile File { get; }
    public string DeviceLabel { get; }
    public InventoryGroupRow? Owner { get; set; }
    public string FileName
    {
        get
        {
            var path = File.LocalRelativePath;
            if (!string.IsNullOrEmpty(path))
            {
                var leaf = System.IO.Path.GetFileName(path.Replace('\\', '/'));
                if (!string.IsNullOrEmpty(leaf)) return leaf;
            }
            return string.IsNullOrWhiteSpace(File.Name) ? File.Kind.ToString() : File.Name;
        }
    }
    public string SizeText => ByteFormat.Size(Grog.Core.Sync.Rollups.HeldBytesOf(File));
}

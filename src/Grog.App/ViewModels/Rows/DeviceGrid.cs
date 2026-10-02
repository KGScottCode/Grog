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

/// <summary>One game's footprint on a single device. A game with games on one drive and extras on the
/// other appears on both -- that's what routing does, so the inventory says so.</summary>
public sealed partial class InventoryRow : ObservableObject
{
    public InventoryRow(LibraryItem item, GameFile file) { Item = item; File = file; }
    public LibraryItem Item { get; }
    public GameFile File { get; }

    /// <summary>The game rollup this file sits under in the grouped grid. Lets a file row bind its
    /// visibility to the group's expand state and lets selection reflect back onto the group.</summary>
    public InventoryGroupRow? Owner { get; set; }

    /// <summary>Per-row selection for the grouped grid (replaces DataGrid.SelectedItems, which isn't
    /// bindable). The VM keeps SelectedInventory in sync from these flags.</summary>
    [ObservableProperty] private bool _isSelected;

    public long GogId => Item.GogId;
    /// <summary>The FILE is the subject here: "which 4 GB .bin is eating my drive" is the cleanup question.
    /// The game is context, so it sits underneath in smaller text.</summary>
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

    /// <summary>Set while this file is part of a running move (reorg). It's a transient activity, not a
    /// manifest FileState, so it's flagged here rather than switched on File.State.</summary>
    public bool IsMoving { get; set; }

    /// <summary>Resolves this file's device to availability so the inventory can distinguish "Disconnected"
    /// (safe, on an unplugged removable drive) from "Missing". Set by the VM from the live manifest.</summary>
    public static Grog.Core.Sync.ConditionRules.DeviceLookup? DeviceLookup { get; set; }
    public Grog.Core.Sync.FileCondition Condition
        => Grog.Core.Sync.ConditionRules.Of(File, DeviceLookup ?? (_ => Grog.Core.Sync.DeviceAvailability.OnlineFixed));

    // ONE vocabulary, shared with the Library row: see ConditionVocabulary.
    public string StatusText => IsMoving ? "Moving" : ConditionVocabulary.Word(Condition);
    public IBrush StatusBrush => ConditionVocabulary.Brush(StatusText);
}

public enum GridSortKey { Size, Name }

/// <summary>One device's side of the two-grid Backups view: its grouped rows (game headers + files),
/// the flat file list, and the current selection. Two of these back the Primary and Secondary grids.</summary>
public sealed partial class DeviceGridViewModel : ObservableObject
{
    public string RootId { get; set; } = "";
    [ObservableProperty] private string _label = "";

    /// <summary>Flat render list: a game header row followed by its file rows. File rows bind visibility to
    /// their group's IsExpanded, so expand/collapse never rebuilds the list.</summary>
    public Services.BulkObservableCollection<object> GroupedRows { get; } = new();
    public Services.BulkObservableCollection<InventoryRow> FileRows { get; } = new();
    public ObservableCollection<InventoryRow> Selected { get; } = new();
    /// <summary>The last plainly-clicked row (group or file) -- the anchor a Shift+click ranges from.</summary>
    public object? SelAnchor { get; set; }

    // ---- Sort (parallels the download queue's sortable header). The game GROUPS are re-sequenced; each
    // game's files stay grouped under it. Sticky per device: the chosen key/direction survives a rebuild.
    private readonly List<InventoryGroupRow> _groups = new();
    /// <summary>Current sort key; defaults to Size, biggest game first.</summary>
    // Size/Files default descending (biggest/most first); Name defaults A->Z. Shared scaffold: SortState.
    private readonly SortState<GridSortKey> _sort = new(GridSortKey.Size, k => k == GridSortKey.Name, asc: false);
    public GridSortKey SortKey => _sort.Key;

    /// <summary>Adopt a fresh set of game groups (from a rebuild) and lay out GroupedRows per the current
    /// sort. Keeps the user's chosen sort across rebuilds.</summary>
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

    /// <summary>Options are the types actually PRESENT on this device, in the canonical chip order -- offering
    /// a type the drive does not hold would be a control that does nothing.</summary>
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

    /// <summary>Canonical chip order, so the flyout lists types in the SAME sequence as the Library's filter
    /// bar. Anything unlisted sorts last, alphabetically.</summary>
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

    /// <summary>Re-lay the grid after a filter change and DROP THE SELECTION. Clearing is not a convenience:
    /// a selected row that the filter has just hidden would still be moved by the Selected button.</summary>
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
    /// <summary>"All types" at rest; the COUNT SHOWN when narrowed, never the count hidden -- the label states
    /// what you are looking at.</summary>
    public string TypeFilterLabel =>
        _hiddenTypes.Count == 0 ? "All types" : $"{TypeOptions.Count - _hiddenTypes.Count} of {TypeOptions.Count} types";
    private void RaiseTypeFilter()
    {
        OnPropertyChanged(nameof(TypeFilterActive)); OnPropertyChanged(nameof(TypeFilterLabel));
        OnPropertyChanged(nameof(HasTypeChoices));
    }
    /// <summary>One type on the device means the control can only ever be a no-op, so it does not render.</summary>
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
        // ONE notification for the whole list (stress run 09-06): per-row Add re-laid the grid thousands of times.
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

    /// <summary>ONE stateful toggle for one binary. Partly-expanded counts as NOT all-expanded, so the first
    /// press always finishes the job rather than undoing it.</summary>
    public bool AllExpanded => _groups.Count > 0 && _groups.All(g => g.IsExpanded);
    public string ExpandAllCaret => AllExpanded ? "\u2303" : "\u2304";
    public string ExpandAllTip => AllExpanded ? "Collapse every game" : "Expand every game";
    /// <summary>A glyph that changes meaning with state must say which state it offers -- the verb rides
    /// inside the control, same rule as Sort/Show.</summary>
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

    /// <summary>The pill's own label: the active key and its direction ("Size ▼"), never a hardcoded word.</summary>
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

    /// <summary>Header summary: the current selection when there is one, otherwise the whole device's
    /// footprint. Always names GAMES and FILES separately, plus the size.</summary>
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
    /// <summary>Amber while a selection is active (matching the enabled move buttons), white otherwise.</summary>
    public IBrush HeaderBrush => Selected.Count > 0 ? Palette.AccentAmber : Palette.InkPrimary;

    public void Clear()
    {
        GroupedRows.Clear(); FileRows.Clear(); Selected.Clear(); _groups.Clear(); _allFiles.Clear();
        // The hidden set SURVIVES a clear: BuildGridRows clears before every rebuild (one per landed file during a
        // run), and wiping it here reset "Show: Game only" to "All types" two seconds after the user picked it
        // (QA 09-18). RebuildTypeOptions prunes a hidden type the device no longer holds, so a drive that lost
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

/// <summary>A game rollup in the grouped inventory grid: one game's files on the viewed device, under an
/// expandable header showing completeness. Selection is derived from the child file rows.</summary>
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
    /// <summary>(09-14) The game's files that sit on the OTHER device: listed dimmed under the game so "4 of 7"
    /// over two rows is not a mystery. Never selectable, never counted in this grid's header or moves.</summary>
    public IReadOnlyList<InventoryElsewhereRow> Elsewhere { get; }

    /// <summary>"Primary" / "Secondary" (the role, as the cards say it), not the folder's raw label.</summary>
    public string DeviceName { get; set; } = "this device";

    // ---- consolidate (owner 09-14): bring the game's files from the other device HERE.
    public bool HasElsewhere => Elsewhere.Count > 0;
    /// <summary>The queue/placement menu block shows for a partial game OR one with files elsewhere.</summary>
    public bool ShowPlacementBlock => !IsComplete || HasElsewhere;
    public long ElsewhereBytes => Elsewhere.Sum(e => Grog.Core.Sync.Rollups.HeldBytesOf(e.File));
    /// <summary>Set at build time from the device's free space: null = fits, else why it cannot run.</summary>
    public string? ConsolidateBlockedReason { get; set; }
    public bool ConsolidateCanRun => ConsolidateBlockedReason is null;
    public string ConsolidateLabel => $"Bring the rest of this game here ({Elsewhere.Count} file{(Elsewhere.Count == 1 ? "" : "s")}, {ByteFormat.Size(ElsewhereBytes)})";
    public string ConsolidateTip => ConsolidateBlockedReason ?? "Moves the files from the other storage onto this one.";
    public System.Windows.Input.ICommand? ConsolidateCommand { get; set; }

    /// <summary>Which device grid (Primary/Secondary) this rollup belongs to -- lets selection and moves
    /// know the source side without threading it through every click handler.</summary>
    public DeviceGridViewModel? Grid { get; set; }

    /// <summary>Right-click queue commands, held on the row so a ContextMenu (a separate popup root, where
    /// $parent[Window] doesn't resolve) can bind to them via the row's own DataContext.</summary>
    public System.Windows.Input.ICommand? QueueRemainingFilteredCommand { get; set; }
    public System.Windows.Input.ICommand? QueueRemainingAllCommand { get; set; }
    public System.Windows.Input.ICommand? MoveRemainingTopCommand { get; set; }
    /// <summary>Per-game "Rescan &amp; Verify Files" (device-grid right-click). Held on the row for the same
    /// popup-root binding reason as the queue commands above.</summary>
    public System.Windows.Input.ICommand? VerifyGameCommand { get; set; }
    /// <summary>The file-scope actions, at game scope: the two menus are ONE menu at two sizes.</summary>
    public System.Windows.Input.ICommand? OpenGameFolderCommand { get; set; }
    public System.Windows.Input.ICommand? CopyGamePathsCommand { get; set; }
    public System.Windows.Input.ICommand? RedownloadGameCommand { get; set; }
    /// <summary>Delete this game's downloaded files on THIS device (owner, 09-13). Red, last, sized like Re-download.</summary>
    public System.Windows.Input.ICommand? DeleteGameCommand { get; set; }
    public string DeleteAllLabel => Files.Count == 1
        ? "Delete downloaded file"
        : $"Delete downloaded files ({Files.Count}, {Grog.Core.Format.ByteFormat.Size(SizeBytes)})";
    /// <summary>The size lives IN the label because this is the one entry that can cost tens of gigabytes.</summary>
    public string RedownloadAllLabel => $"Re-download everything ({Grog.Core.Format.ByteFormat.Size(SizeBytes)})";
    public string CopyPathsLabel => Files.Count == 1 ? "Copy full path" : $"Copy full paths ({Files.Count})";

    /// <summary>Only offer "Rescan &amp; Verify" when at least one of the game's files is actually on this
    /// device -- there's nothing to re-check for a game with zero backed-up files.</summary>
    public bool CanVerify => PresentCount > 0;

    // COLLAPSED by default (owner call, 2026-08-29): a fresh Folders inventory reads as a tidy game
    // list, and the "Expand all" button's label agrees with what is on screen.
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public long SizeBytes => Files.Sum(f => f.SizeBytes);
    public string SizeText => ByteFormat.Size(SizeBytes);

    public bool IsComplete => PresentCount >= TotalCount;
    public string CompletenessText => IsComplete ? "● Complete" : "◐ Partial";
    /// <summary>Always show the count, complete or not ("15 / 15" reads as "all here"). A split game adds where the rest
    /// is ("2 / 2 (1 on Secondary)"; owner 10-01): the dimmed elsewhere rows it replaces read as
    /// files that were here. "Here" is implied; "of" is the whole game across both storages.</summary>
    public string CountText
    {
        get
        {
            var text = $"{PresentCount} / {TotalCount}";
            if (Elsewhere.Count == 0) return text;
            // Spelled out, in parentheses (owner 10-01): the title column takes the squeeze (it trims with an ellipsis).
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
    /// <summary>A wash of the completeness color behind the GAME row only ("partial" is a group property),
    /// at 0x24 -- the same alpha as the completeness chip, bright enough that game rows outweigh file rows.</summary>
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

    /// <summary>Re-derive the group's selected state from its files ("all selected" == group selected).</summary>
    public void ReflectSelection() => IsSelected = Files.Count > 0 && Files.All(f => f.IsSelected);
}

/// <summary>(09-14) A file of a game shown under its rollup on the device it is NOT on: name, which device holds
/// it, size. Read-only context; it carries no selection, no menu, and no bytes for this grid.</summary>
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

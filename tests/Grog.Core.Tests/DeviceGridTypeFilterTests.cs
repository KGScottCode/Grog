// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.App.ViewModels;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;

/// <summary>
/// The Folders inventory grid's per-grid TYPE FILTER. This grid drives the bulk-move buttons, so the filter
/// is a SELECTION tool, not a browsing one: the guarantee under test is that nothing hidden can ever be
/// moved. Two ways that could break -- a stale selection surviving a filter change, and "Move All" reading
/// the unfiltered set -- and both are covered here.
/// </summary>
[NewBatch]
[Trait("foldersfilter")]
public class DeviceGridTypeFilterTests
{
    private static GameFile File(FileKind kind, string name, long size, string? extraType = null)
        => new()
        {
            Kind = kind, Name = name, State = FileState.Present,
            ExpectedSizeBytes = size, LocalSizeBytes = size,
            ExtraType = extraType, FileKey = name,
        };

    private static InventoryGroupRow Group(string title, long id, params GameFile[] files)
    {
        var item = new LibraryItem { GogId = id, Title = title, Slug = $"g{id}" };
        item.Files.AddRange(files);
        var rows = files.Select(f => new InventoryRow(item, f)).ToList();
        return new InventoryGroupRow(title, id, rows, rows.Count, rows.Count);
    }

    /// <summary>A game with one installer and one soundtrack, so every test has at least two types to filter.</summary>
    private static DeviceGridViewModel GridWithMixedTypes()
    {
        var g = Group("Baldur's Gate", 1,
            File(FileKind.Installer, "setup.exe", 4_000),
            File(FileKind.Extra, "soundtrack.zip", 600, "Soundtrack"));
        var grid = new DeviceGridViewModel();
        grid.SetGroups(new[] { g });
        return grid;
    }

    private static void Hide(DeviceGridViewModel grid, string label)
        => grid.TypeOptions.First(o => o.Label.Equals(label, StringComparison.OrdinalIgnoreCase)).IsActive = false;

    // ---- Options ----------------------------------------------------------------------------

    [Test] void OptionsAreTheTypesActuallyPresent()
    {
        var grid = GridWithMixedTypes();
        // A drive holding an installer and a soundtrack offers exactly those two -- never the full chip set,
        // because a control that filters nothing is a control that does nothing.
        Assert.Equal(2, grid.TypeOptions.Count);
        Assert.True(grid.HasTypeChoices);
    }

    [Test] void OneTypeMeansNoControl()
    {
        var grid = new DeviceGridViewModel();
        grid.SetGroups(new[] { Group("Solo", 2, File(FileKind.Installer, "setup.exe", 10)) });
        // Only installers here: the filter could only ever hide everything or nothing, so it does not render.
        Assert.False(grid.HasTypeChoices);
    }

    [Test] void LabelStatesWhatIsShownNotWhatIsHidden()
    {
        var grid = GridWithMixedTypes();
        Assert.Equal("All types", grid.TypeFilterLabel);
        Hide(grid, "Soundtracks");
        Assert.Equal("1 of 2 types", grid.TypeFilterLabel);
        Assert.True(grid.TypeFilterActive);
    }

    // ---- The invariant ----------------------------------------------------------------------

    [Test] void ChangingTheFilterClearsTheSelection()
    {
        var grid = GridWithMixedTypes();
        foreach (var r in grid.FileRows) { r.IsSelected = true; grid.Selected.Add(r); }
        Assert.Equal(2, grid.Selected.Count);

        Hide(grid, "Soundtracks");

        // THE POINT: not "the hidden row is dropped from the selection" but "the selection is gone entirely".
        // A partial clear would still leave the user pressing Move on a set they can no longer fully see.
        Assert.Equal(0, grid.Selected.Count);
        Assert.False(grid.HasSelection);
        Assert.True(grid.FileRows.All(r => !r.IsSelected));
    }

    [Test] void HiddenFilesLeaveFileRowsSoMoveAllCannotCarryThem()
    {
        var grid = GridWithMixedTypes();
        Assert.Equal(2, grid.FileRows.Count);

        Hide(grid, "Soundtracks");

        // MoveAllTo* moves FileRows verbatim, so this collection IS the guarantee: a hidden file is not in it.
        Assert.Equal(1, grid.FileRows.Count);
        Assert.Equal("setup.exe", grid.FileRows[0].FileName);
    }

    [Test] void AGameWithEveryFileHiddenDropsOut()
    {
        var grid = new DeviceGridViewModel();
        grid.SetGroups(new[]
        {
            Group("Installer only", 3, File(FileKind.Installer, "setup.exe", 10)),
            Group("Soundtrack only", 4, File(FileKind.Extra, "ost.zip", 20, "Soundtrack")),
        });
        Hide(grid, "Soundtracks");

        // An empty game header would claim the drive holds something for that game this view can act on.
        var titles = grid.GroupedRows.OfType<InventoryGroupRow>().Select(g => g.Title).ToList();
        Assert.Equal(1, titles.Count);
        Assert.Equal("Installer only", titles[0]);
    }

    [Test] void HeaderSummaryDescribesTheVisibleRows()
    {
        var grid = GridWithMixedTypes();
        Assert.Contains("2 files", grid.HeaderText);
        Hide(grid, "Soundtracks");
        // The summary sits directly above the rows and beside the buttons that act on them, so it counts what
        // is on screen -- a total that included hidden bytes would contradict both.
        Assert.Contains("1 file", grid.HeaderText);
    }

    [Test] void ShowAllRestoresEveryType()
    {
        var grid = GridWithMixedTypes();
        Hide(grid, "Soundtracks");
        grid.ShowAllTypesCommand.Execute(null);
        Assert.False(grid.TypeFilterActive);
        Assert.Equal(2, grid.FileRows.Count);
        Assert.True(grid.TypeOptions.All(o => o.IsActive));
    }

    [Test] void TheFilterSurvivesTheRebuildEveryLandedFileTriggers()
    {
        // BuildGridRows clears the grid before every rebuild (one per landed file during a run). The user's
        // "Show: Game only" must come back on the new rows, or it resets to "All types" two seconds after
        // being picked and "Move all" carries what was filtered out (QA 09-18).
        var grid = GridWithMixedTypes();
        Hide(grid, "Soundtracks");
        grid.Clear();
        grid.SetGroups(new[]
        {
            Group("Baldur's Gate", 1,
                File(FileKind.Installer, "setup.exe", 4_000),
                File(FileKind.Extra, "soundtrack.zip", 600, "Soundtrack")),
        });
        Assert.True(grid.TypeFilterActive, "the hidden type is still hidden after a rebuild");
        Assert.Equal(1, grid.FileRows.Count);
        Assert.False(grid.TypeOptions.First(o => o.Label == "Soundtracks").IsActive);

        // A drive that lost that type entirely comes back unfiltered: nothing is hidden against old content.
        grid.Clear();
        grid.SetGroups(new[] { Group("Baldur's Gate", 1, File(FileKind.Installer, "setup.exe", 4_000)) });
        Assert.False(grid.TypeFilterActive);
    }

    [Test] void SeedingOptionsDoesNotItselfClearASelection()
    {
        // RebuildTypeOptions seeds each chip's state; if that seed fired the change handler, every inventory
        // rebuild would silently drop the user's selection.
        var grid = GridWithMixedTypes();
        foreach (var r in grid.FileRows) { r.IsSelected = true; grid.Selected.Add(r); }
        grid.SetGroups(new[]
        {
            Group("Baldur's Gate", 1,
                File(FileKind.Installer, "setup.exe", 4_000),
                File(FileKind.Extra, "soundtrack.zip", 600, "Soundtrack")),
        });
        Assert.False(grid.TypeFilterActive);
    }
}

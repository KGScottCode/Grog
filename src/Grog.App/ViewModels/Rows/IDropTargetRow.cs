// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.App.ViewModels;

/// <summary>A row that can paint the drag-reorder insertion line on itself (09-16). The line used to be an
/// adorner positioned by list-space coordinates and it did not show once the list was scrolled (owner-measured,
/// repeatable); a flag on the target row is drawn by the row's own template, so it needs no coordinates and
/// survives virtualization.</summary>
public interface IDropTargetRow
{
    bool DropLineAbove { get; set; }
    bool DropLineBelow { get; set; }
}

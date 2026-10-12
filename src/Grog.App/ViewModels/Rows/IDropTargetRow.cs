// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.App.ViewModels;

public interface IDropTargetRow
{
    bool DropLineAbove { get; set; }
    bool DropLineBelow { get; set; }
}

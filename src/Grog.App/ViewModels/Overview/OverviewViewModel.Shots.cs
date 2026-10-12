// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.App.ViewModels;

public sealed partial class OverviewViewModel
{
    private string? _railLibraryOverride, _railSubOverride;

    internal void ForceRailWorstCase()
    {
        _railLibraryOverride = "999/999 Games";
        _railSubOverride = "downloading 999 files\nmoving 999 files";   // the two-line case
        RaiseRailBadge(); RaiseRailOverviewSub();
    }
}

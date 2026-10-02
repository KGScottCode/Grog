// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.App.ViewModels;

/// <summary>Render-harness hooks (tools/HeadlessShots) only. Nothing in the app sets these: null = the live text.</summary>
public sealed partial class OverviewViewModel
{
    private string? _railLibraryOverride, _railSubOverride;

    /// <summary>Force the widest rail readouts so the rail's minimum width is judged against worst-case content.</summary>
    internal void ForceRailWorstCase()
    {
        _railLibraryOverride = "999/999 Games";
        _railSubOverride = "downloading 999 files\nmoving 999 files";   // the two-line case
        RaiseRailBadge(); RaiseRailOverviewSub();
    }
}

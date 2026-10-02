// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.App.ViewModels;

/// <summary>Render-harness hooks (tools/HeadlessShots) only; nothing in the app calls them.</summary>
public sealed partial class StorageViewModel
{
    /// <summary>Assistant-only: stand up a Moving pane (one copy in flight, a waiting tail) without a real
    /// move, for headless rendering of the pane's sort pills and rows (GROG_SHOT_MOVE).</summary>
    internal void SeedMoveForShots(Grog.Core.Volumes.ReorgJob job, double activeFraction)
    {
        _activeReorgJob = job;
        IsReorgRunning = true; ReorgFinished = false; ReorgPaused = false; ReorgPausing = false;
        ReorgHeaderText = "Moving to Secondary";
        _reorgFileFraction = activeFraction;
        SyncReorgRows(job);
        ActivityTabSelected = true;
    }
}

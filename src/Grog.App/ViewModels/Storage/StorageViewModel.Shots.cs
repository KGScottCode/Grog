// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.App.ViewModels;

public sealed partial class StorageViewModel
{
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

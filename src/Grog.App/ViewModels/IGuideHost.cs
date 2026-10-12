// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;

namespace Grog.App.ViewModels;

internal interface IGuideHost
{
    bool IsConnected { get; }
    int TotalCount { get; }
    bool HasBackedUpAnything { get; }
    bool IsFileBackedUp(long gogId, string fileKey);
    IReadOnlySet<(long GogId, string FileKey)> QueuedFiles();
    string CurrentViewName { get; }
    bool IsScanning { get; }
    bool RunActive { get; }
    bool ShowingItemCount { get; }
    bool HasCloudSaves { get; }

    IEnumerable<GameRowViewModel> Games { get; }
    Grog.Core.Sync.Scope EffectiveScope { get; }
    GameRowViewModel? SelectedRow { get; set; }
    AppSettings Settings { get; }

    // Actions
    void Navigate(string view);
    void ClearGridSelection();
    void ScaffoldChosenFolder();
    void Log(string message);
    void ReopenWelcome();
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;

namespace Grog.App.ViewModels;

/// <summary>
/// What the first-run guide needs from the window: the facts its steps wait on, the picker inputs, and
/// four actions. MainWindowViewModel implements it explicitly (MainWindowViewModel.GuideHost.cs); a test
/// can implement it with a fake, which a direct host reference would never allow (the host constructor
/// loads settings and starts the app).
/// </summary>
internal interface IGuideHost
{
    // Facts -> GuideState
    bool IsConnected { get; }
    int TotalCount { get; }
    bool HasBackedUpAnything { get; }
    /// <summary>Whether one file (by identity) is on disk as GOG currently ships it.</summary>
    bool IsFileBackedUp(long gogId, string fileKey);
    /// <summary>The persisted download queue as identities, one snapshot (a queued row shows no arrow).</summary>
    IReadOnlySet<(long GogId, string FileKey)> QueuedFiles();
    string CurrentViewName { get; }
    bool IsScanning { get; }
    bool RunActive { get; }
    bool ShowingItemCount { get; }
    bool HasCloudSaves { get; }

    // Picker inputs and the grid selection the back-up step drives
    IEnumerable<GameRowViewModel> Games { get; }
    Grog.Core.Sync.Scope EffectiveScope { get; }
    GameRowViewModel? SelectedRow { get; set; }
    AppSettings Settings { get; }

    // Actions
    void Navigate(string view);
    void ClearGridSelection();
    void ScaffoldChosenFolder();
    void Log(string message);
    /// <summary>Back walked off the front of the tour: show the Welcome card again.</summary>
    void ReopenWelcome();
}

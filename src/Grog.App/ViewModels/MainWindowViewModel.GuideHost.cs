// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;

namespace Grog.App.ViewModels;

// The window's side of the first-run guide: IGuideHost, explicitly implemented so the guide's view of the
// window stays the narrow one the interface names, and the Changed handler that re-raises what reads it.
public partial class MainWindowViewModel : IGuideHost
{
    /// <summary>The first-run guide (spotlight, captions, tour cursor). Bound by the overlay in
    /// MainWindow.axaml through <c>DataContext="{Binding Guide}"</c>.</summary>
    public GuideViewModel Guide { get; }

    bool IGuideHost.IsConnected => IsConnected;
    int IGuideHost.TotalCount => Library.TotalCount;
    bool IGuideHost.HasBackedUpAnything
        => _manifest?.Current.Items.Any(i => i.Files.Any(Grog.Core.Sync.BackupScope.IsPresent)) ?? false;
    bool IGuideHost.IsFileBackedUp(long gogId, string fileKey)
        => Library.Resolve(gogId, fileKey).File is { } f && Grog.Core.Sync.BackupScope.IsPresent(f);
    IReadOnlySet<(long GogId, string FileKey)> IGuideHost.QueuedFiles()
        => _manifest is null ? new HashSet<(long, string)>()
         : _manifest.Current.Downloads.Snapshot().Select(q => (q.GogId, q.FileKey)).ToHashSet();
    string IGuideHost.CurrentViewName => CurrentView.ToString();
    bool IGuideHost.IsScanning => Library.CatalogBusy;
    bool IGuideHost.RunActive => Overview.RunActive;
    bool IGuideHost.ShowingItemCount => Library.ShowCompositionModal;
    bool IGuideHost.HasCloudSaves => CloudVm.HasCloudGames;
    IEnumerable<GameRowViewModel> IGuideHost.Games => Library.Games;
    Grog.Core.Sync.Scope IGuideHost.EffectiveScope => Library.EffectiveScope;
    GameRowViewModel? IGuideHost.SelectedRow { get => Library.SelectedRow; set => Library.SelectedRow = value; }
    AppSettings IGuideHost.Settings => _settings;
    void IGuideHost.Navigate(string view) => Navigate(view);
    // (09-19) The primary's label reads the GRID's selection (SelectedRows), not SelectedRow: clearing only the
    // latter left "Back up - 1 item" on the tour's Overview stop after the one-file demo.
    void IGuideHost.ClearGridSelection() { Library.SelectedRow = null; Library.ClearSelection(); Library.SelectedRows.Clear(); RaiseBarState(); }
    void IGuideHost.ScaffoldChosenFolder() => ScaffoldChosenFolder();
    void IGuideHost.Log(string message) => Log(message);
    void IGuideHost.ReopenWelcome() => ShowWelcomeOverlay = true;

    /// <summary>The guide's step or active flag moved: everything of the window's that reads the guide
    /// re-raises here. The primary button's label and target depend on the step (the whole-library card
    /// overrides the selection); the scan panel swaps between the page and the overlay on the active flag;
    /// the rail's status decorations stay hidden until the tour's last stop introduces them.</summary>
    private void OnGuideChanged()
    {
        Library.RaiseGuideDerived(); OnPropertyChanged(nameof(BarPrimaryLabel));
        OnPropertyChanged(nameof(ShowRailStatus)); OnPropertyChanged(nameof(ShowHealthBadgeInRail));
        OnPropertyChanged(nameof(ShowRailCounts));
    }
}

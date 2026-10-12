// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;

namespace Grog.App.ViewModels;

public partial class MainWindowViewModel : IGuideHost
{
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
    void IGuideHost.ClearGridSelection() { Library.SelectedRow = null; Library.ClearSelection(); Library.SelectedRows.Clear(); RaiseBarState(); }
    void IGuideHost.ScaffoldChosenFolder() => ScaffoldChosenFolder();
    void IGuideHost.Log(string message) => Log(message);
    void IGuideHost.ReopenWelcome() => ShowWelcomeOverlay = true;

    private void OnGuideChanged()
    {
        Library.RaiseGuideDerived(); OnPropertyChanged(nameof(BarPrimaryLabel));
        OnPropertyChanged(nameof(ShowRailStatus)); OnPropertyChanged(nameof(ShowHealthBadgeInRail));
        OnPropertyChanged(nameof(ShowRailCounts));
    }
}

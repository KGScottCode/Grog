// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Grog.App.ViewModels;

// Shell startup (S3.1): the launch sequence, first-run welcome, connection flag, and the rail-navigation raises.
public partial class MainWindowViewModel
{
    private void RaiseSharedNav()
    {
        OnPropertyChanged(nameof(ShowDashboard));
        OnPropertyChanged(nameof(ShowCloudContent));
        OnPropertyChanged(nameof(ShowAccountsContent));
        Accounts.RaiseAddSlot();
        OnPropertyChanged(nameof(ShowPageHeader));
    }

    private void RaiseNav()
    {
        OnPropertyChanged(nameof(ShowSettings));
        OnPropertyChanged(nameof(ShowDrivesView));
        OnPropertyChanged(nameof(ShowCloudView));
        OnPropertyChanged(nameof(ShowAccountsView));
        RaiseSharedNav();
        OnPropertyChanged(nameof(IsLibraryNav));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(ShowCompositionLine));
        OnPropertyChanged(nameof(NavLibraryOn));
        OnPropertyChanged(nameof(NavUpdatesOn));
        OnPropertyChanged(nameof(NavMissingOn));
        OnPropertyChanged(nameof(NavDrivesOn));
        OnPropertyChanged(nameof(NavCloudOn));
        OnPropertyChanged(nameof(NavOverviewOn));
        OnPropertyChanged(nameof(NavAccountsOn));
        OnPropertyChanged(nameof(NavSettingsOn));
        OnPropertyChanged(nameof(NavAboutOn));
        Library.RaiseDetailWidth();

    }

    public Func<Avalonia.Controls.TopLevel?>? OwnerWindowResolver { get; set; }

    // --- connection state (drives S0/S1 and the state-aware primary button) ---
    [ObservableProperty] private bool _isConnected;

    [ObservableProperty] private bool _initializing = true;

    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task StartAsync()
    {
        try
        {
            try { await EnsureServicesAsync(); }
            catch (Exception ex) when (ex is Grog.Core.Manifest.ManifestTooNewException or Grog.Core.Manifest.ManifestLockedException)
            {
                Log(ex.Message, isError: true);
                Storage.ShowLocationError(ex.Message);
                Initializing = false;
                return;
            }
            catch { /* services retry when the wizard starts; don't skip the welcome */ }

            if (Grog.Core.Storage.GrogPaths.PortableFallbackReason is { } fallback)
                Log($"GrogData is read-only; using the profile folder at {_configDir} ({fallback}).");

            string? remembered = null;
            try { remembered = Grog.Core.Storage.GrogPaths.ResolvePath(_settings.ChosenRoot ?? ""); }   // the live settings, not a second Load()
            catch { remembered = null; }

            bool rootBound = false;
            bool hasRemembered = !string.IsNullOrWhiteSpace(remembered);
            if (_servicesReady && hasRemembered)
            {
                try { await BindToRootAsync(remembered!); rootBound = true; }
                catch (Exception ex) { Log($"Backup storage {remembered} is not reachable right now: {ex.Message}", isError: true); }
            }

            bool firstRun = !rootBound && !hasRemembered && _servicesReady;
            if (firstRun)
            {
                try
                {
                    var def = Grog.Core.Storage.BackupLocationSuggester.DefaultRoot();
                    // Remember that GROG made this one, and only if it did not already exist. That flag is what
                    // later licenses tidying it away; a folder the user had already created is never ours.
                    bool weMadeIt = !Directory.Exists(def);
                    Directory.CreateDirectory(def);
                    await BindToRootAsync(def);
                    _settings.ChosenRoot = Grog.Core.Storage.GrogPaths.StorePath(_backupRoot);
                    _settings.DefaultRootAutoCreated = weMadeIt;
                    _settings.SaveSoon();
                }
                catch { /* if the default can't be created/bound, the welcome overlay still guides them */ }
            }
            if (!_settings.WelcomeShown && !_settings.FirstRunGuideDone) ShowWelcomeOverlay = true;
            else if (!_settings.FirstRunGuideDone) Guide.Resume();
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => Initializing = false);
            _initialized.TrySetResult();
        }
    }

    [ObservableProperty] private bool _showWelcomeOverlay;

    [RelayCommand]
    private void SkipToApp()
    {
        Guide.WelcomeReopenedFromGuide = false;   // skipping from a reopened welcome is still a deliberate exit
        ShowWelcomeOverlay = false;
        MarkWelcomeShown();
        _settings.FirstRunGuideDone = true;
        _settings.SaveSoon();
        var manifestSk = _manifest; var rootSk = _backupRoot;   // skipping the tour still accepts the default folder
        if (manifestSk is not null && !string.IsNullOrWhiteSpace(rootSk))
            _ = Task.Run(() => { try { new Grog.Core.Volumes.BackupLayout(manifestSk.Current, rootSk).ScaffoldCategoryFolders(); } catch { } });
        RaiseDashboard();
    }

    [RelayCommand]
    private void WelcomeGetStarted()
    {
        ShowWelcomeOverlay = false;
        MarkWelcomeShown();
        if (Guide.WelcomeReopenedFromGuide)
        {
            Guide.WelcomeReopenedFromGuide = false;
            Guide.Resume();
            return;
        }
        Guide.StartGuideCommand.Execute(null);
    }

    private void MarkWelcomeShown()
    {
        _settings.WelcomeShown = true;
        _settings.SaveSoon();
    }
}

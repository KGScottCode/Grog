// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Verify;
using Grog.Core.Format;

namespace Grog.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public Runtime.GrogSession Session { get; }

    public StorageViewModel Storage { get; }

    public LibraryViewModel Library { get; }

    public OverviewViewModel Overview { get; }

    public AccountsViewModel Accounts { get; }

    public SettingsViewModel Settings { get; }

    // ---- Forwarders onto the session (S1.1: same names as the fields they replaced, so no call site moved).
    private string _backupRoot { get => Session.BackupRoot; set => Session.BackupRoot = value; }
    internal string BackupRootPath => _backupRoot;
    private string _configDir { get => Session.ConfigDir; set => Session.ConfigDir = value; }
    private Grog.Core.Storage.GrogPaths? _configPaths { get => Session.ConfigPaths; set => Session.ConfigPaths = value; }
    private JsonManifestStore _manifest { get => Session.Manifest; set => Session.Manifest = value; }
    private HttpClient _http => Session.Http;
    private HttpClient _apiHttp => Session.ApiHttp;
    private GogApiClient _api { get => Session.Api; set => Session.Api = value; }
    private GogAuthService _auth { get => Session.Auth; set => Session.Auth = value; }
    private FileTokenStore _tokenStore { get => Session.TokenStore; set => Session.TokenStore = value; }
    private string _interactiveTokenPath { get => Session.InteractiveTokenPath; set => Session.InteractiveTokenPath = value; }
    private bool _servicesReady { get => Session.ServicesReady; set => Session.ServicesReady = value; }
    private DownloadEngine? _liveEngine { get => Session.LiveEngine; set => Session.LiveEngine = value; }
    private bool _stopRequested { get => Session.StopRequested; set => Session.StopRequested = value; }
    private CancellationTokenSource? _cts { get => Session.Cts; set => Session.Cts = value; }
    private CancellationTokenSource? _downloadCts { get => Session.DownloadCts; set => Session.DownloadCts = value; }
    private AppSettings _settings => Session.Settings;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _progressVisible;

    // ---- DEV ONLY (DEBUG builds): reset to first-run without re-login (see DevResetFirstRun). ----
    public bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    [RelayCommand]
    private async Task DevResetFirstRun()
    {
#if !DEBUG
        await Task.CompletedTask;
        return;
#else
        var paths = Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot);   // config dir only; no root probe on the dispatcher
        _settings.ChosenRoot = null;
        // Clear BOTH first-run flags, or the welcome and the guide stay suppressed by earlier runs.
        _settings.WelcomeShown = false;
        _settings.FirstRunGuideDone = false;
        _settings.SaveSoon();
        try
        {
            var mf = paths.ManifestPath;   // %APPDATA%\Grog\grog-manifest.json -- token file is a sibling, left as-is
            if (System.IO.File.Exists(mf)) System.IO.File.Move(mf, mf + ".pre-reset.bak", overwrite: true);
        }
        catch { /* best-effort; a fresh scan rebuilds the library anyway */ }

        // Empty the in-memory library FIRST: BindToRootAsync's SaveAsync would re-persist it right back to disk.
        _manifest.Mutate(m =>   // 
        {
            m.Items.Clear();
            m.Roots.Clear();
            m.PrimaryRootId = null;
            m.Downloads.Clear();
            m.Downloads.Paused = false;
        });

        Library.SelectedRow = null;
        Library.SelectedRows.Clear();

        _backupRoot = "";
        RaiseRootPathChanged();
        try
        {
            var def = Grog.Core.Storage.BackupLocationSuggester.DefaultRoot();
            System.IO.Directory.CreateDirectory(def);
            await BindToRootAsync(def);       // loads the now-empty manifest; RefreshAccounts keeps us connected
            _settings.ChosenRoot = Grog.Core.Storage.GrogPaths.StorePath(_backupRoot); _settings.SaveSoon();
        }
        catch { /* if the default can't be applied, the welcome overlay still guides */ }

        ShowWelcomeOverlay = true;
        RaiseState();
#endif
    }

    public bool HasLocation => !string.IsNullOrEmpty(_backupRoot);

    internal void RaiseRootPathChanged()
    {
        OnPropertyChanged(nameof(HasLocation));
        OnPropertyChanged(nameof(CurrentRootText));
        Storage.RaiseLocationState();
    }

    public CenterView Center
    {
        get
        {
            if (Phase == SetupPhase.NoLocation) return CenterView.LocationPrompt;
            return CurrentView switch
            {
                View.Drives     => CenterView.BackupLocations,
                View.Overview     => CenterView.Health,
                View.Settings   => CenterView.Settings,
                View.About      => CenterView.About,
                View.CloudSaves => CenterView.CloudSaves,
                View.Accounts   => CenterView.Accounts,
                _               => CenterView.Library,
            };
        }
    }

    // Per-panel gates -- each reads the single Center value, so two can never show at once.
    public bool ShowLocationPrompt  => Center == CenterView.LocationPrompt;

    [ObservableProperty] private bool _showAccountGate;
    [ObservableProperty] private string _accountGateWhat = "";

    internal bool RequireAccount(string what)
    {
        if (IsConnected) return true;
        AccountGateWhat = what;
        ShowAccountGate = true;
        return false;
    }

    [RelayCommand] private void DismissAccountGate() => ShowAccountGate = false;

    [RelayCommand]
    private void AccountGateGo()
    {
        ShowAccountGate = false;
        Navigate("Accounts");
    }
    public bool ShowDashboard       => Center == CenterView.Library;   // hero + grid (Library view only)
    public bool ShowCloudContent    => Center == CenterView.CloudSaves;
    public bool ShowAccountsContent => Center == CenterView.Accounts;
    public bool ShowBackupLocations => Center == CenterView.BackupLocations;
    public bool ShowPageHeader => !IsLibraryNav && !ShowBackupLocations;
    public bool ShowHealthView      => Center == CenterView.Health;
    public bool ShowSettingsView    => Center == CenterView.Settings;
    public bool ShowAboutView       => Center == CenterView.About;
    public bool ShowPrimaryCta      => Center == CenterView.Library;   // hero's Sync button

    private void RaiseState()
    {
        Guide.Refresh();   // every fact that can move the guide already routes through here
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(Center));
        OnPropertyChanged(nameof(ShowLocationPrompt));
        RaiseSharedNav();
        OnPropertyChanged(nameof(ShowBackupLocations));
        OnPropertyChanged(nameof(ShowHealthView));
        OnPropertyChanged(nameof(ShowSettingsView));
        Overview.RaiseHealth();
        OnPropertyChanged(nameof(ShowAboutView));
        // The rail's status set gates on ShowAboutView, so it re-raises with it.
        OnPropertyChanged(nameof(ShowRailStatus)); OnPropertyChanged(nameof(ShowRailCounts));
        OnPropertyChanged(nameof(ShowHealthBadgeInRail));
        OnPropertyChanged(nameof(ShowPrimaryCta));
        RaiseBarState();
    }

    public string AccountFootText
    {
        get
        {
            // During first-run onboarding the manifest is not bound yet -- show a neutral placeholder.
            if (_manifest is null) return "Account";
            var count = _manifest.Current.Accounts.Count;
            if (count == 0) return "Account";
            return count == 1 ? "Account (1)" : $"Accounts ({count})";
        }
    }

    // Account-icon dot: reads the same IsConnected the setup flow uses, so it cannot disagree with the buttons.
    public IBrush AccountStatusBrush => IsConnected ? Palette.SuccessGreen : Palette.InkMuted;
    public string AccountStatusText
        => IsConnected ? "Connected to GOG"
         : (_manifest?.Current.Accounts.Count ?? 0) == 0 ? "No GOG account connected yet"
         : "Not connected to GOG";
    public string AccountRailSub
        => IsConnected ? "connected"
         : (_manifest?.Current.Accounts.Count ?? 0) == 0 ? "no account"
         : "signed out";

    internal void RaiseAccountStatus()
    {
        OnPropertyChanged(nameof(AccountFootText));
        OnPropertyChanged(nameof(AccountStatusBrush));
        OnPropertyChanged(nameof(AccountStatusText));
        OnPropertyChanged(nameof(AccountRailSub));   // 
    }

    internal void RaiseDashboard() => Services.UiWatch.Time("RaiseDashboard", RaiseDashboardCore);
    private void RaiseDashboardCore()
    {
        Library.InvalidatePendingFileCount();   // one queue-builder walk per pass at most
        Accounts.RebuildRailAccountDots();
        OnPropertyChanged(nameof(HasAnyBackedUp));   // bound (Storage inventory gate), was never raised
        Library.RaiseDashboardReadouts();
        Overview.RaiseDashboardReadouts();
        Storage.RefreshBackupLocations(dashboardPass: true);
        OnPropertyChanged(nameof(PrimaryEnabled));
        OnPropertyChanged(nameof(ShowPrimaryCta));
        OnPropertyChanged(nameof(ShowDashboard));
        RaiseShowCompositionLine();
        RaiseState();
    }

    internal void RaisePrimaryEnabled() => OnPropertyChanged(nameof(PrimaryEnabled));
    internal void RaiseBarPrimaryLabel() => OnPropertyChanged(nameof(BarPrimaryLabel));
    internal void RaiseShowCompositionLine() => OnPropertyChanged(nameof(ShowCompositionLine));
    internal void RaiseRailActivity()
    {
        OnPropertyChanged(nameof(ShowRailActivity)); OnPropertyChanged(nameof(RailActivityText));
        OnPropertyChanged(nameof(RailOverviewExceptions));   // 
        OnPropertyChanged(nameof(RailActivityBrush)); OnPropertyChanged(nameof(RailActivityTip));
        Overview.RaiseRailOverviewSub();   // 
    }

    partial void OnIsConnectedChanged(bool value)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnIsConnectedChanged(value)); return; }
        RaiseDashboard();
        OnPropertyChanged(nameof(AccountStatusBrush));
        OnPropertyChanged(nameof(AccountStatusText));
        OnPropertyChanged(nameof(AccountRailSub));   // 
        OnPropertyChanged(nameof(QueueNeedsAccount));
        Overview?.RefreshQueueRowFacts();   // each row's NEEDS SIGN-IN follows the same fact
        OnPropertyChanged(nameof(ShowRailActivity)); OnPropertyChanged(nameof(RailActivityBrush)); OnPropertyChanged(nameof(RailActivityTip));
        OnPropertyChanged(nameof(RailOverviewExceptions));   // 
        RaiseDownloadControls();
        RaiseState();
        // Just connected while sitting on a secondary tab: populate it now.
        if (value && ShowCloudView) _ = CloudVm.DiscoverAsync();
        if (value && ShowAccountsView) { Accounts.RefreshAccounts(); _ = Accounts.EnsurePrimaryRegisteredAsync(); }
    }

    [ObservableProperty] private string? _libraryNotSavedReason;
    public bool LibraryNotSaved => LibraryNotSavedReason is not null;
    public string LibraryNotSavedLine => $"Your library could not be saved: {LibraryNotSavedReason}";
    internal void SaveInBackground(string what) => Runtime.ManifestSaves.Default.SaveInBackground(_manifest, what);

    partial void OnBusyChanged(bool value) { OnPropertyChanged(nameof(PrimaryEnabled)); Storage.RaiseInventory(); RaiseBarState(); if (!value) ResumeIfArmed(); }

    public MainWindowViewModel()
    {
        // Cloud saves screen: lazy accessors so the sub-VM reads live services set during init.
        CloudVm = new CloudSavesViewModel(
            () => _manifest, () => _apiHttp, () => _auth, () => _api,
            () => _backupRoot, () => IsConnected, RunBusy, PickFolderAsync,
            () => _manifest?.Current.Transfer.KeepCloudSaves ?? 0,   // (S2.3) manifest fact; the store may not exist yet when this lambda is built
            () => _servicesReady ? EngineSessions() : null);   // assigned in the ctor before this runs; the lambda hides that from the compiler
        CloudVm.HostLog = (m, e, c) => Log(m, isError: e, category: c);   // [Cloud] lines land in the shared log
        CloudVm.IsHiddenGame = IsHiddenGameId;   // 
        QueueRow.IsHiddenGame = IsHiddenGameId;  // one rule for every list that names a game
        QueueRow.RootLabel = id => _manifest is not null ? (Storage?.SlotNameOf(id) ?? "") : "";   // the active card names the SLOT (Primary / Secondary), never a path
        QueueRow.NeedsSignInFor = f => !IsConnected
            || (f.OwnerIds.Count > 0 && _manifest?.Current.Accounts is { Count: > 0 } accts && !f.OwnerIds.Any(o => accts.Any(a => a.Id == o)));
        // Re-raise the shim change notifications so the view's {Binding CloudStatus} (+ its error
        // color/size/weight) update live.
        CloudVm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(CloudSavesViewModel.CloudStatus): OnPropertyChanged(nameof(CloudStatus)); OnPropertyChanged(nameof(HasCloudStatus)); break;
                case nameof(CloudSavesViewModel.CloudStatusBrush): OnPropertyChanged(nameof(CloudStatusBrush)); break;
                case nameof(CloudSavesViewModel.CloudStatusFontSize): OnPropertyChanged(nameof(CloudStatusFontSize)); break;
                case nameof(CloudSavesViewModel.CloudStatusWeight): OnPropertyChanged(nameof(CloudStatusWeight)); break;
                case nameof(CloudSavesViewModel.CloudFolderText): OnPropertyChanged(nameof(CloudFolderText)); break;
                case nameof(CloudSavesViewModel.CloudFolderIsOverridden): OnPropertyChanged(nameof(CloudFolderIsOverridden)); break;
                case nameof(CloudSavesViewModel.CloudFilter): OnPropertyChanged(nameof(CloudFilter)); OnPropertyChanged(nameof(CloudCountText)); break;
                case nameof(CloudSavesViewModel.CloudSummary): OnPropertyChanged(nameof(CloudSummary)); break;
                case nameof(CloudSavesViewModel.HasCloudGames): OnPropertyChanged(nameof(HasCloudGames)); Guide?.Refresh(); break;   // the cloud stop's face follows the list (Guide is built after this hookup)
                case nameof(CloudSavesViewModel.CloudCountText): OnPropertyChanged(nameof(CloudCountText)); break;
                case nameof(CloudSavesViewModel.CloudSortGlyphName): OnPropertyChanged(nameof(CloudSortGlyphName)); break;
                case nameof(CloudSavesViewModel.CloudSortGlyphSize): OnPropertyChanged(nameof(CloudSortGlyphSize)); break;
                case nameof(CloudSavesViewModel.CloudSortGlyphLast): OnPropertyChanged(nameof(CloudSortGlyphLast)); break;
                case nameof(CloudSavesViewModel.CloudSortGlyphChanged): OnPropertyChanged(nameof(CloudSortGlyphChanged)); break;
                case nameof(CloudSavesViewModel.CloudSortLabel): OnPropertyChanged(nameof(CloudSortLabel)); break;   // 
                case nameof(CloudSavesViewModel.CanDownloadAll): OnPropertyChanged(nameof(CanDownloadAll)); break;
                case nameof(CloudSavesViewModel.CloudAllRunning): OnPropertyChanged(nameof(CloudAllRunning)); OnPropertyChanged(nameof(CanDownloadAll)); break;
                case nameof(CloudSavesViewModel.DownloadAllText): OnPropertyChanged(nameof(DownloadAllText)); break;
            }
        };
        // (S1.1) the session owns the clients and settings; the VM's members forward to it.
        Session = new Runtime.GrogSession(AppSettings.Load());
        Session.LogSink = (m, e, c) => Log(m, isError: e, category: c);
        // A save that fails must never be silent: the log gets it once, the Overview banner holds it until a save lands.
        Runtime.ManifestSaves.Default.LogError = m => Log(m, isError: true);
        Runtime.ManifestSaves.Default.NotSavedChanged = reason => Dispatcher.UIThread.Post(() =>
        {
            LibraryNotSavedReason = reason;
            OnPropertyChanged(nameof(LibraryNotSaved)); OnPropertyChanged(nameof(LibraryNotSavedLine));
        });
        AppSettings.Log = m => Log(m, isError: true);
        Storage = new StorageViewModel(this, Session);
        var rootNames = new HashSet<string>(GetType().GetProperties().Select(p => p.Name));
        void Reraise(object? _, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName is { } n && rootNames.Contains(n)) OnPropertyChanged(n); }
        Storage.PropertyChanged += Reraise;
        Library = new LibraryViewModel(this, Session);
        Library.PropertyChanged += Reraise;
        Overview = new OverviewViewModel(this, Session);
        Overview.PropertyChanged += Reraise;
        Accounts = new AccountsViewModel(this, Session);
        Accounts.PropertyChanged += Reraise;
        Settings = new SettingsViewModel(this, Session);
        Settings.PropertyChanged += Reraise;
        _ = Accounts.RefreshTokenProtectionSnapshotAsync();   // off-thread keyring probe; the token-safety card reads the snapshot only
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Grog/0.1");
        _apiHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Grog/0.1");
        Guide = new GuideViewModel(this);
        Schedule = new ScheduleViewModel(this);
        Guide.Changed += OnGuideChanged;
        var trayWanted = _settings.CloseAction == CloseWindowAction.MinimizeToTray && TraySupport.IsLikelySupported();
        if (_settings.MinimizeToTray != trayWanted)
        {
            _settings.MinimizeToTray = trayWanted;
            _settings.MinimizeToTrayChosen = true;
            _settings.SaveSoon();
        }
        _ = StartAsync();
    }

    // --- rail navigation ---
    public enum View { Library, Updates, Missing, Drives, CloudSaves, Overview, Accounts, Settings, About }

    public enum SetupPhase { NoLocation, NoAccount, NoLibrary, Cataloged }

    public enum CenterView { LocationPrompt, Library, CloudSaves, Health, Accounts, BackupLocations, Settings, About }
    [ObservableProperty] private View _currentView = View.Overview;

    private bool _bypassLeaveGuard;

    internal const string CloseTarget = "__close__";
    public event Action? CloseAfterLeave;

    internal void NavigateLeaving(string? view)
    {
        if (view == CloseTarget) { CloseAfterLeave?.Invoke(); return; }
        _bypassLeaveGuard = true;
        try { Navigate(view); }
        finally { _bypassLeaveGuard = false; }
    }

    [RelayCommand]
    private async Task AddSecondaryStorageFromBanner()
    {
        Navigate("Drives");
        if (Storage.CanAddSecondary) await Storage.AddLocationCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void NavigateToOtherStorage()
    {
        Navigate("Drives");
        if (CurrentView != View.Drives) return;   // the Settings leave-guard held us; nothing to reveal yet
        Dispatcher.UIThread.Post(() => Storage.RevealOtherStorage(), DispatcherPriority.Background);   // after the page is realised
    }

    [RelayCommand]
    internal void Navigate(string? view)
    {
        if (!_bypassLeaveGuard && Center == CenterView.Settings && view != "Settings"
            && Settings.ExtrasCard.HasPending)
        {
            Settings.ExtrasCard.AskBeforeLeaving(view);
            return;
        }
        Storage.HideOtherStorage();   // the section folds again on every page change
        _hasDetour = false;
        CurrentView = view switch
        {
            "Updates" => View.Updates,
            "Missing" => View.Missing,
            "Gone" => View.Missing,
            "Drives" => View.Drives,
            "CloudSaves" => View.CloudSaves,
            "Overview" => View.Overview,
            "Accounts" => View.Accounts,
            "Settings" => View.Settings,
            "About" => View.About,
            _ => View.Library,
        };
        switch (view)
        {
            case "Updates": Library.QuickFilter = "updates"; break;
            case "Missing": Library.QuickFilter = "missing"; break;
            case "Gone": Library.QuickFilter = "gone"; break;
            case "CloudSaves": CloudVm.RefreshFromManifest(); Library.QuickFilter = ""; break;
            case "Drives":
                Storage.RefreshBackupLocations();
                Storage.RebuildInventory();
                Library.QuickFilter = "";
                break;
            case "Overview": Storage.RefreshBackupLocations(); Overview.RaiseHealth(); Library.QuickFilter = ""; break;
            case "Accounts": Accounts.RefreshAccounts(); _ = Accounts.EnsurePrimaryRegisteredAsync(); Library.QuickFilter = ""; break;
            default: Storage.RefreshBackupLocations(); Library.QuickFilter = ""; break;
        }
        Library.ApplyFilter();
        RaiseNav();
        RaiseState();   // Center-derived content (ShowSettingsView / ShowBackupLocations) must refresh too
    }

    public string CurrentRootText => string.IsNullOrEmpty(_backupRoot) ? "(not set)" : _backupRoot;

    // ---- Cloud saves screen (logic lives in CloudSavesViewModel) ----
    public CloudSavesViewModel CloudVm { get; }

    public System.Collections.ObjectModel.ObservableCollection<CloudGameRow> CloudGames => CloudVm.CloudGames;
    public System.Collections.ObjectModel.ObservableCollection<object> CloudItems => CloudVm.CloudItems;
    public string CloudStatus => CloudVm.CloudStatus;
    public bool HasCloudStatus => CloudStatus.Length > 0;
    public Avalonia.Media.IBrush CloudStatusBrush => CloudVm.CloudStatusBrush;
    public double CloudStatusFontSize => CloudVm.CloudStatusFontSize;
    public Avalonia.Media.FontWeight CloudStatusWeight => CloudVm.CloudStatusWeight;
    public string CloudFolderText => CloudVm.CloudFolderText;
    public bool CloudFolderIsOverridden => CloudVm.CloudFolderIsOverridden;
    public System.Windows.Input.ICommand DownloadCloudSaveCommand => CloudVm.DownloadCloudSaveCommand;
    public System.Windows.Input.ICommand SaveAsCommand => CloudVm.SaveAsCommand;
    public System.Windows.Input.ICommand ChangeCloudFolderCommand => CloudVm.ChangeCloudFolderCommand;
    public System.Windows.Input.ICommand ResetCloudFolderCommand => CloudVm.ResetCloudFolderCommand;
    public System.Windows.Input.ICommand CheckForNewSavesCommand => CheckCloudSavesCommand;

    [RelayCommand]
    private void CheckCloudSaves()
    {
        if (!RequireAccount("look for your cloud saves")) return;
        CloudVm.CheckForNewSavesCommand.Execute(null);
    }
    // Cloud search / sort / summary shims.
    public string CloudFilter { get => CloudVm.CloudFilter; set => CloudVm.CloudFilter = value; }
    public string CloudSummary => CloudVm.CloudSummary;
    public bool HasCloudGames => CloudVm.HasCloudGames;
    public string CloudCountText => CloudVm.CloudCountText;
    public string CloudSortGlyphName => CloudVm.CloudSortGlyphName;
    public string CloudSortGlyphSize => CloudVm.CloudSortGlyphSize;
    public string CloudSortGlyphLast => CloudVm.CloudSortGlyphLast;
    public string CloudSortGlyphChanged => CloudVm.CloudSortGlyphChanged;
    public string CloudSortLabel => CloudVm.CloudSortLabel;   // 
    public System.Windows.Input.ICommand SortCloudCommand => CloudVm.SortCloudCommand;
    public bool CanDownloadAll => CloudVm.CanDownloadAll;
    public bool CloudAllRunning => CloudVm.CloudAllRunning;
    public string DownloadAllText => CloudVm.DownloadAllText;
    public System.Windows.Input.ICommand DownloadAllCloudCommand => CloudVm.DownloadAllCloudCommand;
    public System.Windows.Input.ICommand CancelDownloadAllCloudCommand => CloudVm.CancelDownloadAllCloudCommand;
    // LocalSave-history shims: expand a game, then open / restore / prune a specific local save.
    public System.Windows.Input.ICommand ToggleLocalSavesCommand => CloudVm.ToggleLocalSavesCommand;
    public System.Windows.Input.ICommand OpenLocalSaveFolderCommand => CloudVm.OpenLocalSaveFolderCommand;
    public System.Windows.Input.ICommand RestoreLocalSaveCommand => CloudVm.RestoreLocalSaveCommand;
    public System.Windows.Input.ICommand AskDeleteLocalSaveCommand => CloudVm.AskDeleteLocalSaveCommand;
    public System.Windows.Input.ICommand CancelDeleteLocalSaveCommand => CloudVm.CancelDeleteLocalSaveCommand;
    public System.Windows.Input.ICommand DeleteLocalSaveCommand => CloudVm.DeleteLocalSaveCommand;

    internal async Task<string?> PickFolderAsync(string title, string? seed)
    {
        var top = OwnerWindowResolver?.Invoke();
        if (top is null) return null;
        try
        {
            Avalonia.Platform.Storage.IStorageFolder? start = null;
            // The seed is usually a backup root: a sleeping drive answers Directory.Exists in seconds, so ask off the UI thread.
            try { if (!string.IsNullOrEmpty(seed) && await Task.Run(() => System.IO.Directory.Exists(seed))) start = await top.StorageProvider.TryGetFolderFromPathAsync(seed); }
            catch { start = null; }
            var picked = await top.StorageProvider.OpenFolderPickerAsync(
                new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = start });
            return picked.FirstOrDefault()?.TryGetLocalPath();
        }
        catch { return null; }
    }
}

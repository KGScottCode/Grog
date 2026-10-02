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

/// <summary>
/// The window's composition root and shell (S3.4, 09-08): builds the session and the page view models
/// (Storage, Library, Overview, Accounts, Settings, plus Guide, Schedule and the cloud-saves VM), owns
/// navigation and the rail, Busy / RunBusy and the log, the primary-action bar state machine, the connect /
/// sign-in orchestration and the download run (the partials). Holds no backup logic of its own -- it drives
/// the same Grog.Core runs the CLI uses.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    /// <summary>The application state that is not a view (S1, 09-08): manifest store, GOG client and session,
    /// settings, root, live engine, cancellation. Page view models receive this; the partials below reach it
    /// through the forwarding members that keep their existing names while the split proceeds.</summary>
    public Runtime.GrogSession Session { get; }

    /// <summary>The Storage page's view model (S3.1): locations, roots, import, layout moves, device grids.</summary>
    public StorageViewModel Storage { get; }

    /// <summary>The Library page's view model (S3.2): rows, filters, selection, detail pane, scope, scan panel.</summary>
    public LibraryViewModel Library { get; }

    /// <summary>The Overview page's view model (S3.3): health, lede, issues, run readouts, queue pane, log, toast.</summary>
    public OverviewViewModel Overview { get; }

    /// <summary>The Accounts page's view model (S3.4): account cards, add / log out / remove, sign-in security, rail dots.</summary>
    public AccountsViewModel Accounts { get; }

    /// <summary>The Settings page's view model (S3.4): theme, launch checks, transfer, retention, notifications,
    /// close action, fresh library, extras layout and the backup policy preview.</summary>
    public SettingsViewModel Settings { get; }

    // ---- Forwarders onto the session (S1.1: same names as the fields they replaced, so no call site moved).
    private string _backupRoot { get => Session.BackupRoot; set => Session.BackupRoot = value; }
    /// <summary>The backup root, for the sub-view-models that need to construct a layout.</summary>
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

    /// <summary>DEV ONLY: simulate a fresh install (forget the root, empty the manifest) while leaving the
    /// token file untouched so the account stays connected, then re-run the first-run path.
    /// Compile-gated: a release binary must not carry a manifest-wiping command behind a visibility binding.</summary>
    [RelayCommand]
    private async Task DevResetFirstRun()
    {
#if !DEBUG
        await Task.CompletedTask;
        return;
#else
        var paths = Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot);   // config dir only; no root probe on the dispatcher (UI-thread sweep 09-06 r2)
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
        _manifest.Mutate(m =>   // (manifest gate 09-08)
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

    /// <summary>The backup root changed at runtime (first-run auto-apply / DEV reset / bind / re-point): notify
    /// the computed HasLocation family on both the shell and the storage VM so bindings re-evaluate instead of
    /// staying stale-false from before a root existed.</summary>
    internal void RaiseRootPathChanged()
    {
        OnPropertyChanged(nameof(HasLocation));
        OnPropertyChanged(nameof(CurrentRootText));
        Storage.RaiseLocationState();
    }

    /// <summary>Exactly one center panel, from (phase x tab). Location must come first everywhere (the
    /// token + manifest live under it); local tabs do not require an account.</summary>
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
                // Every page renders signed out: only ACTIONS need an account, and those gate individually.
                View.CloudSaves => CenterView.CloudSaves,
                // Accounts always shows the real page; its dashed invitation slot is the sign-in control.
                View.Accounts   => CenterView.Accounts,
                // Connected but no library yet: Library scaffolding with an empty grid + "scan to populate" hint.
                _               => CenterView.Library,
            };
        }
    }

    // Per-panel gates -- each reads the single Center value, so two can never show at once.
    public bool ShowLocationPrompt  => Center == CenterView.LocationPrompt;

    /// <summary>The account gate: a small dialog raised by an ACTION that needs an account. It routes to
    /// Accounts rather than signing in here -- one home for that control.</summary>
    [ObservableProperty] private bool _showAccountGate;
    [ObservableProperty] private string _accountGateWhat = "";

    /// <summary>True when the action can proceed. When it cannot, raises the gate and returns false, so a
    /// caller reads as: <c>if (!RequireAccount("scan your library")) return;</c></summary>
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
    /// <summary>The top page-title bar. Hidden on Library (its own toolbar) AND on Local Folders, where the
    /// left panel carries its own "Local Folders" card header -- so the title isn't shown twice.</summary>
    public bool ShowPageHeader => !IsLibraryNav && !ShowBackupLocations;
    public bool ShowHealthView      => Center == CenterView.Health;
    public bool ShowSettingsView    => Center == CenterView.Settings;
    public bool ShowAboutView       => Center == CenterView.About;
    public bool ShowPrimaryCta      => Center == CenterView.Library;   // hero's Sync button

    /// <summary>Raise everything derived from app state; call when a fact or the tab changes.</summary>
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

    /// <summary>Rail footer account line: a COUNT, never a username. The rail is in every screenshot a
    /// user posts to an issue or a forum, and a GOG username is the one piece of identifying data on it.
    /// The Accounts page names them; the rail only says how many.</summary>
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
    /// <summary>Green only when the session works against the GOG API, not when a token file exists.</summary>
    public IBrush AccountStatusBrush => IsConnected ? Palette.SuccessGreen : Palette.InkMuted;
    /// <summary>Tooltip on the same rail row, so it withholds the username for the same reason.</summary>
    public string AccountStatusText
        => IsConnected ? "Connected to GOG"
         : (_manifest?.Current.Accounts.Count ?? 0) == 0 ? "No GOG account connected yet"
         : "Not connected to GOG";
    /// <summary>(UX 09-08 #6) Rail footer second line. THREE states, not two: "signed out" on a machine
    /// that has never had an account reads as though something was lost, which is the first thing a new
    /// user sees (macOS first-run walk, 09-11). No account is a starting point; signed out is a session
    /// that ended. The count is the same one AccountFootText reads, so the two lines cannot disagree.</summary>
    public string AccountRailSub
        => IsConnected ? "connected"
         : (_manifest?.Current.Accounts.Count ?? 0) == 0 ? "no account"
         : "signed out";

    /// <summary>The rail's account trio: count line, dot brush and tip. Raised by the Accounts VM after every
    /// account event and by the connect path.</summary>
    internal void RaiseAccountStatus()
    {
        OnPropertyChanged(nameof(AccountFootText));
        OnPropertyChanged(nameof(AccountStatusBrush));
        OnPropertyChanged(nameof(AccountStatusText));
        OnPropertyChanged(nameof(AccountRailSub));   // (UX 09-08 #6)
    }

    internal void RaiseDashboard() => Services.UiWatch.Time("RaiseDashboard", RaiseDashboardCore);
    private void RaiseDashboardCore()
    {
        Library.InvalidatePendingFileCount();   // one queue-builder walk per pass at most (UI-thread sweep 09-06 r2)
        Accounts.RebuildRailAccountDots();
        // RaiseHealth runs ONCE, via RaiseState at the end of this method (it used to run here as well:
        // two full library walks, two run-journal reads and six bound-collection clears per refresh,
        // at 1 Hz during a download).
        OnPropertyChanged(nameof(HasAnyBackedUp));   // bound (Storage inventory gate), was never raised
        // (S3.2 / S3.3) the library counts and the Overview's coverage / rollup readouts are the pages' own;
        // each raises its set.
        Library.RaiseDashboardReadouts();
        Overview.RaiseDashboardReadouts();
        // Language + platform options depend on the manifest and the scope, never on download progress: they are
        // rebuilt at the end of RebuildRows (every manifest/scope refresh), not on every 1 Hz pass (UI-thread sweep 09-06).
        // Storage rows are rendered on Overview and Drives (and the rail dots everywhere), so they refresh on
        // EVERY dashboard pass; the old IsLibraryNav gate excluded exactly the pages that show them, which is
        // how Clear left a phantom "queued here" segment until the next navigation (owner, 09-04).
        // RaiseState -> RaiseHealth below re-tallies the library anyway, so the storage pass skips its own (UI-thread sweep 09-06).
        Storage.RefreshBackupLocations(dashboardPass: true);
        // NOT RebuildInventory here: it replaces every grid row (selection, expand state, type filter go with
        // them) and this runs at 1 Hz during a run. The grids rebuild per landed/moved file and per command.
        OnPropertyChanged(nameof(PrimaryEnabled));
        OnPropertyChanged(nameof(ShowPrimaryCta));
        OnPropertyChanged(nameof(ShowDashboard));
        RaiseShowCompositionLine();
        RaiseState();
    }

    // ---- The children's small surface back onto the shell (S3.2 / S3.3): single-name raises for the root-owned
    //      readouts a page's own state moves. ----
    internal void RaisePrimaryEnabled() => OnPropertyChanged(nameof(PrimaryEnabled));
    internal void RaiseBarPrimaryLabel() => OnPropertyChanged(nameof(BarPrimaryLabel));
    internal void RaiseShowCompositionLine() => OnPropertyChanged(nameof(ShowCompositionLine));
    internal void RaiseRailActivity()
    {
        OnPropertyChanged(nameof(ShowRailActivity)); OnPropertyChanged(nameof(RailActivityText));
        OnPropertyChanged(nameof(RailOverviewExceptions));   // (Rail exceptions 09-09)
        OnPropertyChanged(nameof(RailActivityBrush)); OnPropertyChanged(nameof(RailActivityTip));
        Overview.RaiseRailOverviewSub();   // (UX 09-08 #6)
    }

    partial void OnIsConnectedChanged(bool value)
    {
        // Marshal: RunBusy flips IsConnected on a background thread, and this handler touches Avalonia objects.
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnIsConnectedChanged(value)); return; }
        RaiseDashboard();
        OnPropertyChanged(nameof(AccountStatusBrush));
        OnPropertyChanged(nameof(AccountStatusText));
        OnPropertyChanged(nameof(AccountRailSub));   // (UX 09-08 #6)
        // The queue chip and Resume/Connect swap key off IsConnected too.
        OnPropertyChanged(nameof(QueueNeedsAccount));
        Overview?.RefreshQueueRowFacts();   // (09-19) each row's NEEDS SIGN-IN follows the same fact
        OnPropertyChanged(nameof(ShowRailActivity)); OnPropertyChanged(nameof(RailActivityBrush)); OnPropertyChanged(nameof(RailActivityTip));
        OnPropertyChanged(nameof(RailOverviewExceptions));   // (Rail exceptions 09-09)
        RaiseDownloadControls();
        RaiseState();
        // Just connected while sitting on a secondary tab: populate it now.
        if (value && ShowCloudView) _ = CloudVm.DiscoverAsync();
        if (value && ShowAccountsView) { Accounts.RefreshAccounts(); _ = Accounts.EnsurePrimaryRegisteredAsync(); }
    }

    /// <summary>The reason the last manifest save failed, or null while saves land (the Overview banner).</summary>
    [ObservableProperty] private string? _libraryNotSavedReason;
    public bool LibraryNotSaved => LibraryNotSavedReason is not null;
    public string LibraryNotSavedLine => $"Your library could not be saved: {LibraryNotSavedReason}";
    /// <summary>Fire-and-forget manifest save with a failure surfaced (log once, Overview banner).</summary>
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
        CloudVm.IsHiddenGame = IsHiddenGameId;   // (09-19)
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
                case nameof(CloudSavesViewModel.CloudSortLabel): OnPropertyChanged(nameof(CloudSortLabel)); break;   // (UX 09-08 #4)
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
        // (S3.1) the Storage page's view model, built once the session exists. Every child's property changes
        // are re-raised under the same names here: the window's code-behind listens on the root, and the
        // shell's own readouts that read a child (rail badge, bar state) follow the child's raises.
        Storage = new StorageViewModel(this, Session);
        // Re-raise a child's change on the root ONLY for names the root still declares (S4, 09-08): with the
        // forwarders gone, an unfiltered re-raise woke every root binding on every child change for nothing
        // (RaiseDashboard 18 -> 29 ms at 1,500 games in the stress run).
        var rootNames = new HashSet<string>(GetType().GetProperties().Select(p => p.Name));
        void Reraise(object? _, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName is { } n && rootNames.Contains(n)) OnPropertyChanged(n); }
        Storage.PropertyChanged += Reraise;
        // (S3.2 / S3.3) the Library and Overview page view models, in that order; children reach each other
        // through this root, never in their ctors.
        Library = new LibraryViewModel(this, Session);
        Library.PropertyChanged += Reraise;
        Overview = new OverviewViewModel(this, Session);
        Overview.PropertyChanged += Reraise;
        // (S3.4) the Accounts and Settings page view models; the same re-raise keeps the window's code-behind
        // handlers (owner column, tray flag) and the rail listening on the root.
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
        // The tray follows the close-action pill (one control, owner call 08-30): the icon exists exactly
        // when "close keeps Grog running in the tray" is chosen, so sync the stored flag on every load --
        // it also migrates profiles from the era of the separate tray toggle.
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

    /// <summary>The app's setup lifecycle, derived purely from facts (location, account, library).
    /// Everything the UI shows is a function of this + the current tab, so states cannot conflict.</summary>
    public enum SetupPhase { NoLocation, NoAccount, NoLibrary, Cataloged }

    /// <summary>Exactly one fills the main pane at any time, computed from (SetupPhase x tab).</summary>
    public enum CenterView { LocationPrompt, Library, CloudSaves, Health, Accounts, BackupLocations, Settings, About }
    // OVERVIEW IS HOME: the app opens on the verdict ("is my backup okay?"), not the catalog.
    [ObservableProperty] private View _currentView = View.Overview;

    /// <summary>Set while the extras card's leave prompt is answering: the same Navigate call comes back
    /// through here and must not be caught by the guard a second time.</summary>
    private bool _bypassLeaveGuard;

    /// <summary>The leave prompt's "destination" when what triggered it was the window's X, not a page change.
    /// Answering it raises <see cref="CloseAfterLeave"/> instead of navigating.</summary>
    internal const string CloseTarget = "__close__";
    /// <summary>The window re-issues its Close when this fires (the staged card has been applied or discarded).</summary>
    public event Action? CloseAfterLeave;

    /// <summary>Leave the page without re-asking. Only the leave prompt calls this.</summary>
    internal void NavigateLeaving(string? view)
    {
        if (view == CloseTarget) { CloseAfterLeave?.Invoke(); return; }
        _bypassLeaveGuard = true;
        try { Navigate(view); }
        finally { _bypassLeaveGuard = false; }
    }

    /// <summary>The won't-fit banner's amber action (09-13): go to Storage and open the second-storage folder
    /// picker in one click, so "more room" is one step, not a page hunt.</summary>
    [RelayCommand]
    private async Task AddSecondaryStorageFromBanner()
    {
        Navigate("Drives");
        if (Storage.CanAddSecondary) await Storage.AddLocationCommand.ExecuteAsync(null);
    }

    /// <summary>(09-14) The rail's "Other storage · N" sub-entry: the Storage page, scrolled to that section, expanded.</summary>
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
        // The extras card is the one place on Settings where choices are staged rather than applied on
        // click, so it is the one place a page change would silently throw work away. Ask instead.
        if (!_bypassLeaveGuard && Center == CenterView.Settings && view != "Settings"
            && Settings.ExtrasCard.HasPending)
        {
            Settings.ExtrasCard.AskBeforeLeaving(view);
            return;
        }
        Storage.HideOtherStorage();   // (09-15) the section folds again on every page change (after the guard, review 09-15)
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
            // NO auto-discovery on activation: looking at a tab must never query GOG. The page renders what
            // the manifest knows; discovery runs in the library scan or the explicit Check-for-new-saves button.
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
        // Navigation is a RENDER, not an Action: never re-derive here -- RecomputeActivity on this path
        // races the background download thread.
        RaiseNav();
        RaiseState();   // Center-derived content (ShowSettingsView / ShowBackupLocations) must refresh too
    }

    public string CurrentRootText => string.IsNullOrEmpty(_backupRoot) ? "(not set)" : _backupRoot;

    // ---- Cloud saves screen (logic lives in CloudSavesViewModel) ----
    public CloudSavesViewModel CloudVm { get; }

    // Binding shims: the Cloud Saves view binds these names on the shell; forwarding keeps the XAML unchanged.
    public System.Collections.ObjectModel.ObservableCollection<CloudGameRow> CloudGames => CloudVm.CloudGames;
    public System.Collections.ObjectModel.ObservableCollection<object> CloudItems => CloudVm.CloudItems;
    public string CloudStatus => CloudVm.CloudStatus;
    /// <summary>Empty status = no line at all: a StackPanel spends Spacing on a zero-height child.</summary>
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
    /// <summary>Relayed through the account GATE: the page renders signed out, so its one action must say
    /// why it cannot run rather than failing quietly.</summary>
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
    public string CloudSortLabel => CloudVm.CloudSortLabel;   // (UX 09-08 #4)
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

    /// <summary>Open the OS folder picker seeded at <paramref name="seed"/>. Returns the chosen local
    /// path or null. Shared by sub-VMs that need a folder (cloud-save archive + Save As).</summary>
    internal async Task<string?> PickFolderAsync(string title, string? seed)
    {
        var top = OwnerWindowResolver?.Invoke();
        if (top is null) return null;
        try
        {
            Avalonia.Platform.Storage.IStorageFolder? start = null;
            // The seed is usually a backup root: a sleeping drive answers Directory.Exists in seconds, so ask off the UI thread (09-25).
            try { if (!string.IsNullOrEmpty(seed) && await Task.Run(() => System.IO.Directory.Exists(seed))) start = await top.StorageProvider.TryGetFolderFromPathAsync(seed); }
            catch { start = null; }
            var picked = await top.StorageProvider.OpenFolderPickerAsync(
                new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = start });
            return picked.FirstOrDefault()?.TryGetLocalPath();
        }
        catch { return null; }
    }
}
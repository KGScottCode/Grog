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
    /// <summary>Page-visibility facts BOTH RaiseState and RaiseNav must raise; a single list, because two
    /// copies drifting apart is a page that shows under one trigger and not the other.</summary>
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

    /// <summary>Set by the view so the login broker can find the owner window at call time.</summary>
    public Func<Avalonia.Controls.TopLevel?>? OwnerWindowResolver { get; set; }

    // --- connection state (drives S0/S1 and the state-aware primary button) ---
    [ObservableProperty] private bool _isConnected;

    /// <summary>True until StartAsync has determined what to show (grid vs onboarding), so the main
    /// pane stays blank (rail only) instead of flashing the welcome screen on load.</summary>
    [ObservableProperty] private bool _initializing = true;

    /// <summary>Completes once the content pane is visible; the launch checks that stat disk and rescan GOG wait on it.</summary>
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task StartAsync()
    {
        try
        {
            try { await EnsureServicesAsync(); }
            catch (Exception ex) when (ex is Grog.Core.Manifest.ManifestTooNewException or Grog.Core.Manifest.ManifestLockedException)
            {
                // A newer Grog wrote this profile (09-08), or another program holds it open: say so and stop
                // short of touching it. The wizard would bind a fresh library over the real one.
                Log(ex.Message, isError: true);
                Storage.ShowLocationError(ex.Message);
                Initializing = false;
                return;
            }
            catch { /* services retry when the wizard starts; don't skip the welcome */ }

            // Core seam hookup (GrogPaths.PortableFallbackReason): a portable GrogData that cannot be written fell back to the profile folder; say so once.
            if (Grog.Core.Storage.GrogPaths.PortableFallbackReason is { } fallback)
                Log($"GrogData is read-only; using the profile folder at {_configDir} ({fallback}).");

            string? remembered = null;
            // Resolved through GrogPaths: a portable copy remembers its root as "{portable}/..." so a
            // drive-letter change re-anchors here instead of reading as a missing folder (and then,
            // catastrophically, as a first run that binds a fresh Documents default).
            try { remembered = Grog.Core.Storage.GrogPaths.ResolvePath(_settings.ChosenRoot ?? ""); }   // the live settings, not a second Load()
            catch { remembered = null; }

            bool rootBound = false;
            bool hasRemembered = !string.IsNullOrWhiteSpace(remembered);
            if (_servicesReady && hasRemembered)
            {
                // Bind the REMEMBERED root even if it is not reachable right now (an unplugged external
                // drive, a NAS that is asleep): the storage page then says "not connected". It must never
                // fall through to the first-run branch below, which would silently re-home the library
                // to Documents and overwrite ChosenRoot - the path to the real backup would be gone.
                try { await BindToRootAsync(remembered!); rootBound = true; }
                catch (Exception ex) { Log($"Backup storage {remembered} is not reachable right now: {ex.Message}", isError: true); }
            }

            // First run = NO remembered root at all. Auto-apply the default backup folder so the ONLY
            // remaining prerequisite is connecting an account.
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
            // Welcome FIRST and once, then the guide; never both on screen (the welcome scrim is hit-testable
            // and swallows every click in the guide's spotlight hole). FirstRunGuideDone (set by Exit or
            // completion) is the ONLY re-arm gate -- never re-arm on "setup looks unfinished", or a deliberate
            // exit gets the tour on every launch. A restart mid-tour resumes the guide directly.
            if (!_settings.WelcomeShown && !_settings.FirstRunGuideDone) ShowWelcomeOverlay = true;
            else if (!_settings.FirstRunGuideDone) Guide.Resume();
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => Initializing = false);
            _initialized.TrySetResult();
        }
    }

    /// <summary>First-run welcome overlay (scrim over the app; defaults already applied). Its CTA sends
    /// the user to the Accounts tab; the secondary link just dismisses to the app.</summary>
    [ObservableProperty] private bool _showWelcomeOverlay;

    /// <summary>"Skip for now": dismiss to the full app. Also sets <c>FirstRunGuideDone</c> -- declining is a
    /// deliberate "leave me alone"; Settings carries "Show me around" for a full re-run.</summary>
    [RelayCommand]
    private void SkipToApp()
    {
        Guide.WelcomeReopenedFromGuide = false;   // skipping from a reopened welcome is still a deliberate exit
        ShowWelcomeOverlay = false;
        MarkWelcomeShown();
        _settings.FirstRunGuideDone = true;
        _settings.SaveSoon();
        // (UI-thread sweep 09-06 r2) scaffolding creates folders on the backup drive: off the dispatcher
        // (best-effort, same as ScaffoldChosenFolder's own catch). The dashboard raise does not depend on it.
        var manifestSk = _manifest; var rootSk = _backupRoot;   // skipping the tour still accepts the default folder
        if (manifestSk is not null && !string.IsNullOrWhiteSpace(rootSk))
            _ = Task.Run(() => { try { new Grog.Core.Volumes.BackupLayout(manifestSk.Current, rootSk).ScaffoldCategoryFolders(); } catch { } });
        RaiseDashboard();
    }

    /// <summary>"Show me around": hand off to the guide WITHOUT navigating first -- jumping ahead would
    /// perform the step the user is about to be shown.</summary>
    [RelayCommand]
    private void WelcomeGetStarted()
    {
        ShowWelcomeOverlay = false;
        MarkWelcomeShown();
        // Re-entered by the guide's own Back: RESUME at the same stop. StartGuide is a genuine re-run and
        // resets the tour; walking back to the welcome and forward again must lose nothing.
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

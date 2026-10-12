// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Download;
using Grog.Core.Format;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Verify;

public partial class MainWindowViewModel
{

    internal void RebindInteractiveSession(Grog.Core.Storage.GrogPaths paths)
        => Session.RebindInteractiveSession(new Services.WebViewLoginProvider(() => OwnerWindowResolver?.Invoke()), paths);   // (S1.1) moved to the session

    private async Task EnsureServicesAsync()
    {
        if (_servicesReady) return;
        await Session.OpenStoreAsync();   // (S1.1) paths, store, load, legacy account migration live in the session
        var paths = Session.ConfigPaths!;
        await Task.Run(Storage.PreseedDriveSamples);   // warm the drive samples off-thread before the UI builds storage rows
        RebindInteractiveSession(paths);
        bool sessionTokenReadable;
        try { sessionTokenReadable = await _tokenStore.LoadAsync() is not null; }
        catch { sessionTokenReadable = false; }
        var interactivePath = _interactiveTokenPath;
        var (anyConnected, interactiveExists) = await Task.Run(() =>
            (sessionTokenReadable || AnyAccountConnected(), System.IO.File.Exists(interactivePath)));
        IsConnected = sessionTokenReadable;
        if (!IsConnected) IsConnected = anyConnected;   // a signed-in secondary is a connection
        Library.AdoptManifestScope();   // scope lives in the manifest; seed it from legacy AppSettings once
        _servicesReady = true;
        Log("startup: services ready");
        // UI-thread watchdog: stalls land in the Activity log with the slow pass named.
        Services.UiWatch.Start(msg => Log(msg, isError: true, category: LogCategory.General));
        Grog.Core.Manifest.ManifestGate.SlowWait = ms => Services.UiWatch.Note("waiting for the manifest gate", ms);
        // THE UI THREAD RUNS ONLY UI: Core's heavy entry points report a call from it, once per entry point.
        Grog.Core.UiThreadGuard.IsUiThread = () => Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
        Grog.Core.UiThreadGuard.Violation = what => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            Log($"UI thread ran {what}. It must run off the UI thread; please report this line.", isError: true, category: LogCategory.General));
        GameRowViewModel.ArtDir = paths.ArtDir;
        // Build the folder list at startup: Overview is home and set directly on CurrentView, so Navigate
        Storage.RefreshBackupLocations();
        await Accounts.RefreshAccountsAsync();   // rows are built after the off-thread token probe
        RaiseState();
        Schedule.StartScheduleTimer(); 
        Settings.OnServicesReady();   
        Schedule.RaiseScheduleBindings();
        Schedule.CheckOverdueBackupOnLaunch();
        Storage.StartVolumeWatch();  
        if (sessionTokenReadable)
            _ = FetchAccountNameAsync();
        else if (interactiveExists)
            Log("Couldn't restore your session - the stored sign-in can't be read right now (keyring locked or unavailable). Sign in again from the Accounts page, or restart after unlocking the keyring.", isError: true);
        if (Grog.Core.Auth.FileTokenStore.PlainTextPreferred)
            Log("Sign-in tokens are stored UNENCRYPTED on this machine (scheduled-tasks opt-out; change it under Accounts > Sign-in Security)");
    }

    private async Task FetchAccountNameAsync()
    {
        try
        {
            var user = await _api.GetUserInfoAsync();
            await Accounts.RegisterPrimaryAsync(user.Username);
        }
        catch
        {
            var m = _manifest; var root = _backupRoot;
            bool sibling;
            try
            {
                sibling = await Task.Run(() =>
                {
                    if (m is null) return false;
                    var svc = new Grog.Core.Auth.AccountService(m, Grog.Core.Storage.GrogPaths.ResolveNoProbe(root));
                    return m.Current.Accounts.ToArray().Any(
                        a => a.Id.Length > 0 && System.IO.File.Exists(svc.TokenPathFor(a.Id)));
                });
            }
            catch { sibling = false; }
            Dispatcher.UIThread.Post(() =>
            {
                IsConnected = sibling;
                Log("Session expired - sign in again from the Accounts page");
                RaiseState();
                Accounts.RefreshAccounts();
            });
        }
    }

    private async Task BindToRootAsync(string root)
    {
        await EnsureServicesAsync();
        var m = _manifest;
        bool primaryAdded = m.Read(cur => cur.Roots.Count == 0 || cur.PrimaryRootId is null);   // the only case EnsurePrimary changes anything
        m.Mutate(cur => Grog.Core.Volumes.BackupLayout.EnsurePrimary(cur, Grog.Core.Storage.GrogPaths.ResolveNoProbe(root).BackupRoot));   // 

        var bound = await Task.Run(() =>
        {
            var p = Grog.Core.Storage.GrogPaths.Resolve(root);
            _ = new Grog.Core.Volumes.BackupLayout(m.Current, p.BackupRoot);   // binds roots (per-root field writes only now)
            string? livePrimary = null;
            if (m.Current.PrimaryRootId is { } pid
                && m.Current.Roots.FirstOrDefault(r => r.Id == pid)?.PathHint is { Length: > 0 } hint
                && System.IO.Directory.Exists(hint))   // a dead hint (portable drive re-lettered) never displaces a live folder
                livePrimary = hint;
            return (Paths: p, LivePrimary: livePrimary);
        });
        var paths = bound.Paths;
        if (!ReferenceEquals(m, _manifest))
            _ = new Grog.Core.Volumes.BackupLayout(_manifest.Current, paths.BackupRoot);   // swapped meanwhile (DEV reset): ensure on the live one
        _backupRoot = paths.BackupRoot;
        _configPaths = Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot); _configDir = paths.ConfigDir;
        RaiseRootPathChanged();
        if (bound.LivePrimary is { } primaryPath
            && !string.Equals(System.IO.Path.GetFullPath(primaryPath), System.IO.Path.GetFullPath(_backupRoot), StringComparison.OrdinalIgnoreCase))
        {
            Log($"Primary storage is {primaryPath} (the app's saved folder was {_backupRoot}); following the library.");
            await Storage.AdoptPrimaryPathAsync(primaryPath);
        }
        if (primaryAdded) await _manifest.SaveAsync();   // the manifest is otherwise unchanged; the launch reconcile saves the rest
        // The manifest is already in memory (OpenStoreAsync): build the rows from it, no re-parse of the file.
        await Task.Run(Storage.PreseedDriveSamples);   // the chosen root is new to the sample cache; probe it off-thread
        RefreshFromManifest();
        Log(Library.TotalCount > 0 ? "" : "No library yet. Scan to see what you own.");
        _ = RepairMisnamedFilesAsync();       // 1060-1083 display-name saves: rename in place, background, converges to 0
        OnPropertyChanged(nameof(CurrentRootText));
        await Accounts.RefreshAccountsAsync();   // 
        // Rebuild the folder rows now that the root exists: EnsureServicesAsync built them with an empty
        // _backupRoot, so every row carries StatusKnown=false until rebuilt here (RaiseDashboard does it).
        RaiseDashboard();
        Storage.CheckLayoutMigration();
        Storage.CheckReorgResume();   // surface an interrupted reorganize (Resume default)

        // A non-empty, non-paused queue = interrupted work; drain it on launch so queued files download and
        // the Pause/Stop controls stay truthful. A paused queue stays paused (its controls show Resume).
        if (IsConnected
            && !_manifest.Current.Downloads.IsEmpty && !_manifest.Current.Downloads.Paused)
            _ = DownloadFiltered(false, fromQueue: true);
        _ = RunLaunchChecksAsync();

        if (_settings.CheckGrogUpdatesOnLaunch) _ = Settings.CheckGrogUpdateAsync();
    }

    private async Task RunLaunchChecksAsync()
    {
        await _initialized.Task;
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                await StartupExistenceCheckAsync();
                // Auto-check for updates on launch (skip if a sync completed very recently, e.g. onboarding).
                var lastSync = _manifest?.Current.LastSyncCompleted;
                var syncedRecently = lastSync is { } ls && (DateTimeOffset.Now - ls) < TimeSpan.FromMinutes(5);
                if (_settings.CheckForUpdatesOnLaunch && IsConnected && Library.TotalCount > 0 && !syncedRecently)
                    await CheckForUpdates();
            }
            catch (Exception ex) { Log($"Launch check stopped: {Grog.Core.Api.GogError.Describe(ex)}", isError: true); }
        });
    }

    private int _repairRunning;
    private async Task RepairMisnamedFilesAsync()
    {
        if (_manifest is null || _api is null || !IsConnected) return;
        if (Grog.Core.Download.MisnamedFileRepair.FindCandidates(_manifest.Current).Count == 0) return;
        if (Interlocked.Exchange(ref _repairRunning, 1) == 1) return;
        try
        {
            var api = _api; var m = _manifest; var root = _backupRoot;
            // The BackupLayout ctor (disk) is built inside the pool task, not before it.
            int n = await Task.Run(() =>
            {
                var layout = new Grog.Core.Volumes.BackupLayout(m.Current, root);
                return Grog.Core.Download.MisnamedFileRepair.RepairAsync(m.Current, layout,
                    async (f, ct) => (await api.ResolveDownloadAsync(f.FileKey, ct)).FileName,
                    msg => Log(msg, category: LogCategory.Verify), gate: m.Gate);   // 
            });

            if (n > 0)
            {
                await m.SaveAsync();
                await Dispatcher.UIThread.InvokeAsync(RefreshFromManifest);
                Log($"Renamed {n} backed-up file(s) to their real filenames (they had been saved under GOG's display labels).",
                    category: LogCategory.Verify);
            }
        }
        catch (Exception ex) { Log($"Filename repair stopped: {ex.Message}", isError: true); }
        finally { Interlocked.Exchange(ref _repairRunning, 0); }
    }

    private async Task StartupExistenceCheckAsync()
    {
        if (_manifest is null || Library.TotalCount == 0) return;
        Overview.SetDiskCheckRunning(true);
        try
        {
            var m = _manifest; var root = _backupRoot;
            var r = (await Task.Run(() => Grog.Core.Runs.VerifyRun.RunAsync(m, root, Grog.Core.Runs.VerifyRequest.LibrarySizeOnly(),
                new Grog.Core.Runs.NullBackupHost(), CancellationToken.None))).Verify;
            await ReconcileAndRefresh();
            if (r.Missing > 0)
            {
                Log($"Startup check: {r.Missing} backed-up file{(r.Missing == 1 ? " is" : "s are")} no longer on disk - marked missing.",
                    isError: true, category: LogCategory.General);
            }
        }
        catch { /* best-effort; never block or fail launch on the existence check */ }
        finally { Overview.SetDiskCheckRunning(false); }
    }

    internal bool IsHiddenGameId(long gogId)
        => Library?.ItemById(gogId) is { } item && Library.Hidden.IsHidden(item);

    internal void RefreshHiddenSurfaces()
    {
        Storage.RebuildInventory();
        Storage.RefreshBackupLocations(forceOtherStorage: true);
        CloudVm.RefreshHiddenView();
        Overview.RefreshQueueRowFacts();
        Overview.RaiseHealth();
    }

    internal void RefreshFromManifest()
    {
        Library.RebuildRows();
        RaiseDashboard();
        CloudVm?.RefreshFromManifest();
    }

    public bool WindowHidden { get; private set; }

    public void ReleaseArtForTray()
    {
        WindowHidden = true;
        Library.ReleaseArt();
    }

    public void ReloadArtAfterTray()
    {
        WindowHidden = false;
        Library.ReloadArt();
    }

    [RelayCommand]
    private async Task Sync()
    {
        if (Busy) return;
        if (!await ConfirmManualRescanAsync()) return;   // manual scan: ask about standing flags
        Library.NoteScanStarting();
        await RunBusy("Syncing library…", async () =>
        {
            Dispatcher.UIThread.Post(() => Library.BeginCatalog("Syncing library…"));
            var r = await RunFullScanAsync((svc, who) =>
                svc.Progress += p => Dispatcher.UIThread.Post(() =>
                    Library.UpdateCatalog(p.Completed, p.Total,
                        p.Total > 0
                            ? (who.Length > 0 ? $"Scanning {who}… {p.Completed}/{p.Total}" : $"Scanning library… {p.Completed}/{p.Total}")
                            : "Contacting GOG…")),
                _cts?.Token ?? default);
            Dispatcher.UIThread.Post(() => { Library.RebuildRows(); Library.EndCatalog(completed: Library.LastScanStoredAnything); });
            Log($"Sync complete: {r.NewGames} new games, {r.NewFiles} new files");
        });
        await Storage.RunPendingImportsAsync(interactive: true);
    }

    [RelayCommand]
    private async Task PrimaryAction()
    {
        if (!RequireAccount("scan your GOG library")) return;
        if (Library.TotalCount == 0) { await CheckForUpdates(); return; }  // "Get my library" = first sync
        if (Library.SelectedActionableCount > 0 && !Library.GuideWantsWholeLibrary) { await Library.SyncSelected(); return; }  // target the selection
        await SyncBackup();
    }

    [ObservableProperty] private bool _showConnectScrim;
    public string ConnectScrimText =>
        "Opening GOG's sign-in window - the first one after starting Grog can take several seconds. "
      + "You sign in on GOG's own page; your password never passes through Grog.";

    [RelayCommand]
    private void CancelConnect()
    {
        if (!Services.WebViewLoginProvider.TryCancelActiveLogin())
            ShowConnectScrim = false;
    }

    [RelayCommand]
    private void ReopenLogin() => _ = ReopenLoginAsync();

    private async Task ReopenLoginAsync()
    {
        Services.WebViewLoginProvider.TryCancelActiveLogin();
        // Let the canceled broker unwind and clear Busy before the new attempt; Connect() no-ops while Busy.
        for (int i = 0; i < 40 && Busy; i++) await Task.Delay(50);
        ShowConnectScrim = false;
        await ConnectCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    internal async Task Connect()
    {
        if (Busy) return;
        ShowConnectScrim = true;
        await Task.Delay(50);   // one frame: let the scrim PAINT before the WebView cold start can block the UI thread
        try { await ConnectCore(); } finally { ShowConnectScrim = false; }
    }

    private async Task ConnectCore()
    {
        Grog.Core.Platform.OsKeyring.ResetAvailabilityCache();
        _ = Accounts.RefreshTokenProtectionSnapshotAsync();   // the card follows the reset even if the login is canceled
        // Tokens live in %APPDATA%, independent of the backup folder, so connecting needs no location.
        await RunBusy("Connecting to GOG…", async () =>
        {
            try
            {
                await _auth.EnsureAuthenticatedAsync();   // opens the WebView login
                var user = await _api.GetUserInfoAsync();
                var expected = _manifest is null ? null
                    : new Grog.Core.Auth.AccountService(_manifest, Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot))   // 
                        .Accounts.FirstOrDefault();
                if (expected is not null && expected.Username.Length > 0
                    && !string.Equals(expected.Username, user.Username, StringComparison.OrdinalIgnoreCase))
                {
                    await _auth.SignOutAsync();   // wipe the mismatched token from this session's file
                    var expectedName = expected.Username;
                    var stillConnected = AnyAccountConnected();   // probed on this worker, not inside the UI invoke
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        IsConnected = stillConnected;
                        Accounts.RefreshAccounts();
                        Log($"account: {user.Username} - sign-in DISCARDED (this slot belongs to {expectedName}; use Add Another GOG Account)",
                            isError: true, category: LogCategory.Auth);
                        ShowToast($"That sign-in was {user.Username}, but this slot belongs to {expectedName}. It was discarded - use Add Another GOG Account for {user.Username}.", 2, "Accounts");
                    });
                    return;
                }

                if (!Grog.Core.Auth.FileTokenStore.LooksReadable(_interactiveTokenPath))
                {
                    await _auth.PersistSessionAsync();
                    Log("Sign-in token file was unreadable - rewritten from the live session", category: LogCategory.Auth);
                }
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    IsConnected = true;
                    await Accounts.RegisterPrimaryAsync(user.Username);   // real name on the Accounts screen, not "This account"
                    StatusText = $"Connected as {user.Username}.";
                    Accounts.RaiseTokenSafety();   // the save may have minted the keyring key just now (W2-b)
                    Log($"account: {user.Username} - signed in", category: LogCategory.Auth);
                    _ = RepairMisnamedFilesAsync();   // names can be resolved now
                });
            }
            catch (OperationCanceledException)
            {
                Log("Sign-in was canceled", category: LogCategory.Auth);
                return;
            }
            catch (Exception ex)
            {
                Log($"Sign-in failed: {Grog.Core.Api.GogError.Describe(ex)}", isError: true, category: LogCategory.Auth);
                return;
            }
        });

        RaiseAccountStatus();
    }

    private bool SyncedRecently => _manifest?.Current.LastSyncCompleted is { } ls
        && (DateTimeOffset.UtcNow - ls) < TimeSpan.FromMinutes(5);

    [RelayCommand]
    private async Task SyncBackup(bool skipCatalogRefresh = false) => await SyncBackupCore(skipCatalogRefresh, false);

    private async Task SyncBackupCore(bool skipCatalogRefresh, bool ignoreContentChips, bool unattended = false)
    {
        if (Busy) return;
        if (!RequireAccount("back up your library")) return;
        _runErrored = false;   // reset per backup; RunBusy's generic catch is the one thing that sets it
        _lastRunOutcome = null;
        if (!skipCatalogRefresh && !SyncedRecently)
        {
            if (!unattended && !await ConfirmManualRescanAsync()) return;
            Library.NoteScanStarting();
            await RunBusy("Backing up…", async () =>
            {
                Dispatcher.UIThread.Post(() => Library.BeginCatalog("Scanning GOG Library…"));
                var r = await RunFullScanAsync((svc, who) =>
                    svc.Progress += p => Dispatcher.UIThread.Post(() =>
                        Library.UpdateCatalog(p.Completed, p.Total,
                            p.Total > 0
                                ? (who.Length > 0 ? $"Scanning {who}… {p.Completed}/{p.Total}" : $"Scanning library… {p.Completed}/{p.Total}")
                                : "Contacting GOG…")),
                    _cts?.Token ?? default);
                Dispatcher.UIThread.Post(() => { Library.RebuildRows(); Library.NoteScanResult(r, userAsked: false); Library.EndCatalog(completed: Library.LastScanStoredAnything); });
            });
            await Storage.RunPendingImportsAsync(interactive: !unattended);
        }

        await DownloadFiltered(updatesOnly: false, ignoreContentChips: ignoreContentChips, unattended: unattended);
        if (_downloadRefused)
        {
            if (unattended) Schedule.NoteScheduledRunRefused(_downloadRefusedReason ?? "a run is already active",
                                                             transient: _downloadRefusedReason is not RefusedSignInNotReady);
            return;
        }
        Log(Library.HasPendingWork ? StatusText : "Backup is current. Everything is backed up.");
        if (!DownloadPaused && !_stopRequested)
        {
            var outcome = _runErrored ? Grog.Core.Scheduling.ScheduleRunOutcome.Failed
                        : _lastRunOutcome is { } ro && ro != Grog.Core.Scheduling.ScheduleRunOutcome.Canceled ? ro
                        : _runFailed > 0 ? Grog.Core.Scheduling.ScheduleRunOutcome.Partial
                        :                  Grog.Core.Scheduling.ScheduleRunOutcome.Completed;
            Schedule.RecordRunOutcome(outcome);
            Overview.MarkLastRunDirty();   // the run's journal is finished: read its outcome once
            _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.UIThread.Post(() => { Overview.MarkLastRunDirty(); Overview.RaiseHealth(); }));
            if (unattended && outcome is not Grog.Core.Scheduling.ScheduleRunOutcome.Failed)
                Schedule.NoteFirstUnattendedRun();   // composes from the run counters NOW; only its raise is deferred
        }
    }

    private bool _runErrored;
    private Grog.Core.Scheduling.ScheduleRunOutcome? _lastRunOutcome;

    private bool _downloadRefused;
    private string? _downloadRefusedReason;
    private const string RefusedSignInNotReady = "sign-in is not ready";
}

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

// Session bring-up and the run entry points: services, bind-to-root, the launch checks, connect / sign-in, the
// Sync and Back-up commands, plus the manifest-backed retention settings. (The scope itself moved to
// LibraryViewModel.Scope in S3.2.)
public partial class MainWindowViewModel
{
    /// <summary>The token file the interactive (WebView) session reads and writes: the first registered
    /// account's, or a scratch file until the first Connect learns who the user is.</summary>

    internal void RebindInteractiveSession(Grog.Core.Storage.GrogPaths paths)
        => Session.RebindInteractiveSession(new Services.WebViewLoginProvider(() => OwnerWindowResolver?.Invoke()), paths);   // (S1.1) moved to the session

    /// <summary>Create the manifest/token/auth/API services. Their state lives in %APPDATA% (config dir),
    /// independent of the backup folder, so this can run before a location is chosen -- letting the user
    /// connect + scan first. Does NOT set _backupRoot (HasLocation stays false until a folder is picked).</summary>
    private async Task EnsureServicesAsync()
    {
        if (_servicesReady) return;
        // Config paths only (%APPDATA%); ResolveConfigDir creates the folder, so even this resolve is disk work
        // and runs on the pool (UI-thread sweep 09-06 r2).
        await Session.OpenStoreAsync();   // (S1.1) paths, store, load, legacy account migration live in the session
        var paths = Session.ConfigPaths!;
        await Task.Run(Storage.PreseedDriveSamples);   // warm the drive samples off-thread before the UI builds storage rows (UI-thread sweep 09-06)
        // The interactive (WebView) session binds to the first registered account's token file; with no
        // accounts yet it writes to a scratch file that Connect moves into place once the username is known.
        RebindInteractiveSession(paths);
        // sessionTokenReadable = the bound session actually LOADED (decrypted), not merely that a file exists:
        // gating the startup verify on the file alone lets GogAuthService pop an unrequested login window.
        bool sessionTokenReadable;
        try { sessionTokenReadable = await _tokenStore.LoadAsync() is not null; }
        catch { sessionTokenReadable = false; }
        // Token probes off the dispatcher (UI-thread sweep 09-06 r2): the secondary-account check and the
        // "does the bound session's file exist" answer used by the log line below.
        var interactivePath = _interactiveTokenPath;
        var (anyConnected, interactiveExists) = await Task.Run(() =>
            (sessionTokenReadable || AnyAccountConnected(), System.IO.File.Exists(interactivePath)));
        IsConnected = sessionTokenReadable;
        if (!IsConnected) IsConnected = anyConnected;   // a signed-in secondary is a connection
        Library.AdoptManifestScope();   // scope lives in the manifest; seed it from legacy AppSettings once
        _servicesReady = true;
        Log("startup: services ready");
        // UI-thread watchdog: stalls land in the Activity log with the slow pass named (diagnostic, 09-04).
        Services.UiWatch.Start(msg => Log(msg, isError: true, category: LogCategory.General));
        Grog.Core.Manifest.ManifestGate.SlowWait = ms => Services.UiWatch.Note("waiting for the manifest gate", ms);
        // THE UI THREAD RUNS ONLY UI (owner 09-25): Core's heavy entry points report a call from it, once per entry point.
        Grog.Core.UiThreadGuard.IsUiThread = () => Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
        Grog.Core.UiThreadGuard.Violation = what => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            Log($"UI thread ran {what}. It must run off the UI thread; please report this line.", isError: true, category: LogCategory.General));
        GameRowViewModel.ArtDir = paths.ArtDir;
        // Build the folder list at startup: Overview is home and set directly on CurrentView, so Navigate
        // never runs to build it and the list would stay empty until the user navigated.
        Storage.RefreshBackupLocations();
        await Accounts.RefreshAccountsAsync();   // rows are built after the off-thread token probe (UI-thread sweep 09-06 r2)
        RaiseState();
        Schedule.StartScheduleTimer();   // begin honoring the automatic-backup schedule (if enabled)
        Settings.OnServicesReady();   // manifest-backed settings: migrate the legacy app values in, re-raise the bindings
        Schedule.RaiseScheduleBindings();   // the Settings scheduler bindings evaluated before the manifest loaded -- refresh them to the saved state
        Schedule.CheckOverdueBackupOnLaunch();   // a scheduled slot may have passed while the app was closed
        Storage.StartVolumeWatch();   // detect drives being plugged in / pulled out while the app is open
        // A saved token makes us optimistically connected; verify it is usable and fetch the name. Gated on
        // the bound session, not IsConnected: probing a token-less session pops the interactive login, and
        // login is user-triggered ONLY.
        if (sessionTokenReadable)
            _ = FetchAccountNameAsync();
        else if (interactiveExists)
            // An unreadable stored session gets one honest log line; the Accounts page is the fix.
            Log("Couldn't restore your session - the stored sign-in can't be read right now (keyring locked or unavailable). Sign in again from the Accounts page, or restart after unlocking the keyring.", isError: true);
        // State plaintext token storage in the log on every launch it is on.
        if (Grog.Core.Auth.FileTokenStore.PlainTextPreferred)
            Log("Sign-in tokens are stored UNENCRYPTED on this machine (scheduled-tasks opt-out; change it under Accounts > Sign-in Security)");
    }

    private async Task FetchAccountNameAsync()
    {
        // A token file makes IsConnected optimistically true, but the token may be expired. GetUserInfo
        // triggers a refresh if possible and throws if the session is truly dead -- so its outcome is
        // the honest connection signal. Correct the state either way.
        try
        {
            var user = await _api.GetUserInfoAsync();
            await Accounts.RegisterPrimaryAsync(user.Username);
        }
        catch
        {
            // The tokens.json session is dead, but a signed-in sibling still carries the app. The per-account
            // File.Exists runs on the pool before the post; config dir only (UI-thread sweep 09-06 r2).
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

    /// <summary>(Re)initialize against a chosen backup root: ensure services, set the root, create the
    /// primary root + scaffold folders, then reconcile.</summary>
    private async Task BindToRootAsync(string root)
    {
        await EnsureServicesAsync();
        // THE one intended GrogPaths.Resolve(root) with the legacy-state probe, plus the BackupLayout ctor
        // (ensures primary + scaffolds) and the primary-hint Directory.Exists: all disk, all on the pool, applied
        // on the UI thread after the await (UI-thread sweep 09-06 r2).
        var m = _manifest;
        // The one manifest STRUCTURE change a layout can make (first run: add the primary root) happens here,
        // on the UI thread, so no worker-built layout ever adds to Roots under the UI's enumeration.
        bool primaryAdded = m.Read(cur => cur.Roots.Count == 0 || cur.PrimaryRootId is null);   // the only case EnsurePrimary changes anything
        m.Mutate(cur => Grog.Core.Volumes.BackupLayout.EnsurePrimary(cur, Grog.Core.Storage.GrogPaths.ResolveNoProbe(root).BackupRoot));   // (manifest gate 09-08)

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
        // _backupRoot changed at runtime (first-run auto-apply / DEV reset / change-folder) -> notify the
        // computed HasLocation-family so bindings (e.g. the Folders work area) re-evaluate instead of staying
        // stale-false from before a root existed.
        RaiseRootPathChanged();
        // The manifest's primary root is the fact; the app's own setting FOLLOWS it. A CLI --primary, or a
        // promotion done elsewhere, lands here instead of being overridden by a stale ChosenRoot.
        if (bound.LivePrimary is { } primaryPath
            && !string.Equals(System.IO.Path.GetFullPath(primaryPath), System.IO.Path.GetFullPath(_backupRoot), StringComparison.OrdinalIgnoreCase))
        {
            Log($"Primary storage is {primaryPath} (the app's saved folder was {_backupRoot}); following the library.");
            await Storage.AdoptPrimaryPathAsync(primaryPath);
        }
        if (primaryAdded) await _manifest.SaveAsync();   // the manifest is otherwise unchanged; the launch reconcile saves the rest
        // The manifest is already in memory (OpenStoreAsync): build the rows from it, no re-parse of the file.
        await Task.Run(Storage.PreseedDriveSamples);   // the chosen root is new to the sample cache; probe it off-thread (UI-thread sweep 09-06)
        RefreshFromManifest();
        Log(Library.TotalCount > 0 ? "" : "No library yet. Scan to see what you own.");
        _ = RepairMisnamedFilesAsync();       // 1060-1083 display-name saves: rename in place, background, converges to 0
        OnPropertyChanged(nameof(CurrentRootText));
        await Accounts.RefreshAccountsAsync();   // (UI-thread sweep 09-06 r2)
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

        // The disk existence pass and the GOG update check run once the pane is showing, never behind Initializing.
        _ = RunLaunchChecksAsync();

        // Grog's OWN update check: one anonymous GitHub request, fire-and-forget, every failure silent.
        // Deliberately last and unawaited -- an update notice must never delay the app being usable.
        if (_settings.CheckGrogUpdatesOnLaunch) _ = Settings.CheckGrogUpdateAsync();
    }

    /// <summary>After the pane is visible: stat the backed-up files (Overview reads "Checking files on disk…"
    /// meanwhile), then the launch rescan of GOG through the ordinary Busy scan path. Both on the UI thread's
    /// own terms: the stat loop is pooled inside, the rescan may ask a question.</summary>
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

    /// <summary>Background repair of files saved under GOG's display label by the 1060-1083 builds (see
    /// MisnamedFileRepair). Needs a signed-in session for the real names; silently waits for the next launch
    /// or sign-in otherwise. One resolve round-trip per candidate, so it runs off the UI thread and reports once.</summary>
    private int _repairRunning;
    private async Task RepairMisnamedFilesAsync()
    {
        if (_manifest is null || _api is null || !IsConnected) return;
        if (Grog.Core.Download.MisnamedFileRepair.FindCandidates(_manifest.Current).Count == 0) return;
        if (Interlocked.Exchange(ref _repairRunning, 1) == 1) return;
        try
        {
            var api = _api; var m = _manifest; var root = _backupRoot;
            // The BackupLayout ctor (disk) is built inside the pool task, not before it (UI-thread sweep 09-06 r2).
            int n = await Task.Run(() =>
            {
                var layout = new Grog.Core.Volumes.BackupLayout(m.Current, root);
                return Grog.Core.Download.MisnamedFileRepair.RepairAsync(m.Current, layout,
                    async (f, ct) => (await api.ResolveDownloadAsync(f.FileKey, ct)).FileName,
                    msg => Log(msg, category: LogCategory.Verify), gate: m.Gate);   // (manifest gate 09-08)
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

    /// <summary>Startup existence pass: presence + size only (no hashing), so files deleted or moved outside
    /// Grog show as missing on launch. Skips offline roots and preserves already-Verified state.</summary>
    private async Task StartupExistenceCheckAsync()
    {
        if (_manifest is null || Library.TotalCount == 0) return;
        Overview.SetDiskCheckRunning(true);
        try
        {
            var m = _manifest; var root = _backupRoot;
            // The presence/size pass is a synchronous stat loop with nothing that yields; run it on the thread
            // pool so first paint is not starved. The BackupLayout ctor (disk) is built there too, not on the
            // dispatcher before the hop (UI-thread sweep 09-06 r2). The reconcile below touches
            // ObservableCollections and runs back on the UI thread after the stat pass.
            // (S2.2) Core's VerifyRun, silent host: the startup line below is the only thing this pass says.
            var r = (await Task.Run(() => Grog.Core.Runs.VerifyRun.RunAsync(m, root, Grog.Core.Runs.VerifyRequest.LibrarySizeOnly(),
                new Grog.Core.Runs.NullBackupHost(), CancellationToken.None))).Verify;
            await ReconcileAndRefresh();
            if (r.Missing > 0)
            {
                // Missing files surface as a Missing dot on affected rows and a Log/Errors entry with Retry;
                // the log records what HAPPENED, the row status states what IS.
                Log($"Startup check: {r.Missing} backed-up file{(r.Missing == 1 ? " is" : "s are")} no longer on disk - marked missing.",
                    isError: true, category: LogCategory.General);
            }
        }
        catch { /* best-effort; never block or fail launch on the existence check */ }
        finally { Overview.SetDiskCheckRunning(false); }
    }

    /// <summary>The one recompute-and-notify path: every state-changing mutation ends here -- rebuild rows +
    /// domain-derived stats, then fire grouped notifications.</summary>
    /// <summary>(09-19) THE "is this game hidden" test for every list outside the Library grid: the Library's own
    /// HiddenPolicy (mature toggle + per-game picks), by id.</summary>
    internal bool IsHiddenGameId(long gogId)
        => Library?.ItemById(gogId) is { } item && Library.Hidden.IsHidden(item);

    /// <summary>(09-19) A hide/unhide or the mature toggle changed: every surface that names games follows at once
    /// -- Storage lists, Other storage, Cloud Saves, the Downloading/Moving rows, Files &amp; issues, the rail.</summary>
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
        // The Cloud Saves rail readout is built from the manifest, not a network discovery, so it fills
        // here without the user ever visiting the page.
        CloudVm?.RefreshFromManifest();
    }

    /// <summary>Called when Grog hides to the tray: free decoded cover art (and the detail banner) so the
    /// heap shrinks, not just the working set. Covers re-decode lazily -- and only for VISIBLE rows, thanks to
    /// grid virtualization -- when the window is shown again.</summary>
    /// <summary>True while the window is hidden in the tray (set by the view's tray hooks). Read by anything
    /// that would otherwise wait on a click nobody can make - the schedule countdown, for one.</summary>
    public bool WindowHidden { get; private set; }

    public void ReleaseArtForTray()
    {
        WindowHidden = true;
        Library.ReleaseArt();
    }

    /// <summary>Re-arm lazy cover decoding after a tray restore; virtualization means only on-screen rows
    /// actually re-decode.</summary>
    public void ReloadArtAfterTray()
    {
        WindowHidden = false;
        Library.ReloadArt();
    }

    [RelayCommand]
    private async Task Sync()
    {
        if (Busy) return;
        if (!await ConfirmManualRescanAsync()) return;   // (New items 09-09) manual scan: ask about standing flags
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

    /// <summary>The single primary button. Does the right thing for the current state:
    /// connect -> get library -> sync backup. One button, one obvious action, always.</summary>
    [RelayCommand]
    private async Task PrimaryAction()
    {
        // NOT a silent inline sign-in. Signing in from wherever you happen to be standing teaches nothing
        // about where accounts are managed; the gate names what needs an account and offers the page.
        if (!RequireAccount("scan your GOG library")) return;
        if (Library.TotalCount == 0) { await CheckForUpdates(); return; }  // "Get my library" = first sync
        if (Library.SelectedActionableCount > 0 && !Library.GuideWantsWholeLibrary) { await Library.SyncSelected(); return; }  // target the selection
        await SyncBackup();
    }

    /// <summary>Scrim shown from the Connect/Add-account CLICK until the login flow ends: the WebView runtime
    /// can take ~10 seconds to cold-start before its window exists, so the scrim names the wait instantly.</summary>
    [ObservableProperty] private bool _showConnectScrim;
    public string ConnectScrimText =>
        "Opening GOG's sign-in window - the first one after starting Grog can take several seconds. "
      + "You sign in on GOG's own page; your password never passes through Grog.";

    /// <summary>The connect scrim's Cancel: closes the in-flight native login dialog, which resolves the
    /// broker as canceled and lets the normal "Sign-in was canceled" path clear the scrim. If no dialog
    /// is live (wedged before it opened, or it died with the compositor), drop the scrim directly --
    /// the button must ALWAYS free the user (Linux walk 2026-08-23, N2).</summary>
    [RelayCommand]
    private void CancelConnect()
    {
        if (!Services.WebViewLoginProvider.TryCancelActiveLogin())
            ShowConnectScrim = false;
    }

    /// <summary>The login window is open but LOST (minimized away, behind everything, on another desktop).
    /// NativeWebDialog exposes no Activate/BringToFront -- probed against the assembly, it offers only
    /// Close/Move/Resize/Navigate/Refresh -- so raising it is impossible and a fresh one is the honest fix:
    /// close the stranded dialog, let the broker settle as canceled, then start the login again.</summary>
    /// <summary>Deliberately a SYNC command that starts the work: an async [RelayCommand] disables itself
    /// for the lifetime of the task, and this one's task lives until the login finishes -- so the button
    /// grayed out exactly when a user who lost the window a second time needs it again.</summary>
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

    /// <summary>Connect a GOG account via the in-app WebView login, then load the library.</summary>
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
        // A user action gets a FRESH chance at the keyring, including re-raising a canceled unlock prompt
        // that the failure cache would otherwise suppress.
        Grog.Core.Platform.OsKeyring.ResetAvailabilityCache();
        _ = Accounts.RefreshTokenProtectionSnapshotAsync();   // the card follows the reset even if the login is canceled
        // Tokens live in %APPDATA%, independent of the backup folder, so connecting needs no location.
        await RunBusy("Connecting to GOG…", async () =>
        {
            try
            {
                await _auth.EnsureAuthenticatedAsync();   // opens the WebView login
                var user = await _api.GetUserInfoAsync();
                // IDENTITY MATCH. The WebView cookie pre-fills the LAST GOG identity, so this path can
                // authenticate a DIFFERENT user than the one this slot is registered to. The token is already
                // on disk (EnsureAuthenticated wrote it), so a mismatch DISCARDS it and says who to use
                // instead; it never silently adopts. Symmetric with Add-account's duplicate check.
                var expected = _manifest is null ? null
                    : new Grog.Core.Auth.AccountService(_manifest, Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot))   // (UI-thread sweep 09-06 r2)
                        .Accounts.FirstOrDefault();
                if (expected is not null && expected.Username.Length > 0
                    && !string.Equals(expected.Username, user.Username, StringComparison.OrdinalIgnoreCase))
                {
                    await _auth.SignOutAsync();   // wipe the mismatched token from this session's file
                    var expectedName = expected.Username;
                    var stillConnected = AnyAccountConnected();   // probed on this worker, not inside the UI invoke (UI-thread sweep 09-06 r2)
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
                // This lambda is on RunBusy's background thread; the UI tail must run on the UI thread.

                // A valid IN-MEMORY session skips the login AND the save, so an orphaned token file
                // (keyring key lost while the app ran) would survive this "successful" connect and the
                // next restart is a silent logout (Linux walk 2026-08-23, B1 primary-slot variant).
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
        // Connecting does NOT fetch the library: scanning is the user's call, offered as "Scan My Library".

        // Update the setup-bar prerequisites (advanced/exited path).
        RaiseAccountStatus();
    }

    /// <summary>True if the catalog was refreshed within the last few minutes (a manual Rescan, the launch
    /// check, or onboarding). Lets the backup action skip a redundant "checking for updates" scan.</summary>
    private bool SyncedRecently => _manifest?.Current.LastSyncCompleted is { } ls
        && (DateTimeOffset.UtcNow - ls) < TimeSpan.FromMinutes(5);

    [RelayCommand]
    private async Task SyncBackup(bool skipCatalogRefresh = false) => await SyncBackupCore(skipCatalogRefresh, false);

    /// <summary>The real implementation. `ignoreContentChips` lets Status back up the WHOLE library rather
    /// than whatever Library happens to be filtered to.</summary>
    private async Task SyncBackupCore(bool skipCatalogRefresh, bool ignoreContentChips, bool unattended = false)
    {
        if (Busy) return;
        if (!RequireAccount("back up your library")) return;
        _runErrored = false;   // reset per backup; RunBusy's generic catch is the one thing that sets it
        _lastRunOutcome = null;
        // Phase 1: refresh catalog (the "manifest" is never named to the user). Skipped when the catalog
        // was refreshed in the last few minutes -- avoids a redundant scan before download.
        if (!skipCatalogRefresh && !SyncedRecently)
        {
            // (New items 09-09) Only a run a person started asks; the scheduled path passes unattended and
            // scans straight through, clearing nothing.
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
                // RECORDED, NOT SHOWN: this refresh also runs on the 03:00 scheduled backup, where a modal
                // would sit until morning. Counts and read failures are captured for the next open.
                Dispatcher.UIThread.Post(() => { Library.RebuildRows(); Library.NoteScanResult(r, userAsked: false); Library.EndCatalog(completed: Library.LastScanStoredAnything); });
            });
            // Storage added before this scan owes an import. Between the phases, never alongside phase 2: the
            // import and the run both plan placement and save the manifest (review 09-06). Unattended = no modal.
            await Storage.RunPendingImportsAsync(interactive: !unattended);
        }
        // Phase 2: download all gaps.
        await DownloadFiltered(updatesOnly: false, ignoreContentChips: ignoreContentChips, unattended: unattended);
        // (09-26) Refused because a run was already live: this backup did nothing, so it records nothing
        // (no "Backup is current", no last-backup stamp for the overdue check).
        if (_downloadRefused)
        {
            // A scheduled run refused for a passing reason keeps its slot and retries quietly; one refused for
            // sign-in settles the slot, so no retry pops the login window.
            if (unattended) Schedule.NoteScheduledRunRefused(_downloadRefusedReason ?? "a run is already active",
                                                             transient: _downloadRefusedReason is not RefusedSignInNotReady);
            return;
        }
        Log(Library.HasPendingWork ? StatusText : "Backup is current. Everything is backed up.");
        // The single "last backup" timestamp the overdue-on-launch check compares against; both manual and
        // scheduled backups flow through here. Paused is not an ENDING and stamps nothing.
        if (!DownloadPaused && !_stopRequested)
        {
            // The run's own outcome, the same one its journal carries: the App counted only 3-strike files, so one
            // network strike read "Completed" here beside "partial" in the journal (QA 09-30 C4).
            var outcome = _runErrored ? Grog.Core.Scheduling.ScheduleRunOutcome.Failed
                        : _lastRunOutcome is { } ro && ro != Grog.Core.Scheduling.ScheduleRunOutcome.Canceled ? ro
                        : _runFailed > 0 ? Grog.Core.Scheduling.ScheduleRunOutcome.Partial
                        :                  Grog.Core.Scheduling.ScheduleRunOutcome.Completed;
            Schedule.RecordRunOutcome(outcome);
            Overview.MarkLastRunDirty();   // the run's journal is finished: read its outcome once
            // (09-22) The journal's summary can still be catching up when that first read lands (the Health card
            // said "3 files failed" over a run that failed 6): read it again once it has settled.
            _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.UIThread.Post(() => { Overview.MarkLastRunDirty(); Overview.RaiseHealth(); }));
            // The FIRST unattended run that finished is the one nobody watched: pin its account.
            if (unattended && outcome is not Grog.Core.Scheduling.ScheduleRunOutcome.Failed)
                Schedule.NoteFirstUnattendedRun();   // composes from the run counters NOW; only its raise is deferred
        }
    }

    /// <summary>Set by <see cref="RunBusy"/>'s generic catch -- the one place a run's own exception lands.
    /// Read (and reset) only by SyncBackupCore, which owns the outcome stamp.</summary>
    private bool _runErrored;
    private Grog.Core.Scheduling.ScheduleRunOutcome? _lastRunOutcome;

    /// <summary>(09-26) The last DownloadFiltered did not start because a run was already live.</summary>
    private bool _downloadRefused;
    /// <summary>Why the last DownloadFiltered was refused, worded for the "Scheduled backup waits" line.</summary>
    private string? _downloadRefusedReason;
    /// <summary>The one refusal a retry cannot clear on its own.</summary>
    private const string RefusedSignInNotReady = "sign-in is not ready";
}

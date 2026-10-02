// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.App.Runtime;

namespace Grog.App.ViewModels;

/// <summary>
/// The Accounts page's view model (S3.4, 09-08): the account cards, add / log in / log out / remove, the
/// primary registration, the SIGN-IN SECURITY card (token protection snapshot, plaintext opt-in), the rail's
/// account dots and the connected-owner answer the library rows read. The interactive sign-in itself
/// (Connect / ConnectCore, the scrim, RequireAccount, IsConnected) stays on the shell as session
/// orchestration and is reached through <c>_root</c>.
/// </summary>
public sealed partial class AccountsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _root;
    private readonly GrogSession _session;

    public AccountsViewModel(MainWindowViewModel root, GrogSession session)
    {
        _root = root;
        _session = session;
    }

    // ---- Session / sibling shorthands (same names as the root's, so the moved code reads unchanged) ----
    private LibraryViewModel _library => _root.Library;
    private JsonManifestStore _manifest => _session.Manifest;
    private string _backupRoot => _session.BackupRoot;
    private Grog.Core.Storage.GrogPaths? _configPaths { get => _session.ConfigPaths; set => _session.ConfigPaths = value; }
    private AppSettings _settings => _session.Settings;
    private System.Net.Http.HttpClient _apiHttp => _session.ApiHttp;
    private GogApiClient _api => _session.Api;
    private GogAuthService _auth => _session.Auth;
    private string _interactiveTokenPath => _session.InteractiveTokenPath;
    private DownloadEngine? _liveEngine => _session.LiveEngine;
    private bool IsConnected => _root.IsConnected;
    private bool Busy => _root.Busy;
    private void Log(string message, bool isError = false, LogCategory category = LogCategory.General) => _root.Log(message, isError, category);

    /// <summary>The add slot's label and tip read Accounts.Count and IsConnected; the shell raises them with
    /// its page-visibility set.</summary>
    internal void RaiseAddSlot() { OnPropertyChanged(nameof(AddAccountSlotText)); OnPropertyChanged(nameof(AddAccountSlotTip)); }

    // ---- Account panel ----
    public ObservableCollection<AccountRow> Accounts { get; } = new();

    /// <summary>Rebuild the Accounts panel rows from the manifest. Fire-and-forget wrapper over
    /// <see cref="RefreshAccountsAsync"/> for the many call sites that need no completion; the async body
    /// carries its own try/catch (UI-thread sweep 09-06 r2).</summary>
    internal void RefreshAccounts() => _ = RefreshAccountsAsync();

    /// <summary>Generation stamp: a refresh that finishes after a newer one started applies nothing.</summary>
    private int _refreshAccountsGen;

    /// <summary>Rebuild the Accounts panel rows from the manifest. The per-account token probe (stat + open +
    /// read and, for a keyring-sealed token, a keyring call) and the keyring protection readout run on the
    /// thread pool; the bound ObservableCollection is only touched on the UI thread, after the await, and only
    /// when this is still the newest refresh against the same manifest (UI-thread sweep 09-06 r2).</summary>
    internal async Task RefreshAccountsAsync()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { await Dispatcher.UIThread.InvokeAsync(async () => await RefreshAccountsAsync()); return; }
        try
        {
            int gen = ++_refreshAccountsGen;
            _root.RebuildMaskPairs();   // log masking reads an immutable snapshot; this is its only writer
            // The rail footer derives from the same account list; every path that changes accounts funnels
            // through here, so this is the one raise that keeps "Accounts (N)" from going stale.
            _root.RaiseAccountStatus();
            var m = _manifest; var root = _backupRoot;
            if (m is null) { Accounts.Clear(); return; }

            // Snapshot the ids on the UI thread (the manifest list is mutated here), probe them off it.
            var ids = m.Current.Accounts.Select(a => a.Id).ToList();
            var probe = await Task.Run(() =>
            {
                Grog.Core.Auth.FileTokenStore.ForgetReadable();   // the cards re-read for real; the rail dots follow
                var svc = new Grog.Core.Auth.AccountService(m, Grog.Core.Storage.GrogPaths.ResolveNoProbe(root));
                var connected = new Dictionary<string, bool>();
                foreach (var id in ids)
                    // A row is "signed in" iff its OWN token file exists AND is readable right now --
                    // under a locked keyring the file exists but cannot decrypt. Uncached: this is the page's
                    // own refresh, and the cache was just cleared for it.
                    connected[id] = Grog.Core.Auth.FileTokenStore.LooksReadable(svc.TokenPathFor(id));
                return (Connected: connected,
                        Protection: Grog.Core.Auth.FileTokenStore.Protection,
                        Reason: Grog.Core.Auth.FileTokenStore.ProtectionReason);
            });
            if (gen != _refreshAccountsGen || !ReferenceEquals(m, _manifest)) return;   // superseded
            ApplyTokenProtectionSnapshot(probe.Protection, probe.Reason);

            Accounts.Clear();
            var svcUi = new Grog.Core.Auth.AccountService(m, Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot));
            // Seed the rail dots' remembered answers with this fresh probe so they never lag the cards.
            foreach (var kv in probe.Connected) _railTokenReadable[svcUi.TokenPathFor(kv.Key)] = kv.Value;
            _railTokenProbedAt = Environment.TickCount64;
            // The badge registry may not exist yet on this path; the cards read it, so keep it in step.
            _library.RebuildOwnerBadges(svcUi.Accounts
                .Select(a => (a.Id, string.IsNullOrWhiteSpace(a.Username) ? a.Id : a.Username)).ToList());
            var counts = svcUi.ItemCountsByAccount();
            // (UX 09-08 #1) Row subline stats from data already in the manifest: games per owner with any local
            // copy, and the last completed library check. Same UI-thread manifest read ItemCountsByAccount does.
            var backedUp = BackedUpGamesByOwner(m.Current);
            var lastChecked = m.Current.LastSyncCompleted is { } lsc ? Grog.Core.Format.RelativeTime.Ago(lsc) : null;
            if (svcUi.Accounts.Count == 0)
            {
                // The placeholder exists for ONE state: signed in but not yet registered. Signed out with zero
                // registrations must show the empty page with its Connect slot, never a ghost row.
                if (_library.TotalCount > 0 && IsConnected)
                    Accounts.Add(new AccountRow("", "This account", _library.TotalCount, true, true,
                        BacksInteractiveSession: true,   // the placeholder IS the live _auth session
                        BackedUpGames: backedUp.Values.Sum(), LastChecked: lastChecked));
                LogAccountRows(0);
                OnPropertyChanged(nameof(AddAccountSlotText)); OnPropertyChanged(nameof(AddAccountSlotTip));
                return;
            }
            foreach (var a in svcUi.Accounts)
                Accounts.Add(new AccountRow(a.Id,
                    string.IsNullOrEmpty(a.Username) ? a.Id : a.Username,
                    counts.TryGetValue(a.Id, out var n) ? n : 0, true,
                    // An account registered between the snapshot and now is probed cached (5 s) on this thread;
                    // the next refresh (which every registration triggers) reads it for real.
                    Connected: probe.Connected.TryGetValue(a.Id, out var ok)
                        ? ok : Grog.Core.Auth.FileTokenStore.LooksReadableCached(svcUi.TokenPathFor(a.Id)),
                    // The FIRST account's token file backs the in-memory _auth session (RebindInteractiveSession):
                    // its log-out/remove must also drop the live session. The old predicate (Id=="") went dead
                    // when the unnamed slot was migrated away, silently unrouting that cleanup.
                    BacksInteractiveSession: a == svcUi.Accounts[0],
                    BackedUpGames: backedUp.TryGetValue(a.Id, out var bu) ? bu : 0, LastChecked: lastChecked));
            LogAccountRows(svcUi.Accounts.Count);
            RebuildRailAccountDots();
            OnPropertyChanged(nameof(AddAccountSlotText)); OnPropertyChanged(nameof(AddAccountSlotTip));   // read Accounts.Count, which just changed

            // Self-heal duplicate registrations, then rebuild the rows so the panel reflects the deduped state.
            if (svcUi.Accounts.Count > 1) _ = DedupeAccountsAsync(svcUi);
        }
        catch (Exception ex) { Log($"Accounts panel refresh failed: {ex.Message}", isError: true); }
    }

    /// <summary>(UX 09-08 #1) Per-owner count of GAMES (not movies) with at least one file on disk. Owners-based
    /// like ItemCountsByAccount: a co-owned game counts for every owner.</summary>
    private static Dictionary<string, int> BackedUpGamesByOwner(Grog.Core.Manifest.LibraryManifest manifest)
    {
        var counts = new Dictionary<string, int>();
        foreach (var item in manifest.Items)
        {
            if (item.IsMovie) continue;
            if (!item.Files.Any(f => f.State is Grog.Core.Models.FileState.Present or Grog.Core.Models.FileState.Verified
                                             or Grog.Core.Models.FileState.Outdated)) continue;
            foreach (var owner in item.OwnerIds)
                counts[owner] = counts.TryGetValue(owner, out var n) ? n + 1 : 1;
        }
        return counts;
    }

    private async Task DedupeAccountsAsync(Grog.Core.Auth.AccountService svc)
    {
        try
        {
            if (await svc.DedupeAsync() > 0) Dispatcher.UIThread.Post(RefreshAccounts);
        }
        catch { /* healing is best-effort; it must never break the panel */ }
    }

    /// <summary>Diagnostic: a log line naming manifest count vs panel rows pins the layer at fault.</summary>
    private void LogAccountRows(int manifestCount)
    {
        // Mismatch-only: a healthy line every rebuild wallpapers the log; the job is the exception.
        if (Accounts.Count != manifestCount && !(manifestCount == 0 && Accounts.Count <= 1))
            Log($"Accounts panel MISMATCH: manifest has {manifestCount}, panel built {Accounts.Count} row(s).",
                isError: true);
    }

    /// <summary>Register the FIRST account once its username is known (the id derives from it), and move the
    /// scratch token into its own tokens-&lt;id&gt;.json; the session is then per-account.</summary>
    internal async Task RegisterPrimaryAsync(string username)
    {
        if (_manifest is null || string.IsNullOrWhiteSpace(username)) return;
        try
        {
            // (UI-thread sweep 09-06) config-dir resolve (probe + legacy migration) off the dispatcher.
            var root = _backupRoot;
            var paths = await Task.Run(() => Grog.Core.Storage.GrogPaths.Resolve(root));
            var svc = new Grog.Core.Auth.AccountService(_manifest, paths);
            if (svc.Accounts.Count == 0)
            {
                var acct = await svc.AddOrUpdateAsync("", username, username);
                var home = svc.TokenPathFor(acct.Id);
                var scratch = _interactiveTokenPath;
                // (UI-thread sweep 09-06) the token file's exists/copy/delete run off the dispatcher; same order as before.
                bool moved = !string.Equals(scratch, home, StringComparison.OrdinalIgnoreCase)
                    && await Task.Run(() =>
                    {
                        if (!System.IO.File.Exists(scratch)) return false;
                        System.IO.File.Copy(scratch, home, overwrite: true);
                        Grog.Core.Auth.FileTokenStore.ForgetReadable();
                        try { System.IO.File.Delete(scratch); } catch { }
                        return true;
                    });
                if (moved) _root.RebindInteractiveSession(paths);
            }
            RefreshAccounts();
            _root.RaiseAccountStatus();
            // Row flags derive from the REGISTERED account set at build time, so a registration
            // change must rebuild them -- reconnecting the last account left every "(account
            // removed)" caption standing until the next rescan/restart (owner 08-31).
            _root.RefreshFromManifest();
        }
        catch { /* best-effort naming */ }
    }

    /// <summary>For a primary with no registration row yet: fetch the username once (on the Accounts tab)
    /// and register it, replacing the "This account" placeholder.</summary>
    internal async Task EnsurePrimaryRegisteredAsync()
    {
        if (_manifest is null || !IsConnected || _api is null) return;
        try
        {
            // Inside the try and config-dir only (UI-thread sweep 09-06 r2).
            var svc = new Grog.Core.Auth.AccountService(_manifest, Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot));
            if (svc.Accounts.Count > 0) return;
            var user = await _api.GetUserInfoAsync(); await RegisterPrimaryAsync(user.Username);
        }
        catch { /* offline: keep the placeholder for now */ }
    }

    // ---- Remove account: two-step (scrim confirm), allowed on EVERY account including the primary ----
    // The confirm also names the library consequence: items are owner-refcounted (per-file OwnerIds),
    // so removing an account decrements every stamp and games left with NO owner leave the library
    // (their bytes stay -- untrack, never delete). "Don't show this again" skips the dialog entirely.
    [ObservableProperty] private bool _showRemoveAccountConfirm;
    [ObservableProperty] private bool _dontAskRemoveAccountAgain;
    /// <summary>(09-19, owner) The OPT-IN half of Remove account: also untrack the games only this account owns.
    /// Off by default and reset on every open: removing an account wipes its login and stops syncing it, and
    /// changes NOTHING else. Before, removal always untracked them -- with one account that emptied the library,
    /// the queue and the Storage page (owner-hit on the release walk), and the rescan that "brings them back"
    /// returned games with no file records.</summary>
    [ObservableProperty] private bool _alsoRemoveAccountGames;
    partial void OnAlsoRemoveAccountGamesChanged(bool value) => OnPropertyChanged(nameof(RemoveAccountWarningText));
    public string AlsoRemoveGamesLabel
    {
        get
        {
            int sole = _pendingRemoveRow is null || _manifest is null
                ? 0 : Grog.Core.Sync.LibraryUntrack.SoleOwnedBy(_manifest.Current, _pendingRemoveRow.Id).Count;
            return $"Also remove the {sole} game{(sole == 1 ? "" : "s")} only this account owns from the library (files stay on disk)";
        }
    }
    public bool CanAlsoRemoveGames => _pendingRemoveRow is not null && _manifest is not null
        && Grog.Core.Sync.LibraryUntrack.SoleOwnedBy(_manifest.Current, _pendingRemoveRow.Id).Count > 0;
    private AccountRow? _pendingRemoveRow;
    /// <summary>Names what stays (the never-delete rule) and what leaves: games owned ONLY by this
    /// account come off the library grid with it. Downloaded bytes always stay.</summary>
    public string RemoveAccountWarningText
    {
        get
        {
            var name = _pendingRemoveRow?.Name ?? "this account";
            var text = $"{name} stops syncing and its saved login is wiped. Your library, your queue and your "
                     + "downloaded files stay exactly as they are; downloads that need this account wait until it is signed in again.";
            if (AlsoRemoveAccountGames)
                text += " With the box ticked, the games only this account owns leave the library and the queue. "
                      + "Their files stay on disk; adding the account back and re-adding the drive finds them again.";
            return text;
        }
    }

    [RelayCommand]
    private async Task RemoveAccount(AccountRow? row)
    {
        if (row is null || _manifest is null) return;
        if (_settings.SuppressRemoveAccountConfirm) { await RemoveAccountCore(row, alsoRemoveGames: false); return; }
        _pendingRemoveRow = row;
        DontAskRemoveAccountAgain = false;
        AlsoRemoveAccountGames = false;
        OnPropertyChanged(nameof(AlsoRemoveGamesLabel)); OnPropertyChanged(nameof(CanAlsoRemoveGames));
        OnPropertyChanged(nameof(RemoveAccountWarningText));
        ShowRemoveAccountConfirm = true;
    }

    [RelayCommand]
    private void CancelRemoveAccount() { ShowRemoveAccountConfirm = false; _pendingRemoveRow = null; DontAskRemoveAccountAgain = false; }

    [RelayCommand]
    private async Task ConfirmRemoveAccount()
    {
        ShowRemoveAccountConfirm = false;
        if (DontAskRemoveAccountAgain) { _settings.SuppressRemoveAccountConfirm = true; _settings.SaveSoon(); }
        DontAskRemoveAccountAgain = false;
        var row = _pendingRemoveRow; _pendingRemoveRow = null;
        bool alsoGames = AlsoRemoveAccountGames; AlsoRemoveAccountGames = false;
        if (row is null || _manifest is null) return;
        await RemoveAccountCore(row, alsoGames);
    }

    private async Task RemoveAccountCore(AccountRow row, bool alsoRemoveGames)
    {
        if (_manifest is null) return;
        try
        {
            var svc = new Grog.Core.Auth.AccountService(_manifest, Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot));   // (UI-thread sweep 09-06 r2)
            // The first account's file backs the in-memory _auth session: pause first (in-flight downloads
            // hold a token about to be wiped) and drop the live auth before removing. No promotion needed.
            if (row.BacksInteractiveSession)
            {
                try { _root.PauseDownloads(); } catch { }
                _root.StopSizing();
                try { await _auth.SignOutAsync(); } catch { }
            }
            // (09-19, owner) DEFAULT: the library is left alone. Files keep the removed account's owner stamp, so
            // their downloads wait ("owner signed out") and signing that account in again picks them straight up.
            // OPT-IN: owner decrement BEFORE RemoveAsync so its SaveAsync persists both in one write; sole-owned
            // items leave the library with the account (owner refcount hits zero); bytes stay on disk.
            var removedItems = alsoRemoveGames
                ? _manifest.Read(m => Grog.Core.Sync.LibraryUntrack.RemoveOwner(m, row.Id))   // (manifest gate 09-08)
                : new System.Collections.Generic.List<Grog.Core.Models.LibraryItem>();

            // The queue entries are purged; an in-flight transfer for a removed game must stop too.
            if (removedItems.Count > 0) _liveEngine?.CancelGames(removedItems.Select(i => i.GogId).ToHashSet());
            await svc.RemoveAsync(row.Id);
            // The interactive session was bound to the removed account's token file. Bind it to whoever is
            // first NOW (or the scratch file): left alone, Connect wrote the next login into a file no
            // registered account reads, and Api kept the wiped session (sweep 2 #5).
            if (row.BacksInteractiveSession) _root.RebindInteractiveSession(Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot));
            var connected = await _root.AnyAccountConnectedAsync();   // token probes off the dispatcher (UI-thread sweep 09-06 r2)
            await Dispatcher.UIThread.InvokeAsync(() => _root.IsConnected = connected);
            _root.RaiseBarState();
            RefreshAccounts();
            _root.RaiseAccountStatus();
            // Same-turn row rebuild: the orange "(account removed)" flags must appear NOW, not on
            // the next rescan/restart (see RegisterPrimaryAsync for the reconnect direction).
            _root.RefreshFromManifest();
            if (removedItems.Count > 0) _root.Storage.RefreshBackupLocations();   // queue changed: re-plan fits-nowhere
            var leftNote = removedItems.Count > 0 ? $", {removedItems.Count} game(s) left the library" : "";
            Log($"account: {row.Name} - removed (saved login wiped, library and backups kept{leftNote})", category: LogCategory.Auth);
        }
        catch (Exception ex) { Log($"Couldn't remove account: {ex.Message}", isError: true); }
    }

    /// <summary>Sign out of the primary GOG session: clears the stored token AND the in-memory auth, so the
    /// next Connect shows the login page. Downloaded backups on disk are untouched.</summary>
    [RelayCommand]
    private async Task Logout()
    {
        if (Busy) return;
        // Pause BEFORE signing out: in-flight downloads hold a token about to be revoked, and letting them
        // run records 401s as failures against files that are fine. Pausing keeps partials and the queue.
        try { _root.PauseDownloads(); } catch { }
        // No cookie cleanup needed: every login runs in a throwaway WebView session (see
        // WebViewLoginProvider), so no identity can pre-fill the next sign-in.
        _root.StopSizing();
        await _root.RunBusy("Signing out…", async () =>
        {
            try { await _auth.SignOutAsync(); } catch { }
            // Not hard false: a signed-in secondary still scans and downloads, so connected follows ALL accounts.
            await Dispatcher.UIThread.InvokeAsync(() => _root.IsConnected = _root.AnyAccountConnected());
        });
        RefreshAccounts();
        _root.RefreshFromManifest();   // "(account logged out)" captions key off the connected set
        _root.RaiseAccountStatus();
        _root.RaiseBarState();   // the primary action everywhere becomes "Connect Account"
        _root.StatusText = "Signed out. Your downloaded backups are kept.";
        Log($"account: {_root.AccountDisplayName("")} - signed out (downloaded backups kept)", category: LogCategory.Auth);
    }

    /// <summary>Log out of ONE account from its own card. Log Out ONLY forgets the stored token -- the
    /// registration, items and bytes stay; Remove is its own red button. Signed out it reads "Log In".</summary>
    [RelayCommand]
    private async Task LogoutAccount(AccountRow? row)
    {
        if (row is null || _manifest is null) return;
        if (row.BacksInteractiveSession)
        {
            // Route on the ROW's state, not global IsConnected: a signed-in sibling keeps the global true.
            // This row's file backs _auth, so Logout/Connect (which manage the live session and the
            // WebView cookie) are its verbs; the old Id=="" predicate went dead post-rename and every
            // row fell through to the Add flow, leaving _auth holding a signed-out account's session.
            if (row.Connected) await Logout(); else await _root.Connect();
            return;
        }
        if (!row.Connected) { await AddAccount(); return; }   // the button reads "Log In" then
        // The token-file delete runs off the dispatcher; config dir only (UI-thread sweep 09-06 r2).
        var m = _manifest; var root = _backupRoot; var id = row.Id;
        await Task.Run(() => new Grog.Core.Auth.AccountService(m, Grog.Core.Storage.GrogPaths.ResolveNoProbe(root)).LogOut(id));
        RefreshAccounts();
        _root.RefreshFromManifest();   // "(account logged out)" captions key off the connected set
        Log($"account: {row.Name} - logged out (the account and its backups stay)", category: LogCategory.Auth);
    }

    /// <summary>The Accounts page's one add control. With no accounts it MUST be the primary connect (owns
    /// tokens.json, registers Id ""); AddAccount is the second-account flow with its own token file.</summary>
    [RelayCommand]
    private async Task ConnectOrAddAccount()
    {
        if (_manifest is null) { await _root.Connect(); return; }
        var svc = new Grog.Core.Auth.AccountService(_manifest, Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot));   // (UI-thread sweep 09-06 r2)
        if (svc.Accounts.Count == 0 || !IsConnected) await _root.Connect();
        else await AddAccount();
    }

    /// <summary>The token-safety block exists only where the keyring story does: Windows DPAPI needs no
    /// explanation or escape hatch (it works in Task Scheduler), so the block hides there entirely.</summary>
    public bool ShowTokenSafetyOptions => !OperatingSystem.IsWindows();

    /// <summary>The SIGN-IN SECURITY card's state line, badge and body, derived from what THIS machine does;
    /// wording flips the instant the scheduled-tasks toggle changes.</summary>
    // FileTokenStore.Protection is a keyring probe on Linux/macOS; the bound getters read a snapshot taken on the
    // thread pool (RefreshAccounts, RaiseTokenSafety, the ResetAvailabilityCache sites) instead of probing on
    // every binding evaluation (UI-thread sweep 09-06 r2). Until the first snapshot lands the live value is read
    // once and remembered.
    private Grog.Core.Platform.TokenProtection? _tokenProtectionSnapshot;
    private Grog.Core.Platform.TokenProtectionReason? _tokenProtectionReasonSnapshot;

    /// <summary>The last-known token protection; the guide reads this rather than probing the keyring itself.</summary>
    // Never probe from a getter (final check 09-06): the Accounts page is in the tree at startup, and the
    // first binding read would spawn the keyring helper on the dispatcher. Until the first off-thread
    // snapshot lands the card reads as the honest fallback; the ctor kicks that probe at once.
    internal Grog.Core.Platform.TokenProtection TokenProtectionSnapshot
        => _tokenProtectionSnapshot ?? Grog.Core.Platform.TokenProtection.FilePermissionsOnly;
    private Grog.Core.Platform.TokenProtectionReason TokenProtectionReasonSnapshot
        => _tokenProtectionReasonSnapshot ?? Grog.Core.Platform.TokenProtectionReason.Encrypted;

    private void ApplyTokenProtectionSnapshot(Grog.Core.Platform.TokenProtection p, Grog.Core.Platform.TokenProtectionReason r)
    {
        bool changed = _tokenProtectionSnapshot != p || _tokenProtectionReasonSnapshot != r;
        _tokenProtectionSnapshot = p; _tokenProtectionReasonSnapshot = r;
        if (changed) RaiseTokenSafetyProperties();
    }

    /// <summary>Re-read the protection answer on the thread pool and apply it on the UI thread. Call after
    /// anything that can change it (a keyring reset, a token save).</summary>
    internal async Task RefreshTokenProtectionSnapshotAsync()
    {
        try
        {
            var snap = await Task.Run(() => (Grog.Core.Auth.FileTokenStore.Protection, Grog.Core.Auth.FileTokenStore.ProtectionReason));
            if (Dispatcher.UIThread.CheckAccess()) ApplyTokenProtectionSnapshot(snap.Item1, snap.Item2);
            else Dispatcher.UIThread.Post(() => ApplyTokenProtectionSnapshot(snap.Item1, snap.Item2));
        }
        catch { /* the previous snapshot stands */ }
    }

    public bool TokenSafetyIsFallback
        => TokenProtectionSnapshot == Grog.Core.Platform.TokenProtection.FilePermissionsOnly;
    public string TokenSafetyHeadline => TokenSafetyIsFallback
        ? "Readable by your user account only" : "Encrypted on your machine";
    public string TokenSafetyBadge => TokenSafetyIsFallback
        ? "FILE ONLY" : OperatingSystem.IsMacOS() ? "KEYCHAIN" : "SYSTEM KEYRING";
    public string TokenSafetyBody
    {
        get
        {
            var p = TokenProtectionSnapshot;
            // Each fallback CAUSE gets its own sentence: the opt-out was the user's choice, a missing
            // keyring has an install remedy, and a fresh machine just has not saved yet. The old single
            // sentence told the last two something false (Core TokenProtectionReason doc).
            return TokenProtectionReasonSnapshot switch
            {
                Grog.Core.Platform.TokenProtectionReason.OptedOut =>
                    "Grog keeps a refresh token, never your password. It is stored unencrypted so scheduled CLI runs can read it, protected by file permissions.",
                Grog.Core.Platform.TokenProtectionReason.KeyringUnavailable =>
                    "Grog keeps a refresh token, never your password. No system keyring is reachable, so the token is protected by file permissions only."
                    + (OperatingSystem.IsLinux() ? " Installing libsecret (sudo apt install libsecret-tools) lets Grog encrypt it." : ""),
                Grog.Core.Platform.TokenProtectionReason.KeyNotYetCreated =>
                    "Grog keeps a refresh token, never your password. Your system keyring is available; the token will be encrypted with it the next time you sign in.",
                _ => "Grog keeps a refresh token, never your password." + Grog.Core.Platform.OsKeyring.UnlockNote(p),
            };
        }
    }

    /// <summary>The scheduled-tasks opt-out (Core marker file, see FileTokenStore.PlainTextPreferred).
    /// Flipping it re-saves every token NOW: decryption needs the keyring, only reachable in the GUI.</summary>
    public bool PlainTextTokenEnabled
    {
        get => Grog.Core.Auth.FileTokenStore.PlainTextPreferred;
        set
        {
            if (value == Grog.Core.Auth.FileTokenStore.PlainTextPreferred) return;
            _ = ApplyPlainTextToken(value);
        }
    }

    private async Task ApplyPlainTextToken(bool on)
    {
        try
        {
            // The toggle is a user action: clear held keyring state so the re-save below can mint the key
            // (turning the opt-out OFF on a healthy keyring must produce GRGK1, not plaintext - Linux
            // walk 2026-08-23 R3, same held-state root as R1).
            Grog.Core.Platform.OsKeyring.ResetAvailabilityCache();
            // The whole read -> flip -> re-save sequence runs on the thread pool, in the same order as before:
            // every LoadAsync/SaveAsync is a token decrypt/encrypt through the keyring (UI-thread sweep 09-06 r2).
            var m = _manifest; var root = _backupRoot;
            var (skipped, protectionAfter) = await Task.Run(async () =>
            {
                // Read every stored session BEFORE flipping: reading GRGK1 needs the keyring either way,
                // but doing it first means a failure leaves the policy unchanged instead of half-applied.
                var paths = Grog.Core.Storage.GrogPaths.ResolveNoProbe(root);
                var stores = new List<Grog.Core.Auth.FileTokenStore>();
                var sessions = new List<Grog.Core.Auth.AuthSession?>();
                if (m is not null)
                {
                    var svc = new Grog.Core.Auth.AccountService(m, paths);
                    foreach (var a in svc.Accounts.ToList())
                    {
                        var store = new Grog.Core.Auth.FileTokenStore(svc.TokenPathFor(a.Id));
                        stores.Add(store);
                        sessions.Add(await store.LoadAsync());
                    }
                }
                Grog.Core.Auth.FileTokenStore.SetPlainTextPreferred(on);
                var skippedN = 0;
                for (var i = 0; i < stores.Count; i++)
                    if (sessions[i] is { } s) await stores[i].SaveAsync(s);
                    else skippedN++;
                return (skippedN, Grog.Core.Auth.FileTokenStore.Protection);
            });
            // An unreadable token (orphaned GRGK1, locked keyring) cannot be rewritten to the new policy:
            // silence here left the card claiming a state the disk did not have (Linux walk 2026-08-23, B2).
            if (skipped > 0)
                Log($"Token storage changed, but {skipped} stored sign-in(s) could not be read and were left as-is - log out and back in to rewrite them", isError: true);
            // The OFF log must not claim encryption the machine cannot deliver: with the marker gone but
            // the keyring unreachable, the re-save above still wrote plaintext.
            Log(on ? "Sign-in tokens are now stored readable by scheduled tasks (unencrypted, your user account only)"
                : protectionAfter == Grog.Core.Platform.TokenProtection.FilePermissionsOnly
                    ? "Keyring opt-out removed, but no keyring is reachable right now - tokens remain unencrypted until one is"
                    : "Sign-in tokens are now encrypted with the system keyring");
        }
        catch (Exception ex)
        {
            Log($"Couldn't change token storage: {Grog.Core.Api.GogError.Describe(ex)}", isError: true);
        }
        OnPropertyChanged(nameof(PlainTextTokenEnabled));
        // Snapshot first so the card and the guide detail read the post-flip answer (UI-thread sweep 09-06 r2).
        await RefreshTokenProtectionSnapshotAsync();
        RaiseTokenSafety();
        _root.Guide?.RaiseDetail();
    }

    /// <summary>Re-derives the SIGN-IN SECURITY card. Must run after ANY event that can change the
    /// protection answer mid-session -- a login save minting the keyring key is the one the card missed
    /// (Linux walk 2026-08-23, W2-b: card kept claiming "no keyring reachable" until restart).</summary>
    internal void RaiseTokenSafety()
    {
        RaiseTokenSafetyProperties();
        // The answer may have just changed (a mint, a reset): re-probe off the dispatcher and raise again if
        // it did (UI-thread sweep 09-06 r2).
        _ = RefreshTokenProtectionSnapshotAsync();
    }

    private void RaiseTokenSafetyProperties()
    {
        OnPropertyChanged(nameof(TokenSafetyIsFallback));
        OnPropertyChanged(nameof(TokenSafetyHeadline));
        OnPropertyChanged(nameof(TokenSafetyBadge));
        OnPropertyChanged(nameof(TokenSafetyBody));
        _root.Guide?.RaiseDetail();   // the guide's sign-in reassurance reads the same snapshot
    }

    /// <summary>Label and tooltip follow which flow the slot will actually run.</summary>
    public string AddAccountSlotText => IsConnected && Accounts.Count > 0
        ? "Add Another GOG Account" : "Connect Your GOG Account";
    public string AddAccountSlotTip => IsConnected && Accounts.Count > 0
        ? "Sign in to another GOG account; its list of games is combined with the existing _library."
        : "Sign in to GOG. A GOG login window opens; Grog never sees your password.";

    /// <summary>Add a SECOND GOG account: an isolated token store + non-persistent WebView give a fresh login,
    /// stored in its own tokens-&lt;id&gt;.json, then the library merges. Never touches _auth.</summary>
    private async Task AddAccount()
    {
        if (Busy || _manifest is null) return;
        if (!_root.HasLocation) { Log("Choose a backup folder first, then add an account."); return; }
        _root.ShowConnectScrim = true;
        await Task.Delay(50);   // one frame: let the scrim paint before the WebView cold start blocks the UI thread
        try { await AddAccountCore(); } finally { _root.ShowConnectScrim = false; }
    }

    private async Task AddAccountCore()
    {
        // Same contract as ConnectCore: a user-initiated sign-in gets a FRESH chance at the keyring,
        // clearing any held no-key/refusal so the save can mint. Non-first rows' "Log In" routes here
        // (the first account routes to Connect via BacksInteractiveSession), and this reset was missing -
        // a held startup readout blocked the mint (Linux walk 2026-08-23 R1).
        Grog.Core.Platform.OsKeyring.ResetAvailabilityCache();
        _ = RefreshTokenProtectionSnapshotAsync();   // (UI-thread sweep 09-06 r2) the card follows the fresh keyring answer
        // (UI-thread sweep 09-06) config-dir resolve and the stale temp-token cleanup off the dispatcher; the
        // rest of this method already runs inside RunBusy's Task.Run.
        var root = _backupRoot;
        var (paths, tempPath) = await Task.Run(() =>
        {
            var p = Grog.Core.Storage.GrogPaths.Resolve(root);
            var tmp = System.IO.Path.Combine(p.ConfigDir, "tokens-adding.tmp.json");
            try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { }
            return (p, tmp);
        });
        var acctSvc = new Grog.Core.Auth.AccountService(_manifest, paths);

        var tempStore = new FileTokenStore(tempPath);
        var tempAuth = new GogAuthService(_apiHttp, tempStore,
            new Services.WebViewLoginProvider(() => _root.OwnerWindowResolver?.Invoke()));   // always throwaway now
        var tempApi = new GogApiClient(_apiHttp, tempAuth);

        await _root.RunBusy("Add a GOG account. Sign in…", async () =>
        {
            try
            {
                await tempAuth.EnsureAuthenticatedAsync();     // fresh WebView login: different account
                var user = await tempApi.GetUserInfoAsync();
                var id = user.Username;

                // Signing in as an account Grog already has is a no-op. Catch it BEFORE copying tokens or
                // merging: the merge re-walks a library already present and the copy strands a stray token file.
                var already = acctSvc.Accounts.FirstOrDefault(
                    a => string.Equals(a.Username, user.Username, StringComparison.OrdinalIgnoreCase));
                if (already is not null)
                {
                    // A registered account whose token was forgotten (Log Out) OR is unreadable signs back
                    // in here: adopt the fresh token for the existing row instead of treating it as a
                    // duplicate no-op. LooksReadable, not File.Exists -- an orphaned GRGK1 whose key left
                    // the keyring EXISTS but cannot decrypt, and judging that "already connected" discarded
                    // a real GOG login (Linux walk 2026-08-23, B1).
                    var ownPath = acctSvc.TokenPathFor(already.Id);
                    if (!Grog.Core.Auth.FileTokenStore.LooksReadable(ownPath))
                    {
                        // Covers the tokens.json slot (Id "") too; _auth lazily reloads it on next use.
                        System.IO.File.Copy(tempPath, ownPath, overwrite: true);
                        Grog.Core.Auth.FileTokenStore.ForgetReadable();
                        var nowConnected = _root.AnyAccountConnected();   // probed here on the worker, not inside the post (UI-thread sweep 09-06 r2)
                        Dispatcher.UIThread.Post(() =>
                        {
                            _root.IsConnected = nowConnected;
                            _root.RaiseBarState();
                            RefreshAccounts();
                            _root.RefreshFromManifest();   // clears "(account logged out)" captions same-turn
                            RaiseTokenSafety();   // the save may have minted the keyring key just now
                            Log($"account: {user.Username} - signed back in", category: LogCategory.Auth);
                            _root.ShowToast($"Signed back in as {user.Username}.", 0);   // info tier: success is not an error
                        });
                        return;
                    }
                    try { if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath); } catch { }
                    Dispatcher.UIThread.Post(() =>
                    {
                        RefreshAccounts();
                        Log($"{user.Username} is already connected -- nothing to add.");
                        _root.ShowToast($"{user.Username} is already connected.", 0);   // info tier
                    });
                    return;
                }

                System.IO.File.Copy(tempPath, acctSvc.TokenPathFor(id), overwrite: true);
                var acct = await acctSvc.AddOrUpdateAsync(id, user.Username, user.Username);

                Dispatcher.UIThread.Post(() => _library.BeginCatalog($"Merging {user.Username}'s library…"));
                // Core tags the merge with the NEW account's id so files attribute to it and leaves the
                // reconcile to us (this scan sees ONE library; a delisted sweep would flag the other accounts'
                // games). The art hook rides on ConfigureScan. (S2.2)
                var mergeHost = new MainWindowViewModel.AppRunHost(_root, LogCategory.Scan)
                {
                    ScanDress = (svc, _) => svc.Progress += p => Dispatcher.UIThread.Post(() =>
                        _library.UpdateCatalog(p.Completed, p.Total,
                            p.Total > 0 ? $"Merging {user.Username}… {p.Completed}/{p.Total}" : "Contacting GOG…")),
                };
                await Grog.Core.Runs.LibraryScan.MergeAccountAsync(_manifest, tempApi, acct, mergeHost, CancellationToken.None);
                await _root.ReconcileAndRefresh();
                Dispatcher.UIThread.Post(() => { RefreshAccounts(); _library.EndCatalog(); });
                Log($"Added {user.Username} and merged their library");
            }
            catch (OperationCanceledException) { Log("Add account was canceled"); }
            catch (Exception ex) { Log($"Couldn't add account: {Grog.Core.Api.GogError.Describe(ex)}", isError: true); }
            finally { try { System.IO.File.Delete(tempPath); } catch { } }
        });
    }

    /// <summary>One dot per registered account (first five): green = token present and readable. Rebuilt with
    /// the dashboard and on Accounts refresh, so the rail cannot disagree with the cards.</summary>
    public sealed record AccountDot(Avalonia.Media.IBrush Brush, string Tip);
    public System.Collections.ObjectModel.ObservableCollection<AccountDot> RailAccountDots { get; } = new();
    // Rail-dot token readability, refreshed on the thread pool at most every 5 s; the 1 Hz pass reads the
    // remembered answer and never does the file/keyring probe itself (UI-thread sweep 09-06 r2). Written only
    // on the UI thread (the pool result is posted back), so the reads need no lock.
    private readonly Dictionary<string, bool> _railTokenReadable = new();
    private long _railTokenProbedAt = long.MinValue / 2;
    private int _railTokenProbing;
    internal void RebuildRailAccountDots()
    {
        if (_manifest is null || string.IsNullOrEmpty(_backupRoot)) { if (RailAccountDots.Count > 0) RailAccountDots.Clear(); return; }
        // Runs from every dashboard pass: the config dir was resolved once at bind (no re-resolve, no root probe).
        var paths = _configPaths ??= Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot);
        var svc = new Grog.Core.Auth.AccountService(_manifest, paths);
        var accounts = svc.Accounts.Take(5).Select(a => (Id: a.Id, Name: string.IsNullOrEmpty(a.Username) ? a.Id : a.Username,
                                                          Path: svc.TokenPathFor(a.Id))).ToList();

        // Refresh the remembered answers off-thread (at most every 5 s, one in flight); until the first probe
        // lands an account reads as signed out, exactly like an unreadable token, and the posted result fixes it.
        long now = Environment.TickCount64;
        if (now - _railTokenProbedAt >= 5000 && Interlocked.CompareExchange(ref _railTokenProbing, 1, 0) == 0)
        {
            _railTokenProbedAt = now;
            var pathsToProbe = accounts.Select(a => a.Path).ToList();
            _ = Task.Run(() =>
            {
                var result = new Dictionary<string, bool>();
                foreach (var pth in pathsToProbe) result[pth] = Grog.Core.Auth.FileTokenStore.LooksReadableCached(pth);
                return result;
            }).ContinueWith(t =>
            {
                Interlocked.Exchange(ref _railTokenProbing, 0);
                if (!t.IsCompletedSuccessfully) return;
                var result = t.Result;
                Dispatcher.UIThread.Post(() =>
                {
                    bool changed = false;
                    foreach (var kv in result)
                        if (!_railTokenReadable.TryGetValue(kv.Key, out var was) || was != kv.Value) { _railTokenReadable[kv.Key] = kv.Value; changed = true; }
                    if (changed) RebuildRailAccountDots();   // the dots follow the fresh answer on the next pass, not 5 s later
                });
            }, TaskScheduler.Default);
        }

        // Diff against the bound list: only a dot whose brush or tip changed is replaced, so the rail does not
        // re-realize five items per second.
        var desired = new List<AccountDot>(accounts.Count);
        foreach (var a in accounts)
        {
            bool ok = _railTokenReadable.TryGetValue(a.Path, out var r) && r;
            // Gray, not orange, for signed out: inactive, not a warning -- a run reports real problems.
            desired.Add(new AccountDot(ok ? Palette.SuccessGreen : Palette.InkMuted,
                $"{a.Name} - {(ok ? "signed in" : "signed out")}"));
        }
        while (RailAccountDots.Count > desired.Count) RailAccountDots.RemoveAt(RailAccountDots.Count - 1);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i >= RailAccountDots.Count) { RailAccountDots.Add(desired[i]); continue; }
            var have = RailAccountDots[i];
            if (!ReferenceEquals(have.Brush, desired[i].Brush) || have.Tip != desired[i].Tip) RailAccountDots[i] = desired[i];
        }
    }

    /// <summary>Registered accounts whose token is readable RIGHT NOW - the row-level "can this game's
    /// owner still download" answer (same predicate as the Accounts page's Connected state).</summary>
    internal List<string> ConnectedAccountIds()
    {
        if (_manifest is null) return new List<string>();
        // RebuildRows runs on every scan progress flush and every manifest refresh: no backup-root probe
        // (ResolveNoProbe) and no token re-read per call (review 09-06).
        // The answer is the rail dots' remembered probe (refreshed off-thread every 5 s and on every account
        // event); no token file is opened here (final check 09-06). Before the first probe lands the fallback
        // is the cached predicate, which only touches disk when nothing is remembered for that path.
        var svc = new Grog.Core.Auth.AccountService(_manifest, _configPaths ??= Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot));
        return svc.Accounts
            .Where(a => _railTokenReadable.TryGetValue(svc.TokenPathFor(a.Id), out var ok) ? ok
                       : Grog.Core.Auth.FileTokenStore.LooksReadableCached(svc.TokenPathFor(a.Id)))
            .Select(a => a.Id).ToList();
    }
}

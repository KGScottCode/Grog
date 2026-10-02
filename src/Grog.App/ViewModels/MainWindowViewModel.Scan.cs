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

// Scanning: the prominent activity strip and the multi-account scan runner. (The scan panel and the scan
// summary moved to LibraryViewModel.Scan in S3.2.)
public partial class MainWindowViewModel
{
    // --- prominent activity progress (primary operations: library sync, backup) ---
    /// <summary>When true, show the big amber progress panel over the content, not just the status bar.</summary>
    [ObservableProperty] private bool _activityVisible;
    /// <summary>Headline for the activity panel, e.g. "Fetching your library" / "Backing up your games".</summary>
    [ObservableProperty] private string _activityTitle = "";
    /// <summary>Sub-line, e.g. "47 of 158" or a current item name.</summary>
    [ObservableProperty] private string _activityDetail = "";
    /// <summary>0-100 for the activity panel's bar; mirrors ProgressValue during primary ops.</summary>
    [ObservableProperty] private double _activityValue;
    /// <summary>True for indeterminate phases (e.g. contacting GOG before totals are known).</summary>
    [ObservableProperty] private bool _activityIndeterminate;
    /// <summary>True shows the big centered overlay (heavy ops); false keeps to the slim line.</summary>
    [ObservableProperty] private bool _activityModal;

    private void BeginActivity(string title, bool indeterminate = true, bool modal = false)
    {
        ActivityTitle = title;
        ActivityDetail = "";
        ActivityValue = 0;
        ActivityIndeterminate = indeterminate;
        ActivityModal = modal;
        ActivityVisible = true;
    }
    private void UpdateActivity(int done, int total, string? detail = null)
    {
        ActivityIndeterminate = total <= 0;
        ActivityValue = total <= 0 ? 0 : 100.0 * done / total;
        ActivityDetail = detail ?? (total > 0 ? $"{done} of {total}" : "");
    }
    private void EndActivity() { ActivityVisible = false; ActivityModal = false; }

    /// <summary>Per-account session cache for an engine run: each file fetches through its first authenticated
    /// owner; all-owners-signed-out = Skipped, never a strike. Null with no registered accounts.</summary>
    private Grog.Core.Auth.AccountSessions? EngineSessions()
    {
        if (_manifest.Current.Accounts.Count == 0) return null;
        // Only the config dir is needed here; the legacy-state probe of the backup root ran at bind.
        var sessions = new Grog.Core.Auth.AccountSessions(_manifest, Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot), _apiHttp)
        {
            OnThrottle = msg => Session.Log(msg),
        };
        // The first account gets a NON-INTERACTIVE chain over the interactive token store: a background worker
        // must never pop the login dialog, and GogAuthService syncs from the store before every refresh so the
        // two instances never rotate against each other's refresh token.
        var auth = new GogAuthService(_apiHttp, Session.TokenStore, new NonInteractiveLoginProvider());
        var api = new GogApiClient(_apiHttp, auth) { OnThrottle = msg => Session.Log(msg) };
        sessions.Adopt(_manifest.Current.Accounts[0].Id, auth, api);
        return sessions;
    }

    /// <summary>ANY registered account has a readable stored token (cheap file probe). A signed-in secondary
    /// keeps scans and downloads alive, so IsConnected follows this, not just tokens.json.</summary>
    internal bool AnyAccountConnected() => AnyAccountConnectedCore(_manifest, _backupRoot);

    /// <summary>Same answer off the dispatcher: the token probe is a stat + open per account and, under a
    /// keyring-sealed token, a keyring call. Captures the manifest and root on the calling thread
    /// (UI-thread sweep 09-06 r2).</summary>
    internal Task<bool> AnyAccountConnectedAsync()
    {
        var m = _manifest; var root = _backupRoot;
        if (m is null) return Task.FromResult(false);
        return Task.Run(() => AnyAccountConnectedCore(m, root));
    }

    private static bool AnyAccountConnectedCore(JsonManifestStore? manifest, string? root)
    {
        try
        {
            if (manifest is null) return false;
            // LooksReadable, not File.Exists: a GRGK1 file under a locked keyring is a token the app
            // cannot use. Cached per path (5 s) and no backup-root probe: this is asked more than once per
            // user action (UI-thread sweep 09-06 r2).
            var paths = Grog.Core.Storage.GrogPaths.ResolveNoProbe(root);
            var svc = new Grog.Core.Auth.AccountService(manifest, paths);
            return manifest.Current.Accounts.Any(
                a => Grog.Core.Auth.FileTokenStore.LooksReadableCached(svc.TokenPathFor(a.Id)));
        }
        catch { return false; }
    }

    /// <summary>Run a FULL library scan through Core's <see cref="Grog.Core.Runs.LibraryScan"/> (every
    /// connected account, or the legacy single session with none registered). The App only dresses each
    /// pass (host.ConfigureScan) and logs the per-account outcome. (S2.2)</summary>
    /// <param name="dress">Progress dressing; second arg is the account name, "" when naming is noise.</param>
    private async Task<Grog.Core.Sync.SyncResult> RunFullScanAsync(
        System.Action<LibrarySyncService, string> dress, CancellationToken ct)
    {
        // Scans re-read every account token: give the keyring a fresh chance (see ConnectCore note).
        Grog.Core.Platform.OsKeyring.ResetAvailabilityCache();
        _ = Accounts.RefreshTokenProtectionSnapshotAsync();   // the SIGN-IN SECURITY card follows the fresh keyring answer (UI-thread sweep 09-06 r2)
        var host = new AppRunHost(this, LogCategory.Scan) { ScanDress = dress };
        var outcome = await Grog.Core.Runs.LibraryScan.RunAsync(_manifest, _api, EngineSessions(), host, ct, datePurchases: true);
        Library._lastScanPasses = outcome.Passes;
        if (outcome.Passes is { } passes)
        {
            // Log each pass account-first; shared games count in every owner's pass, and the grid's
            // owner discs answer per-game sharing.
            foreach (var p in passes)
                Dispatcher.UIThread.Post(() => Log(
                    p.Skipped ? $"account: {p.Name} - scan skipped ({p.SkipReason})"
                    : p.Result is { } pr
                        ? $"account: {p.Name} - scanned {pr.GamesSeen} products ({pr.NewGames} new, {pr.NewFiles} new files, {pr.UpdatedFiles} updated"
                          + (pr.FetchFailures > 0
                              ? $", FAILED to fetch {string.Join(", ", pr.FetchFailedNames.Take(5))}: {pr.FetchFailReason}"
                              : "")
                          + (pr.RichListingUnavailable
                              ? "; GOG's library listing did not answer, so types and the excluded list were kept from the last scan)"
                              : ")")
                        : $"account: {p.Name} - scan FAILED ({(p.Error is { } pe ? Grog.Core.Api.GogError.Describe(pe) : "unknown error")})",
                    isError: p.Result is null && !p.Skipped, category: LogCategory.Scan));
        }
        return outcome.Aggregate;
    }

    // ---- (New items 09-09) The manual-rescan question about still-flagged New items ----
    // A MANUAL rescan is the moment "New" means something: whatever this scan adds becomes the new New, so
    // the standing flags are either retired now or deliberately kept. Scheduled/unattended runs never ask
    // and never clear -- nobody is there to answer, and a silent clear would erase the evidence.
    [ObservableProperty] private bool _showClearNewPrompt;
    [ObservableProperty] private string _clearNewPromptTitle = "";
    /// <summary>Set while the prompt is up; completed with the user's answer. Null = no prompt pending.</summary>
    private TaskCompletionSource<bool?>? _clearNewAnswer;

    public string ClearNewPromptBody =>
        "They stay in your library and keep whatever backup state they have. Anything this scan adds will be flagged New.";

    [RelayCommand] private void ClearNewAndScan()  => AnswerClearNew(true);
    [RelayCommand] private void KeepNewAndScan()   => AnswerClearNew(false);
    [RelayCommand] private void CancelClearNewScan() => AnswerClearNew(null);

    private void AnswerClearNew(bool? answer)
    {
        ShowClearNewPrompt = false;
        var tcs = _clearNewAnswer; _clearNewAnswer = null;
        tcs?.TrySetResult(answer);
    }

    /// <summary>Ask before a MANUAL scan when items are still flagged New. Returns false when the user
    /// cancelled the scan; true to go ahead (having cleared the flags first, if that is what they chose).
    /// No flags, no prompt. Call on the UI thread, before any scan work starts.</summary>
    private async Task<bool> ConfirmManualRescanAsync()
    {
        if (_manifest is null) return true;
        int n = _manifest.Read(Grog.Core.Sync.NewItems.Count);   // in-memory count under the gate
        if (n == 0) return true;
        ClearNewPromptTitle = $"Clear the New flag on {n} items?";
        OnPropertyChanged(nameof(ClearNewPromptBody));
        _clearNewAnswer = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ShowClearNewPrompt = true;
        var answer = await _clearNewAnswer.Task;
        if (answer is null) return false;                        // Cancel: no scan at all
        if (answer == false) return true;                        // Keep and Scan
        // Clear and Scan. Disk work is awaited off the dispatcher by SaveAsync; the manifest may have moved
        // while the dialog was up, so the count is re-taken rather than trusted.
        var manifest = _manifest;
        int cleared = await Task.Run(() => { int c = 0; manifest.Mutate(m => c = Grog.Core.Sync.NewItems.ClearAll(m)); return c; });   // gate wait off the dispatcher
        if (cleared > 0)
        {
            await manifest.SaveAsync();
            Library.RebuildRows();
            Library.RaiseNewItems();
            Log($"Cleared the New flag on {cleared} item(s) before rescanning.", category: LogCategory.Scan);
        }
        return true;
    }

    /// <summary>Bare version from the assembly's informational version: "0.1.0" in a release build,
    /// "0.1.0.1133" in a dev build, where the fourth part is the repo's commit count.</summary>
    internal static string VersionString
    {
        get
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var attr = (System.Reflection.AssemblyInformationalVersionAttribute?)System.Attribute.GetCustomAttribute(
                asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            var info = attr?.InformationalVersion;
            var v = string.IsNullOrWhiteSpace(info) ? asm.GetName().Version?.ToString() ?? "0.9.0" : info;
            var plus = v.IndexOf('+');   // strip build-metadata suffix (e.g. "+abc123")
            if (plus >= 0) v = v[..plus];
            // Shown as MAJOR.MINOR.PATCH: the build counter stays in the assembly and commit subjects.
            // The update check compares this against the tag with equal parts, so a build of the tag never nags.
            var parts = v.Split('.');
            if (parts.Length > 3) v = string.Join('.', parts.Take(3));
            return v;
        }
    }

    /// <summary>App version for the About box.</summary>
    public string AppVersionText => $"v{VersionString.TrimStart('v', 'V')} \u00A9 2026 - Kevin G. Scott";
}

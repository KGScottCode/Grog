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

public partial class MainWindowViewModel
{
    [ObservableProperty] private bool _activityVisible;
    [ObservableProperty] private string _activityTitle = "";
    [ObservableProperty] private string _activityDetail = "";
    [ObservableProperty] private double _activityValue;
    [ObservableProperty] private bool _activityIndeterminate;
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

    private Grog.Core.Auth.AccountSessions? EngineSessions()
    {
        if (_manifest.Current.Accounts.Count == 0) return null;
        var sessions = new Grog.Core.Auth.AccountSessions(_manifest, Grog.Core.Storage.GrogPaths.ResolveNoProbe(_backupRoot), _apiHttp)
        {
            OnThrottle = msg => Session.Log(msg),
        };
        var auth = new GogAuthService(_apiHttp, Session.TokenStore, new NonInteractiveLoginProvider());
        var api = new GogApiClient(_apiHttp, auth) { OnThrottle = msg => Session.Log(msg) };
        sessions.Adopt(_manifest.Current.Accounts[0].Id, auth, api);
        return sessions;
    }

    internal bool AnyAccountConnected() => AnyAccountConnectedCore(_manifest, _backupRoot);

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
            var paths = Grog.Core.Storage.GrogPaths.ResolveNoProbe(root);
            var svc = new Grog.Core.Auth.AccountService(manifest, paths);
            return manifest.Current.Accounts.Any(
                a => Grog.Core.Auth.FileTokenStore.LooksReadableCached(svc.TokenPathFor(a.Id)));
        }
        catch { return false; }
    }

    private async Task<Grog.Core.Sync.SyncResult> RunFullScanAsync(
        System.Action<LibrarySyncService, string> dress, CancellationToken ct)
    {
        Grog.Core.Platform.OsKeyring.ResetAvailabilityCache();
        _ = Accounts.RefreshTokenProtectionSnapshotAsync();   // the SIGN-IN SECURITY card follows the fresh keyring answer
        var host = new AppRunHost(this, LogCategory.Scan) { ScanDress = dress };
        var outcome = await Grog.Core.Runs.LibraryScan.RunAsync(_manifest, _api, EngineSessions(), host, ct, datePurchases: true);
        Library._lastScanPasses = outcome.Passes;
        if (outcome.Passes is { } passes)
        {
            foreach (var p in passes)
                Dispatcher.UIThread.Post(() => Log(
                    p.Skipped ? $"account: {p.Name} - scan skipped ({p.SkipReason})"
                    : p.Result is { } pr
                        ? $"account: {p.Name} - scanned {pr.GamesSeen} products ({pr.NewGames} new, {pr.NewFiles} new files, {pr.UpdatedFiles} updated"
                          + (pr.FetchFailures > 0
                              ? $", FAILED to fetch {string.Join(", ", pr.FetchFailedNames.Take(5))}: {pr.FetchFailReason}"
                              : "")
                          + (pr.RichListingUnavailable
                              ? "; GOG's library listing did not answer, so types and the excluded list were kept from the last scan"
                              : "")
                          + (pr.ModCatalogError is { } mce
                              ? $"; GOG's mod catalog did not answer ({mce}), so mods are listed as games this scan"
                              : "")
                          + ")"
                        : $"account: {p.Name} - scan FAILED ({(p.Error is { } pe ? Grog.Core.Api.GogError.Describe(pe) : "unknown error")})",
                    isError: p.Result is null && !p.Skipped, category: LogCategory.Scan));
        }
        return outcome.Aggregate;
    }

    [ObservableProperty] private bool _showClearNewPrompt;
    [ObservableProperty] private string _clearNewPromptTitle = "";
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
            var parts = v.Split('.');
            if (parts.Length > 3) v = string.Join('.', parts.Take(3));
            return v;
        }
    }

    public string AppVersionText => $"v{VersionString.TrimStart('v', 'V')} \u00A9 2026 - Kevin G. Scott";
}

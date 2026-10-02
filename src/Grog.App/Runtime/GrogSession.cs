// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.Core.Storage;
using Grog.App.ViewModels;

namespace Grog.App.Runtime;

/// <summary>
/// The application state that is not a view: the manifest store, the GOG client and its session, the
/// settings, the chosen root, the live download engine and the run's cancellation. One instance per
/// process, created by the shell and handed to every page view model (S1, 09-08).
///
/// No Avalonia here, no ObservableProperty, no Dispatcher: this type is built and driven from tests with a
/// temp profile and a fake GOG. Presentation stays in the view models; orchestration (scan, backup, import,
/// verify, reorg) moves here in S2 and calls Core's run types.
/// </summary>
public sealed class GrogSession
{
    public AppSettings Settings { get; }
    /// <summary>Long-lived client for downloads (no timeout: a 4 GB installer is not a hang).</summary>
    public HttpClient Http { get; }
    /// <summary>Bounded client for metadata and API calls.</summary>
    public HttpClient ApiHttp { get; }

    public JsonManifestStore Manifest { get; set; } = null!;
    /// <summary>ONE ledger per session of what a running move still has to write; planner and engine read it.</summary>
    public Grog.Core.Volumes.PendingWrites PendingWrites { get; } = new();
    public GogApiClient Api { get; set; } = null!;
    public GogAuthService Auth { get; set; } = null!;
    public FileTokenStore TokenStore { get; set; } = null!;
    /// <summary>The token file the interactive (WebView) session is bound to: the first registered account's,
    /// or a scratch file until Connect learns the username.</summary>
    public string InteractiveTokenPath { get; set; } = "";

    public string BackupRoot { get; set; } = "";
    public string ConfigDir { get; set; } = "";
    public GrogPaths? ConfigPaths { get; set; }
    /// <summary>The store and clients exist and the manifest is loaded.</summary>
    public bool ServicesReady { get; set; }

    private volatile DownloadEngine? _liveEngine;
    /// <summary>The engine of the run in progress, or null. Volatile: read from workers and the UI.</summary>
    public DownloadEngine? LiveEngine { get => _liveEngine; set => _liveEngine = value; }
    private volatile bool _stopRequested;
    /// <summary>Transient signal that breaks the run loops (Pause / Stop / quit).</summary>
    public bool StopRequested { get => _stopRequested; set => _stopRequested = value; }
    public CancellationTokenSource? Cts { get; set; }
    /// <summary>Cancels the per-file download path, which has no RunBusy Cts of its own.</summary>
    public CancellationTokenSource? DownloadCts { get; set; }

    /// <summary>Where session-level messages go (the App's activity log). Set by the shell.</summary>
    public Action<string, bool, LogCategory>? LogSink { get; set; }
    public void Log(string message, bool isError = false, LogCategory category = LogCategory.General)
        => LogSink?.Invoke(message, isError, category);

    public GrogSession(AppSettings settings, HttpClient? http = null, HttpClient? apiHttp = null)
    {
        Settings = settings;
        Http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        ApiHttp = apiHttp ?? GogApiClient.CreateApiHttpClient();   // compressed API responses; the download client stays raw
    }

    /// <summary>Resolve the config paths, open and load the manifest store, run the one-time account
    /// migration. Off-thread where it touches disk. Throws <see cref="ManifestTooNewException"/> for a
    /// newer build's profile. Does not bind a backup root.</summary>
    public async Task OpenStoreAsync(GrogPaths? paths = null)
    {
        paths ??= await Task.Run(() => GrogPaths.Resolve(null));
        ConfigPaths = paths; ConfigDir = paths.ConfigDir;
        var store = new JsonManifestStore(paths);
        store.SaveSkipped += msg => Log(msg, isError: true);   // a save that gave up must never be silent
        Manifest = store;
        await Manifest.LoadAsync();
        // Upgrade a pre-multi-account install before anything touches a token file: the unnamed slot
        // becomes an ordinary account and its bare tokens.json is renamed to the per-account scheme.
        try { await new AccountService(Manifest, paths).MigrateLegacyPrimaryAsync(); }
        catch { /* best-effort; an interrupted migration resumes next launch */ }
    }

    /// <summary>Bind the interactive session to the first registered account's token file (or a scratch file
    /// with no accounts yet) and rebuild the auth + API clients on it.</summary>
    public void RebindInteractiveSession(IInteractiveLoginProvider login, GrogPaths? paths = null)
    {
        paths ??= ConfigPaths ?? GrogPaths.ResolveNoProbe(BackupRoot);
        var first = Manifest?.Current.Accounts.Count > 0 ? Manifest.Current.Accounts[0] : null;
        InteractiveTokenPath = first is null
            ? System.IO.Path.Combine(paths.ConfigDir, "tokens-connect.tmp.json")
            : new AccountService(Manifest!, paths).TokenPathFor(first.Id);
        TokenStore = new FileTokenStore(InteractiveTokenPath);
        Auth = new GogAuthService(ApiHttp, TokenStore, login);
        Api = new GogApiClient(ApiHttp, Auth);
        Api.OnThrottle = msg => Log(msg);   // 429/5xx backoff waits surface in the Activity log
    }
}

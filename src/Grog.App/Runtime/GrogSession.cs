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

public sealed class GrogSession
{
    public AppSettings Settings { get; }
    public HttpClient Http { get; }
    public HttpClient ApiHttp { get; }

    public JsonManifestStore Manifest { get; set; } = null!;
    public Grog.Core.Volumes.PendingWrites PendingWrites { get; } = new();
    public GogApiClient Api { get; set; } = null!;
    public GogAuthService Auth { get; set; } = null!;
    public FileTokenStore TokenStore { get; set; } = null!;
    public string InteractiveTokenPath { get; set; } = "";

    public string BackupRoot { get; set; } = "";
    public string ConfigDir { get; set; } = "";
    public GrogPaths? ConfigPaths { get; set; }
    public bool ServicesReady { get; set; }

    private volatile DownloadEngine? _liveEngine;
    public DownloadEngine? LiveEngine { get => _liveEngine; set => _liveEngine = value; }
    private volatile bool _stopRequested;
    public bool StopRequested { get => _stopRequested; set => _stopRequested = value; }
    public CancellationTokenSource? Cts { get; set; }
    public CancellationTokenSource? DownloadCts { get; set; }

    public Action<string, bool, LogCategory>? LogSink { get; set; }
    public void Log(string message, bool isError = false, LogCategory category = LogCategory.General)
        => LogSink?.Invoke(message, isError, category);

    public GrogSession(AppSettings settings, HttpClient? http = null, HttpClient? apiHttp = null)
    {
        Settings = settings;
        Http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        ApiHttp = apiHttp ?? GogApiClient.CreateApiHttpClient();   // compressed API responses; the download client stays raw
    }

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

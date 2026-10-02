// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Manifest;

namespace Grog.App.ViewModels;

/// <summary>The Cloud Saves screen: owns the cloud-game list + its discovery/download flows via the same Core
/// services the CLI uses. Shell state is supplied lazily so it composes without re-coupling to MainWindowViewModel.</summary>
public partial class CloudSavesViewModel : ObservableObject
{
    private readonly Func<JsonManifestStore?> _manifest;
    private readonly Func<HttpClient> _apiHttp;
    private readonly Func<GogAuthService> _auth;
    private readonly Func<GogApiClient> _api;
    private readonly Func<string> _backupRoot;
    private readonly Func<bool> _isConnected;
    private readonly Func<string, Func<Task>, Task> _runBusy;
    private readonly Func<string, string?, Task<string?>> _pickFolder;
    private readonly Func<int> _keepCloudSaves;
    private readonly Func<Grog.Core.Auth.AccountSessions?> _sessions;

    /// <summary>Host log sink (wired by MainWindowViewModel): cloud-save work writes account-first
    /// [Cloud] lines to the shared Activity log / grog.log, which outlives the page.</summary>
    public Action<string, bool, LogCategory>? HostLog { get; set; }

    private void LogCloud(string message, bool isError = false)
        => HostLog?.Invoke(message, isError, LogCategory.Cloud);

    public CloudSavesViewModel(
        Func<JsonManifestStore?> manifest,
        Func<HttpClient> apiHttp,
        Func<GogAuthService> auth,
        Func<GogApiClient> api,
        Func<string> backupRoot,
        Func<bool> isConnected,
        Func<string, Func<Task>, Task> runBusy,
        Func<string, string?, Task<string?>> pickFolder,
        Func<int> keepCloudSaves,
        Func<Grog.Core.Auth.AccountSessions?>? sessions = null)
    {
        _sessions = sessions ?? (() => null);
        _manifest = manifest;
        _apiHttp = apiHttp;
        _auth = auth;
        _api = api;
        _backupRoot = backupRoot;
        _isConnected = isConnected;
        _runBusy = runBusy;
        _pickFolder = pickFolder;
        _keepCloudSaves = keepCloudSaves;
    }

    // --- Cloud-saves archive folder (defaults to <backup root>/Cloud Saves; overridable) ---

    /// <summary>The folder cloud-save archives land in -- the override if set, else the default.
    /// (UI-thread sweep 09-06 r2) a bound getter used to build a BackupLayout (probes every root on disk) per
    /// binding evaluation; the value is now cached and recomputed off the dispatcher by RefreshFromManifest.</summary>
    public string CloudFolderText
    {
        get
        {
            var m = _manifest();
            if (m is null) return "";
            // An explicit override needs no layout (avoids building one before a backup root exists).
            if (!string.IsNullOrEmpty(m.Current.CloudSavesFolder)) return m.Current.CloudSavesFolder;
            return _cloudFolderTextCache;
        }
    }
    private string _cloudFolderTextCache = "";
    private int _cloudFolderTextGen;

    /// <summary>Recompute the default cloud folder text on a worker and raise CloudFolderText when it lands.
    /// Only the LATEST request applies (a generation counter), and only if manifest/root are still the same.</summary>
    private void RecomputeCloudFolderText()
    {
        var m = _manifest();
        var root = _backupRoot();
        var gen = ++_cloudFolderTextGen;
        if (m is null || string.IsNullOrEmpty(root))
        {
            var v = m is null ? "" : "(set a backup folder first)";
            if (_cloudFolderTextCache != v) { _cloudFolderTextCache = v; OnPropertyChanged(nameof(CloudFolderText)); }
            return;
        }
        var current = m.Current;
        _ = Task.Run(() =>
        {
            string text;
            try { text = new Grog.Core.Volumes.BackupLayout(current, root).ResolveCloudBaseDir(out _) ?? "(set a backup folder first)"; }
            catch { text = "(set a backup folder first)"; }
            Dispatcher.UIThread.Post(() =>
            {
                if (gen != _cloudFolderTextGen) return;                       // a newer recompute is in flight
                if (!ReferenceEquals(_manifest(), m) || _backupRoot() != root) return;
                if (_cloudFolderTextCache == text) return;
                _cloudFolderTextCache = text;
                OnPropertyChanged(nameof(CloudFolderText));
                RebuildCloudFolderRows();   // the per-account rows read the base dir too (final check 09-06)
            });
        });
    }

    public bool CloudFolderIsOverridden => !string.IsNullOrEmpty(_manifest()?.Current.CloudSavesFolder);

    // --- Per-account folder rows: with multiple accounts the header lists each
    //     account's own folder ("Name : path") instead of one path, so the split layout is visible.
    public sealed record CloudFolderRow(string AccountId, string Name, string Path, AccountBadge? Badge, bool IsOverridden)
    { public bool HasBadge => Badge is not null; }

    /// <summary>Point ONE account's saves at a folder of its own, fully replacing its spot under the
    /// base. Per-account because a household may want each player's saves on their own disk.</summary>
    [RelayCommand]
    private async Task ChangeAccountCloudFolder(CloudFolderRow? row)
    {
        var m = _manifest();
        if (row is null || m is null) return;
        var acct = m.Current.Accounts.FirstOrDefault(a => a.Id == row.AccountId);
        if (acct is null) return;
        var picked = await _pickFolder($"Choose a cloud-saves folder for {row.Name}", string.IsNullOrEmpty(acct.CloudSavesFolder) ? row.Path : acct.CloudSavesFolder);
        if (string.IsNullOrEmpty(picked)) return;
        acct.CloudSavesFolder = picked;
        await m.SaveAsync();
        RefreshFromManifest();   // paths moved: rebuild rows + re-check bookkeeping against the new folder
    }

    [RelayCommand]
    private async Task ResetAccountCloudFolder(CloudFolderRow? row)
    {
        var m = _manifest();
        if (row is null || m is null) return;
        var acct = m.Current.Accounts.FirstOrDefault(a => a.Id == row.AccountId);
        if (acct is null || string.IsNullOrEmpty(acct.CloudSavesFolder)) return;
        acct.CloudSavesFolder = "";
        await m.SaveAsync();
        RefreshFromManifest();
    }

    public ObservableCollection<CloudFolderRow> CloudFolderRows { get; } = new();
    public bool ShowCloudFolderRows => (_manifest()?.Current.Accounts.Count ?? 0) > 1;
    public string CloudFolderHeader => ShowCloudFolderRows ? "Cloud Saves Backup Storage" : "Cloud Saves Backup Storage";

    private void RebuildCloudFolderRows()
    {
        CloudFolderRows.Clear();
        var m = _manifest();
        if (m is null || m.Current.Accounts.Count <= 1) { RaiseCloudFolderRows(); return; }
        var baseDir = CloudFolderText;
        foreach (var a in m.Current.Accounts)
        {
            var name = string.IsNullOrEmpty(a.Username) ? (a.Id.Length == 0 ? "This account" : a.Id) : a.Username;
            CloudFolderRows.Add(new CloudFolderRow(a.Id, name,
                Grog.Core.CloudSaves.CloudSaveReconciler.AccountArchiveDir(m.Current, baseDir, a.Id),
                OwnerBadges.Map.TryGetValue(a.Id, out var b) ? b : null,
                !string.IsNullOrEmpty(a.CloudSavesFolder)));
        }
        RaiseCloudFolderRows();
    }

    private void RaiseCloudFolderRows()
    {
        OnPropertyChanged(nameof(ShowCloudFolderRows));
        OnPropertyChanged(nameof(CloudFolderHeader));
    }

    [RelayCommand]
    private async Task ChangeCloudFolder()
    {
        var m = _manifest();
        if (m is null) return;
        var seed = string.IsNullOrEmpty(m.Current.CloudSavesFolder) ? _backupRoot() : m.Current.CloudSavesFolder;
        var picked = await _pickFolder("Choose a folder for cloud-save backups", seed);
        if (string.IsNullOrEmpty(picked)) return;
        m.Current.CloudSavesFolder = picked;
        await m.SaveAsync();
        OnPropertyChanged(nameof(CloudFolderText));
        OnPropertyChanged(nameof(CloudFolderIsOverridden));
        RefreshFromManifest();
    }

    [RelayCommand]
    private async Task ResetCloudFolder()
    {
        var m = _manifest();
        if (m is null) return;
        m.Current.CloudSavesFolder = "";
        await m.SaveAsync();
        OnPropertyChanged(nameof(CloudFolderText));
        OnPropertyChanged(nameof(CloudFolderIsOverridden));
        RefreshFromManifest();
    }

    /// <summary>The bound, filtered + sorted view. The full set lives in <see cref="_allCloud"/>; every
    /// refresh repopulates that and re-applies the current search + sort.</summary>
    public ObservableCollection<CloudGameRow> CloudGames { get; } = new();
    /// <summary>The bound list WITH account grouping: CloudAccountHeaderRow items interleaved with the game
    /// rows they own, kept flat for the row templates. A single account gets no grouping chrome.</summary>
    public ObservableCollection<object> CloudItems { get; } = new();
    private readonly List<CloudGameRow> _allCloud = new();
    [ObservableProperty] private string _cloudStatus = "";

    /// <summary>Expand/collapse one account's section, persisted like other view state.</summary>
    [RelayCommand]
    private void ToggleCloudAccount(CloudAccountHeaderRow? header)
    {
        if (header is null) return;
        header.IsExpanded = !header.IsExpanded;
        var m = _manifest();
        if (m is not null)
        {
            m.Mutate(cur =>   // (manifest gate 09-08)
            {
                if (header.IsExpanded) cur.CloudCollapsedAccounts.Remove(header.AccountId);
                else if (!cur.CloudCollapsedAccounts.Contains(header.AccountId)) cur.CloudCollapsedAccounts.Add(header.AccountId);
            });
            m.SaveSoon();   // a fold flag
        }
        ApplyCloudView();
    }

    /// <summary>The display name for an account id, from the registered accounts.</summary>
    private string AccountName(string id)
    {
        var a = _manifest()?.Current.Accounts.FirstOrDefault(x => x.Id == id);
        return a is null ? (id.Length == 0 ? "This account" : id)
             : string.IsNullOrEmpty(a.Username) ? (a.Id.Length == 0 ? "This account" : a.Id) : a.Username;
    }

    /// <summary>The cloud service bound to the account that OWNS a row's saves: per-account sessions when
    /// accounts are registered (non-interactive, can never pop a login), the legacy session otherwise.</summary>
    private Grog.Core.CloudSaves.CloudSaveService CloudServiceFor(string accountId)
    {
        if (_sessions() is { } s && s.Accounts.Count > 0)
            return new Grog.Core.CloudSaves.CloudSaveService(s.Http, s.AuthFor(accountId));
        return new Grog.Core.CloudSaves.CloudSaveService(_apiHttp(), _auth());
    }

    /// <summary>Core's cloud-save run over this page's manifest, sessions and root (its ServiceFor is the
    /// same rule as <see cref="CloudServiceFor"/>). (S2.2)</summary>
    private Grog.Core.Runs.CloudSaveRun NewCloudRun(CloudRunHost host)
        => new(_manifest()!, _apiHttp(), _auth(), _sessions(), _backupRoot(), host);

    /// <summary>The page's side of a CloudSaveRun: the run's per-game progress line becomes the status text
    /// during Download All (and is dropped for a single row, whose RunBusy label already says it); every
    /// other line is an account-first [Cloud] log entry, exactly the ones this page wrote itself. (S2.2)</summary>
    private sealed class CloudRunHost : Grog.Core.Runs.NullBackupHost
    {
        private readonly CloudSavesViewModel _vm;
        private readonly bool _statusProgress;
        public CloudRunHost(CloudSavesViewModel vm, bool statusProgress) { _vm = vm; _statusProgress = statusProgress; }
        public override void Log(string message, bool isError = false)
        {
            if (message.StartsWith("Backing up cloud saves…", StringComparison.Ordinal))
            {
                if (_statusProgress) Dispatcher.UIThread.Post(() => _vm.CloudStatus = message);
                return;
            }
            Dispatcher.UIThread.Post(() => _vm.LogCloud(message, isError));
        }
    }

    // ---- Search + sort + summary ----
    public enum CloudSortKey { Name, Size, Last, Changed }
    // Name A->Z; the rest most-first. Shared scaffold: SortState.
    private readonly SortState<CloudSortKey> _cloudSort = new(CloudSortKey.Name, k => k == CloudSortKey.Name);

    [ObservableProperty] private string _cloudFilter = "";
    partial void OnCloudFilterChanged(string value) => ApplyCloudView();

    [RelayCommand]
    private void SortCloud(string? key)
    {
        var k = key switch { "Size" => CloudSortKey.Size, "Last" => CloudSortKey.Last, "Changed" => CloudSortKey.Changed, _ => CloudSortKey.Name };
        _cloudSort.Toggle(k);
        ApplyCloudView();
        RaiseCloudSortGlyphs();
    }

    public string CloudSortGlyphName    => _cloudSort.Glyph(CloudSortKey.Name);
    public string CloudSortGlyphSize    => _cloudSort.Glyph(CloudSortKey.Size);
    public string CloudSortGlyphLast    => _cloudSort.Glyph(CloudSortKey.Last);
    public string CloudSortGlyphChanged => _cloudSort.Glyph(CloudSortKey.Changed);
    /// <summary>(UX 09-08 #4) The one sort control's face: current key + direction glyph ("Name  ▲").</summary>
    public string CloudSortLabel => _cloudSort.Key switch
    {
        CloudSortKey.Size => "Size" + CloudSortGlyphSize,
        CloudSortKey.Last => "Last" + CloudSortGlyphLast,
        CloudSortKey.Changed => "Changed" + CloudSortGlyphChanged,
        _ => "Name" + CloudSortGlyphName,
    };
    private void RaiseCloudSortGlyphs()
    {
        OnPropertyChanged(nameof(CloudSortGlyphName)); OnPropertyChanged(nameof(CloudSortGlyphSize));
        OnPropertyChanged(nameof(CloudSortGlyphLast)); OnPropertyChanged(nameof(CloudSortGlyphChanged));
        OnPropertyChanged(nameof(CloudSortLabel));   // (UX 09-08 #4)
    }

    /// <summary>Rebuild the bound list from the full set, honoring the search text and current sort.</summary>
    /// <summary>(09-19) The Library's hidden rule, so a hidden game is not NAMED here either. Applied to the VIEW
    /// only: <see cref="_allCloud"/>, the need count and "Download all" still cover its saves (names, never bytes).</summary>
    public Func<long, bool> IsHiddenGame { get; set; } = _ => false;
    internal void RefreshHiddenView() => ApplyCloudView();

    private void ApplyCloudView()
    {
        IEnumerable<CloudGameRow> q = _allCloud.Where(g => !IsHiddenGame(g.GogId));
        if (!string.IsNullOrWhiteSpace(CloudFilter))
            q = q.Where(g => g.Title.Contains(CloudFilter, StringComparison.OrdinalIgnoreCase));
        q = _cloudSort.Key switch
        {
            CloudSortKey.Size    => _cloudSort.Asc ? q.OrderBy(g => g.SizeBytes) : q.OrderByDescending(g => g.SizeBytes),
            CloudSortKey.Last    => _cloudSort.Asc ? q.OrderBy(g => g.LastBackup ?? DateTimeOffset.MinValue)
                                                  : q.OrderByDescending(g => g.LastBackup ?? DateTimeOffset.MinValue),
            // "Changed" surfaces games needing a local save (never backed up / size differs) first.
            CloudSortKey.Changed => _cloudSort.Asc ? q.OrderBy(g => g.IsUpToDate).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
                                                  : q.OrderByDescending(g => g.IsUpToDate).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase),
            _                    => _cloudSort.Asc ? q.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
                                                  : q.OrderByDescending(g => g.Title, StringComparer.OrdinalIgnoreCase),
        };
        CloudGames.Clear();
        foreach (var g in q) CloudGames.Add(g);

        // Grouped view: account header rows interleaved with their games, shown whenever MORE THAN ONE account
        // is registered -- an account with no saves still gets its header, saying so. One account = flat page.
        CloudItems.Clear();
        var shown = CloudGames.ToList();
        var registered = _manifest()?.Current.Accounts ?? new List<Grog.Core.Models.GrogAccount>();
        if (registered.Count <= 1)
        {
            foreach (var g in shown) CloudItems.Add(g);
        }
        else
        {
            var collapsed = _manifest()?.Current.CloudCollapsedAccounts ?? new List<string>();
            // Every registered account in registration order, plus any orphan ids still carrying saves
            // (e.g. a removed account's last-seen entries) so no row can ever be silently unreachable.
            var ids = registered.Select(a => a.Id)
                .Concat(_allCloud.Select(g => g.AccountId).Distinct().Where(id => registered.All(a => a.Id != id)))
                .ToList();
            foreach (var acct in ids)
            {
                var all = _allCloud.Where(g => g.AccountId == acct).ToList();
                var header = new CloudAccountHeaderRow(acct, AccountName(acct), all.Count,
                    all.Sum(g => g.SizeBytes), expanded: !collapsed.Contains(acct));
                CloudItems.Add(header);
                if (header.IsExpanded)
                    foreach (var g in shown.Where(g => g.AccountId == acct)) CloudItems.Add(g);
            }
        }
        OnPropertyChanged(nameof(CloudSummary)); OnPropertyChanged(nameof(HasCloudGames)); OnPropertyChanged(nameof(CloudCountText));
        OnPropertyChanged(nameof(CloudNeedCount)); OnPropertyChanged(nameof(CanDownloadAll)); OnPropertyChanged(nameof(DownloadAllText));
        OnPropertyChanged(nameof(RailCloudCount));   // the rail row state, or it stays blank forever
        OnPropertyChanged(nameof(RailCloudSub));   // (UX 09-08 #6)
    }

    public bool HasCloudGames => _allCloud.Count > 0;
    /// <summary>Header line: games, local saves, and bytes backed up.</summary>
    public string CloudSummary
    {
        get
        {
            if (_allCloud.Count == 0) return "";
            // Games count distinct titles; saves and bytes sum every row, because each (game, account)
            // row now owns its own archive and bookkeeping.
            int games = _allCloud.Select(g => g.GogId).Distinct().Count(), snaps = _allCloud.Sum(g => g.LocalSaveCount);
            long size = _allCloud.Sum(g => g.BackedUpSize);
            return $"{games} game{(games == 1 ? "" : "s")} · {snaps} local save{(snaps == 1 ? "" : "s")} · {Grog.Core.Format.ByteFormat.Size(size)} backed up";
        }
    }
    public string CloudCountText => !string.IsNullOrWhiteSpace(CloudFilter) ? $"{CloudGames.Count} of {_allCloud.Count} shown" : "";

    /// <summary>True when the status line reports a failure -- drives a loud red/bold treatment so a
    /// cloud backup that DIDN'T happen can't be missed.</summary>
    [ObservableProperty] private bool _cloudStatusIsError;

    public Avalonia.Media.IBrush CloudStatusBrush => CloudStatusIsError ? Palette.ErrorRedBright : Palette.InkMuted;
    public double CloudStatusFontSize => CloudStatusIsError ? 15 : 12;
    public Avalonia.Media.FontWeight CloudStatusWeight => CloudStatusIsError ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal;

    partial void OnCloudStatusIsErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(CloudStatusBrush));
        OnPropertyChanged(nameof(CloudStatusFontSize));
        OnPropertyChanged(nameof(CloudStatusWeight));
    }

    /// <summary>Show the cloud-save games already flagged in the manifest (no network).</summary>
    public void RefreshFromManifest()
    {
        CloudStatusIsError = false;
        OnPropertyChanged(nameof(CloudFolderText));          // backup root may have loaded since startup
        RecomputeCloudFolderText();                          // (UI-thread sweep 09-06 r2) default folder resolved off-thread
        OnPropertyChanged(nameof(CloudFolderIsOverridden));
        RebuildCloudFolderRows();
        _allCloud.Clear();
        var manifest = _manifest();
        if (manifest is null) { ApplyCloudView(); return; }
        ReconcileStaleBookkeeping(manifest);   // off-thread; re-runs this refresh if it changes anything
        // Show only games GOG's last discovery confirmed have cloud saves (a cached CloudClientId alone lingers).
        // ONE ROW PER (game, account): a co-saved game appears once per section, each with ITS OWN archive + bookkeeping.
        manifest.Mutate(cur => Grog.Core.CloudSaves.CloudSaveReconciler.SeedLegacy(cur));   // (manifest gate 09-08)
        foreach (var it in manifest.Current.Items
                     .Where(i => i.HasCloudSaves ?? false)
                     .OrderBy(i => i.Title))
            foreach (var e in it.CloudByAccount)
                _allCloud.Add(new CloudGameRow(it.GogId, it.Title, it.CloudClientId, e.SizeBytes,
                    e.LastBackup, e.LocalSaveCount, e.BackedUpSize,
                    e.Files, e.UpdatedUtc, e.BackedUpFiles, e.BackedUpChangeUtc,
                    accountId: e.AccountId));
        ApplyCloudView();
        // Only the EMPTY case says anything: with games present the summary sub-head below already
        // carries games / localSaves / bytes, so a count here restated it one line early.
        CloudStatus = _allCloud.Count == 0
            ? "No cloud saves known yet. Run a library scan (or press Check for new saves) to look."
            : "";

        // Re-open a game whose panel was open before a rebuild (e.g. right after pruning one of its localSaves).
        if (_expandAfterRefresh is long gid)
        {
            _expandAfterRefresh = null;
            var row = _allCloud.FirstOrDefault(g => g.GogId == gid);
            if (row is not null && row.HasBackup) { row.IsExpanded = true; LoadLocalSaves(row); }
        }
    }

    /// <summary>A "backed up" claim is only true if local save files actually exist on disk: clear the
    /// bookkeeping for any game whose local save folder is empty, so a row never claims an empty backup.
    /// The disk walk (one directory enumeration per claimed game, on the backup drive) runs OFF the UI
    /// thread and at most once a minute: it used to run inline on every manifest refresh - thirty-odd call
    /// sites, scan completion included - and a sleeping drive froze the window (review 09-06). Changes are
    /// applied on the dispatcher and trigger one more refresh.</summary>
    private long _lastReconcileTicks = long.MinValue;
    private bool _reconciling;
    private void ReconcileStaleBookkeeping(JsonManifestStore manifest, bool force = false)
    {
        var now = Environment.TickCount64;
        if (_reconciling || (!force && now - _lastReconcileTicks < 60_000)) return;
        var candidates = manifest.Current.Items
            .SelectMany(it => it.CloudByAccount
                .Where(e => e.LocalSaveCount > 0 || e.LastBackup is not null || e.BackedUpSize != 0)
                .Select(e => (Item: it, Entry: e)))
            .ToList();
        _lastReconcileTicks = now;
        if (candidates.Count == 0) return;
        var backupRoot = _backupRoot();
        var current = manifest.Current;
        // What each entry claimed when it was checked: a cloud download finishing during the walk changes the
        // claim, and that entry must not be wiped on the strength of a stale check (verification 09-06).
        var claims = candidates.Select(c => (c.Entry.LocalSaveCount, c.Entry.LastBackup, c.Entry.BackedUpSize)).ToList();
        _reconciling = true;
        _ = Task.Run(() =>
        {
            var stale = new List<(Grog.Core.Models.LibraryItem Item, Grog.Core.Models.CloudAccountSave Entry, int Index)>();
            try
            {
                var layout = new Grog.Core.Volumes.BackupLayout(current, backupRoot);   // one layout, not one per game; probes every root, so off-thread
                for (int i = 0; i < candidates.Count; i++)
                    if (!HasLocalSaveFilesOnDisk(manifest, layout, candidates[i].Item, backupRoot, candidates[i].Entry.AccountId))
                        stale.Add((candidates[i].Item, candidates[i].Entry, i));
            }
            catch { /* a probe threw: change nothing this pass */ stale.Clear(); }
            Dispatcher.UIThread.Post(() =>
            {
                _reconciling = false;
                if (stale.Count == 0) return;
                // The world moved on (folder changed, library reloaded): every "not on disk" answer is about
                // the old folder, and applying it would zero real bookkeeping.
                if (!ReferenceEquals(_manifest(), manifest) || !ReferenceEquals(manifest.Current, current) || _backupRoot() != backupRoot) return;
                bool any = false;
                using (manifest.Gate.Enter())   // (manifest gate 09-08)
                    foreach (var (it, e, i) in stale)
                    {
                        if ((e.LocalSaveCount, e.LastBackup, e.BackedUpSize) != claims[i]) continue;   // changed since the check
                        any = true;
                        e.LocalSaveCount = 0;
                        e.LastBackup = null;
                        e.BackedUpSize = 0;
                        e.BackedUpFiles = 0;
                        e.BackedUpChangeUtc = null;
                        Grog.Core.CloudSaves.CloudSaveReconciler.MirrorLegacy(it);
                    }
                if (!any) return;
                Runtime.ManifestSaves.Default.SaveInBackground(manifest, "cloud saves");
                RefreshFromManifest();
            });
        });
    }

    /// <summary>True if the game has at least one actual cloud-save file under ONE ACCOUNT's local save
    /// folder. Uses the SAME path resolution as the downloader so the check matches where files land.</summary>
    private static bool HasLocalSaveFilesOnDisk(JsonManifestStore manifest, Grog.Core.Volumes.BackupLayout layout, Grog.Core.Models.LibraryItem it, string backupRoot, string accountId)
    {
        try
        {
            var slug = string.IsNullOrEmpty(it.Slug) ? it.Title : it.Slug;
            var cloudBase = layout.ResolveCloudBaseDir(out _) ?? System.IO.Path.Combine(backupRoot, "Cloud Saves");
            var root = System.IO.Path.Combine(
                Grog.Core.CloudSaves.CloudSaveReconciler.GameArchiveDir(manifest.Current, cloudBase, accountId, slug), "cloud-saves");
            return System.IO.Directory.Exists(root)
                && System.IO.Directory.EnumerateFiles(root, "*", System.IO.SearchOption.AllDirectories).Any();
        }
        catch { return true; }   // on any error, don't wrongly wipe a real backup's bookkeeping
    }

    /// <summary>One-call discovery via GOG's own master list (v2 containers). Flags which library
    /// games have cloud saves + caches size/space_id, then shows them. No per-game library walk.</summary>
    public async Task DiscoverAsync()
    {
        var manifest = _manifest();
        if (manifest is null) return;
        if (!_isConnected()) return;   // signed out: the page renders with its sign-in invitation instead
        try
        {
            ReconcileStaleBookkeeping(manifest, force: true);   // (UI-thread sweep 09-06 r2) inside the try: callers fire-and-forget this method
            CloudStatusIsError = false;
            CloudStatus = "Checking GOG for cloud saves…";
            if (_sessions() is { } sessions && sessions.Accounts.Count > 0)
            {
                // Multi-account: every connected account's containers via the NON-INTERACTIVE session cache
                // (never pops a login). Partial knowledge is safe: a signed-out account stays as last seen.
                var runner = new Grog.Core.Sync.MultiAccountSync(sessions, manifest) { IncludeCloudSaves = false };
                var (byAccount, complete) = await runner.ListCloudContainersByAccountAsync();
                if (byAccount.Count == 0)
                {
                    CloudStatusIsError = true;
                    CloudStatus = "No signed-in account to check. Sign in from the Accounts page and try again.";
                    return;
                }
                int games = manifest.Read(cur => Grog.Core.CloudSaves.CloudSaveReconciler.ApplyAccounts(cur, byAccount));   // (manifest gate 09-08)
                await manifest.SaveAsync();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    RefreshFromManifest();
                    if (!complete)
                    {
                        CloudStatus = "Some accounts are signed out; their saves are shown as last seen.";
                    }
                    else if (games == 0) CloudStatus = "No cloud saves found on any account.";
                });
                return;
            }

            var cloud = new Grog.Core.CloudSaves.CloudSaveService(_apiHttp(), _auth());
            var disco = new Grog.Core.CloudSaves.CloudSaveDiscoveryService(cloud);
            var api = _api();
            // Discovery is self-sufficient (no full library sync): matched container ids reuse their library
            // title, unmatched ids get their title fetched directly, so cloud saves works from a cold start.
            var rows = await disco.DiscoverAsync(manifest.Current,
                async (pid, _) => (await api.GetProductInfoAsync(pid))?.Title, gate: manifest.Gate);   // (manifest gate 09-08)
            await manifest.SaveAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allCloud.Clear();
                foreach (var r in rows)
                    _allCloud.Add(new CloudGameRow(r.GogId, r.Title, r.ClientId, r.SizeBytes,
                        r.LastBackup, r.SnapshotCount, r.BackedUpSize,
                        r.Files, r.LatestChangeUtc, r.BackedUpFiles, r.BackedUpChangeUtc));
                ApplyCloudView();
                CloudStatus = rows.Count == 0
                    ? "No cloud saves found on your account."
                    : "";
            });
        }
        catch (Exception ex)
        {
            CloudStatusIsError = true; CloudStatus = $"Couldn't list cloud saves: {Grog.Core.Api.GogError.Describe(ex)}";
            LogCloud($"Couldn't list cloud saves: {ex}", isError: true);   // the log gets the whole exception
        }
        finally { HasDiscovered = true; }
    }

    /// <summary>True once a discovery pass has run this session. Navigating to the page auto-discovers ONLY
    /// while false: a cold start populates itself, but returning never re-queries GOG behind the user's back.</summary>
    public bool HasDiscovered { get; private set; }

    /// <summary>Explicit re-query. The page is a view of what we already know; asking GOG again is an
    /// action the user takes, not something that happens because they looked at the tab.</summary>
    [RelayCommand]
    private async Task CheckForNewSaves() => await DiscoverAsync();

    /// <summary>Per-game "Save As": export this game's cloud save into a folder the user picks (e.g. the
    /// game's live save folder) -- a manual restore, separate from the timestamped archive.</summary>
    [RelayCommand]
    private async Task SaveAs(CloudGameRow? row)
    {
        if (row is null) return;
        if (!_isConnected()) { CloudStatusIsError = true; CloudStatus = "Connect your GOG account (Library tab) first."; return; }
        var target = await _pickFolder($"Save \"{row.Title}\" cloud save into…", _backupRoot());
        if (string.IsNullOrEmpty(target)) return;
        await _runBusy($"Saving {row.Title} cloud save…", async () =>
        {
            var cloud = CloudServiceFor(row.AccountId);   // the saves belong to THIS row's account
            var n = await cloud.ExportSavesAsync(row.GogId, target);
            Dispatcher.UIThread.Post(() =>
            {
                CloudStatusIsError = n == 0;
                CloudStatus = n > 0
                    ? $"Saved {n} file(s) for {row.Title} to {target}"
                    : $"Couldn't save {row.Title}: GOG refused the cloud request (0 files).";
            });
        });
    }

    /// <summary>The status line for one row's Download, from what the run recorded.</summary>
    internal static string DownloadStatusText(string title, Grog.Core.Runs.CloudSaveEntryResult? entry)
    {
        if (entry is null) return $"Couldn't back up {title}: it is no longer listed with cloud saves. Check for new saves and try again.";
        if (entry.Error is { Length: > 0 } err) return $"Couldn't back up {title}: {err.TrimEnd('.', ' ')}. Not marked as backed up.";
        if (entry.FilesWritten <= 0) return $"Couldn't back up {title}: GOG refused the cloud request (0 files). Not marked as backed up.";
        if (entry.Partial) return $"Saved {entry.FilesWritten} of {entry.FilesExpected} file(s) for {title}; GOG refused the rest. It stays marked as needing a backup.";
        return $"Saved {entry.FilesWritten} file(s) for {title}";
    }

    [RelayCommand]
    private async Task DownloadCloudSave(CloudGameRow? row)
    {
        if (row is null) return;
        if (!_isConnected()) { CloudStatusIsError = true; CloudStatus = "Connect your GOG account (Library tab) first."; return; }
        await _runBusy($"Downloading cloud saves for {row.Title}…", async () =>
        {
            // (S2.2) Core's CloudSaveRun over THIS row's (game, account) archive: client id, dated local save,
            // retention, bookkeeping only when files were written. It logs the backed-up / FAILED line itself.
            var result = await NewCloudRun(new CloudRunHost(this, statusProgress: false))
                .DownloadAsync(Grog.Core.Runs.CloudSaveSelection.ForEntry(row.GogId, row.AccountId), _keepCloudSaves(),
                               System.Threading.CancellationToken.None, skipUpToDate: false);
            var entry = result.Entries.Count > 0 ? result.Entries[0] : null;
            var n = entry?.FilesWritten ?? 0;
            Dispatcher.UIThread.Post(() =>
            {
                CloudStatusIsError = n == 0 || entry is { Partial: true };
                // The run knows WHY (no client id, a network error, a refusal): say that. Every failure read
                // "GOG refused the cloud request" before, including ones GOG never saw (sweep 2 #25).
                CloudStatus = DownloadStatusText(row.Title, entry);
                if (n > 0) { _ = DiscoverAsync(); RefreshFromManifest(); }   // (UI-thread sweep 09-06 r2) DiscoverAsync catches its own failures
            });
        });
    }

    // ---- Download all : localSaves every game that needs one (new or changed), SKIPPING games
    //      already up to date. Cancellable between games.
    [ObservableProperty] private bool _cloudAllRunning;
    private System.Threading.CancellationTokenSource? _cloudAllCts;

    /// <summary>Games Download-all would save locally (never backed up, or changed since the last local
    /// save). Up-to-date games are skipped.</summary>
    public int CloudNeedCount => _allCloud.Count(g => !g.IsUpToDate);
    /// <summary>Rail row state: current games over games with cloud saves, the same shape as Library's
    /// fraction. Distinct games; a game is current only when EVERY account's saves for it are backed up.</summary>
    public string RailCloudCount
    {
        get
        {
            var byGame = _allCloud.GroupBy(g => g.GogId).ToList();
            if (byGame.Count == 0) return "";
            int current = byGame.Count(g => g.All(r => r.IsUpToDate));
            return $"{current}/{byGame.Count} Games";
        }
    }
    /// <summary>(UX 09-08 #6) Cloud rail second line: "{done} of {total} backed up", same figures as RailCloudCount.</summary>
    public string RailCloudSub
    {
        get
        {
            var byGame = _allCloud.GroupBy(g => g.GogId).ToList();
            if (byGame.Count == 0) return "no cloud saves yet";
            int current = byGame.Count(g => g.All(r => r.IsUpToDate));
            return $"{current} of {byGame.Count} backed up";
        }
    }
    public bool CanDownloadAll => CloudNeedCount > 0 && !CloudAllRunning;
    public string DownloadAllText => $"Download All ({CloudNeedCount})";

    partial void OnCloudAllRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownloadAll));
    }

    [RelayCommand]
    private async Task DownloadAllCloud()
    {
        if (!_isConnected()) { CloudStatusIsError = true; CloudStatus = "Connect your GOG account (Library tab) first."; return; }
        // Per-account archives: every (game, account) row that needs a backup gets one, each through its
        // owning account's session into its own folder -- co-saved games back up once PER ACCOUNT.
        if (CloudNeedCount == 0) { CloudStatus = "All cloud saves are already up to date."; return; }
        _cloudAllCts = new System.Threading.CancellationTokenSource();
        var ct = _cloudAllCts.Token;
        CloudAllRunning = true;
        await _runBusy("Backing up cloud saves…", async () =>
        {
            // (S2.2) Core's CloudSaveRun: every known archive, skipping the up-to-date ones (its rule is the
            // rows' rule), cancellable between games. Per-game outcome lines are the run's own.
            var result = await NewCloudRun(new CloudRunHost(this, statusProgress: true))
                .DownloadAsync(Grog.Core.Runs.CloudSaveSelection.All, _keepCloudSaves(), ct, skipUpToDate: true);
            int ok = result.Ok, failed = result.Failed;
            Dispatcher.UIThread.Post(() =>
            {
                CloudStatusIsError = failed > 0;
                CloudStatus = result.Canceled
                    ? $"Stopped - backed up {ok} game(s)."
                    : $"Backed up {ok} game(s)" + (failed > 0 ? $", {failed} failed." : ".");
                // (09-19) The run's END goes to the log too: a Stop left no trace there, only on the status line.
                LogCloud("Cloud saves: " + CloudStatus, failed > 0);
                RefreshFromManifest();
            });
        });
        CloudAllRunning = false;
        _cloudAllCts = null;
    }

    [RelayCommand]
    private void CancelDownloadAllCloud() => _cloudAllCts?.Cancel();

    // ---- LocalSave history: expand a game to its timestamped localSaves on disk, then open the folder,
    //      restore one into a chosen folder, or prune one (two-step confirm). Pure disk work, no GOG call. ----

    /// <summary>After a rebuild (e.g. a prune reruns RefreshFromManifest), re-open this game so the panel
    /// doesn't collapse under the user. Cleared once honored.</summary>
    private long? _expandAfterRefresh;

    [RelayCommand]
    private void ToggleLocalSaves(CloudGameRow? row)
    {
        if (row is null || !row.HasBackup) return;
        row.IsExpanded = !row.IsExpanded;
        if (row.IsExpanded) LoadLocalSaves(row);
    }

    /// <summary>(UI-thread sweep 09-06 r2) the archive dir resolution (BackupLayout: probes every root) and the
    /// directory walk run on a worker; rows are posted back only if this row is still expanded and still part
    /// of the current list (a refresh rebuilds rows; a stale walk must not populate a dead row).</summary>
    private void LoadLocalSaves(CloudGameRow row)
    {
        row.LocalSaves.Clear();
        var manifest = _manifest();
        var backupRoot = _backupRoot();
        if (manifest is null) { row.LocalSavesLoaded = true; return; }
        var current = manifest.Current;
        var item = current.ItemById(row.GogId);
        var slug = string.IsNullOrEmpty(item?.Slug) ? row.Title : item!.Slug;
        _ = Task.Run(() =>
        {
            List<(string Dir, DateTimeOffset StampUtc, int FileCount, long SizeBytes)> found = new();
            try
            {
                var dir = ResolveGameDir(current, backupRoot, row.AccountId, slug);
                foreach (var s in Grog.Core.CloudSaves.LocalSaveArchive.List(dir))
                    found.Add((s.Dir, s.StampUtc, s.FileCount, s.SizeBytes));
            }
            catch { /* unreadable archive: show nothing rather than throw on a worker */ }
            Dispatcher.UIThread.Post(() =>
            {
                if (!row.IsExpanded || !_allCloud.Contains(row)) return;
                row.LocalSaves.Clear();
                foreach (var s in found)
                    row.LocalSaves.Add(new LocalSaveRow(s.Dir, s.StampUtc, s.FileCount, s.SizeBytes));
                row.LocalSavesLoaded = true;
            });
        });
    }

    /// <summary>This row's account's archive dir for this game, resolved exactly as the downloader resolves it.
    /// The tokens.json slot keeps the legacy per-game folder; named accounts write under accounts/&lt;id&gt;/.
    /// (UI-thread sweep 09-06 r2) pure function of captured inputs so it can run on a worker.</summary>
    private static string ResolveGameDir(Grog.Core.Manifest.LibraryManifest current, string backupRoot, string accountId, string slug)
    {
        string baseDir;
        try
        {
            var layout = new Grog.Core.Volumes.BackupLayout(current, backupRoot);
            baseDir = layout.ResolveCloudBaseDir(out _) ?? System.IO.Path.Combine(backupRoot, "Cloud Saves");
        }
        catch { baseDir = System.IO.Path.Combine(backupRoot, "Cloud Saves"); }
        // Account folder first (named for the account), game inside; the legacy flat layout stays
        // readable for the tokens.json slot's existing archives.
        return Grog.Core.CloudSaves.CloudSaveReconciler.GameArchiveDir(current, baseDir, accountId, slug);
    }

    [RelayCommand]
    private void OpenLocalSaveFolder(LocalSaveRow? snap)
    {
        if (snap is not null) Grog.App.Services.FileManager.Open(snap.Dir);
    }

    /// <summary>Restore a specific local save's files into a folder the user picks (e.g. the game's live save
    /// folder). Copies from disk; never overwrites or touches the local save itself.</summary>
    [RelayCommand]
    private async Task RestoreLocalSave(LocalSaveRow? snap)
    {
        if (snap is null) return;
        var target = await _pickFolder("Restore this local save into…", _backupRoot());
        if (string.IsNullOrEmpty(target)) return;
        await _runBusy("Restoring local save…", async () =>
        {
            int n;
            try { n = await Task.Run(() => Grog.Core.CloudSaves.LocalSaveArchive.RestoreTo(snap.Dir, target)); }
            catch { n = -1; }
            Dispatcher.UIThread.Post(() =>
            {
                CloudStatusIsError = n <= 0;
                CloudStatus = n > 0 ? $"Restored {n} file(s) to {target}"
                            : n == 0 ? "Nothing to restore - that local save is empty."
                            : "Couldn't restore that local save (a file may be in use).";
            });
        });
    }

    [RelayCommand]
    private void AskDeleteLocalSave(LocalSaveRow? snap) { if (snap is not null) snap.ConfirmingDelete = true; }

    [RelayCommand]
    private void CancelDeleteLocalSave(LocalSaveRow? snap) { if (snap is not null) snap.ConfirmingDelete = false; }

    /// <summary>Delete one local save folder for good (confirmed via the row's two-step gate), then re-sync
    /// the game's bookkeeping from what's actually left on disk so counts/summary stay honest.</summary>
    [RelayCommand]
    private async Task DeleteLocalSave(LocalSaveRow? snap)
    {
        if (snap is null) return;
        var parent = _allCloud.FirstOrDefault(g => g.LocalSaves.Contains(snap));
        // (UI-thread sweep 09-06 r2) the prune AND the re-list of what remains (archive dir resolution + directory
        // walk on the backup drive) run in one Task.Run; inputs are captured here and re-checked after.
        var manifest = _manifest();
        var backupRoot = _backupRoot();
        var current = manifest?.Current;
        var slug = parent is null ? "" : (string.IsNullOrEmpty(current?.ItemById(parent.GogId)?.Slug) ? parent.Title : current!.ItemById(parent.GogId)!.Slug);
        IReadOnlyList<Grog.Core.CloudSaves.LocalSaveArchive.LocalSave> remaining;
        try
        {
            remaining = await Task.Run(() =>
            {
                Grog.Core.CloudSaves.LocalSaveArchive.Prune(snap.Dir);
                if (parent is null || current is null) return (IReadOnlyList<Grog.Core.CloudSaves.LocalSaveArchive.LocalSave>)System.Array.Empty<Grog.Core.CloudSaves.LocalSaveArchive.LocalSave>();
                return Grog.Core.CloudSaves.LocalSaveArchive.List(ResolveGameDir(current, backupRoot, parent.AccountId, slug));
            });
        }
        catch
        {
            CloudStatusIsError = true;
            CloudStatus = "Couldn't delete that local save (a file may be in use).";
            return;
        }
        CloudStatusIsError = false;
        CloudStatus = "Local save deleted.";
        if (parent is null) return;
        if (!ReferenceEquals(_manifest(), manifest) || !ReferenceEquals(manifest?.Current, current) || _backupRoot() != backupRoot)
        { RefreshFromManifest(); return; }   // the world moved on: rebuild from the current manifest instead of stale counts

        // Re-derive this game's local save count / last-backup / backed-up size from disk reality.
        var item = current?.ItemById(parent.GogId);
        if (item is not null && manifest is not null)
        {
            // Per-account entry first, mirror second (the reconciler's single write path, the same call
            // CloudSaveRun.PruneAsync makes). Deleting an OLD local save doesn't change what the latest backup
            // captured; RecordLocalSaves owns the empty-history reset. (S2.2)
            using (manifest.Gate.Enter())   // (manifest gate 09-08)
                Grog.Core.CloudSaves.CloudSaveReconciler.RecordLocalSaves(
                    item, parent.AccountId, remaining.Count,
                    remaining.Count > 0 ? remaining[0].StampUtc : (DateTimeOffset?)null);
            await manifest.SaveAsync();
        }
        // Rebuild the rows so the (immutable) subtitle/badge reflect the new count, then re-open this game.
        _expandAfterRefresh = remaining.Count > 0 ? parent.GogId : (long?)null;
        RefreshFromManifest();
    }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.App.Runtime;

namespace Grog.App.ViewModels;

/// <summary>
/// The Settings page's view model (S3.4, 09-08): the accent theme, the launch checks and Grog's own update
/// row, the transfer settings (workers, auto pick, speed cap), old-version and cloud-save retention,
/// desktop notifications, the close action and tray pill, Fresh Library, and the extras layout with its
/// policy summary and folder preview (the Policy partial). The content scope, platforms, languages and view
/// toggles the page also shows are the Library's and bind it directly. The download run the transfer
/// settings feed (DownloadFiltered, the engine host) stays on the shell and reads <c>Xfer</c> here.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IExtrasPlacementHost
{
    private readonly MainWindowViewModel _root;
    private readonly GrogSession _session;

    public SettingsViewModel(MainWindowViewModel root, GrogSession session)
    {
        _root = root;
        _session = session;
        ExtrasCard = new ExtrasPlacementEditor(this);
    }

    /// <summary>The "Where extras are stored" card: staged shape + storage, committed together by its own
    /// Apply (09-11). The page owns ONE property here, not the card's state -- that is the whole point of it
    /// being a separate class.</summary>
    public ExtrasPlacementEditor ExtrasCard { get; }

    // ---- Session / sibling shorthands (same names as the root's, so the moved code reads unchanged) ----
    private LibraryViewModel _library => _root.Library;
    private JsonManifestStore _manifest => _session.Manifest;
    private AppSettings _settings => _session.Settings;
    private DownloadEngine? _liveEngine => _session.LiveEngine;
    private System.Net.Http.HttpClient _apiHttp => _session.ApiHttp;
    private static string VersionString => MainWindowViewModel.VersionString;
    private void Log(string message, bool isError = false, LogCategory category = LogCategory.General) => _root.Log(message, isError, category);

    /// <summary>The manifest is open (EnsureServicesAsync): migrate the pre-manifest app settings in and
    /// re-raise every manifest-backed binding the page evaluated before the store existed.</summary>
    internal void OnServicesReady()
    {
        MigrateKeepOldVersions(); OnPropertyChanged(nameof(KeepOldVersions));
        MigrateTransferSettings();   // (S2.3) worker count, cap, cloud retention: manifest facts now
        OnPropertyChanged(nameof(SettingMaxConcurrent)); OnPropertyChanged(nameof(SettingAutoConcurrency)); OnPropertyChanged(nameof(ConcurrencyBoxEnabled));
        OnPropertyChanged(nameof(SettingLimitDownloadSpeed)); OnPropertyChanged(nameof(SettingDownloadLimitKBps)); RaiseCloudKeep();
        ExtrasCard.ResetFromManifest();   // the card can read its committed state now
        RaisePolicyState();        // same for the other manifest-backed setting (ExtrasInsideGame / "How to organize extras")
    }

    /// <summary>The language summary or the scope moved on the Library: the one-line policy readout follows.</summary>
    internal void RaisePolicySummaryText() => OnPropertyChanged(nameof(PolicySummaryText));

    // Bindings evaluate before the store is open (owner-hit 09-08, NRE in CloudKeepAll at window creation):
    // until then the defaults answer, and OnServicesReady raises every transfer property once loaded.
    private static readonly Grog.Core.Manifest.TransferSettings _xferDefaults = new();
    /// <summary>Transfer settings live in the manifest (S2.3): worker count, cap, cloud retention. The shell's
    /// download run reads this when it builds the engine.</summary>
    internal Grog.Core.Manifest.TransferSettings Xfer => _manifest?.Current.Transfer ?? _xferDefaults;
    private Grog.Core.Manifest.TransferSettings _xfer => Xfer;
    private void SaveTransferSettings() => _manifest?.SaveSoon();   // settings fields: debounced across slider drags

    /// <summary>One-way, once: the App's transfer settings move into the manifest so the CLI honours them (S2.3).
    /// A profile that already carries the flag keeps whatever the manifest says.</summary>
    private void MigrateTransferSettings()
    {
        if (_manifest is null || _settings.TransferSettingsMigrated) return;
        var t = _manifest.Current.Transfer;
        t.MaxConcurrentDownloads = _settings.MaxConcurrentDownloads;
        t.AutoConcurrency = _settings.AutoConcurrency;
        t.DownloadLimitEnabled = _settings.DownloadLimitEnabled;
        t.DownloadLimitKBps = _settings.DownloadLimitKBps;
        t.KeepCloudSaves = _settings.KeepCloudSaves;
        _settings.TransferSettingsMigrated = true; _settings.SaveSoon();
        Runtime.ManifestSaves.Default.SaveInBackground(_manifest, "transfer settings");
    }

    // --- settings (bound in the Settings view; persist on change) ---
    public bool SettingCheckOnLaunch
    {
        get => _settings.CheckForUpdatesOnLaunch;
        set { _settings.CheckForUpdatesOnLaunch = value; _settings.SaveSoon(); OnPropertyChanged(); }
    }

    /// <summary>Grog's own tier-1 update check ("finds, never downloads", like the GOG row above it).</summary>
    public bool SettingCheckGrogUpdates
    {
        get => _settings.CheckGrogUpdatesOnLaunch;
        set { _settings.CheckGrogUpdatesOnLaunch = value; _settings.SaveSoon(); OnPropertyChanged(); }
    }

    /// <summary>Set once when the startup check finds a newer release; null is "nothing to say".</summary>
    public Grog.Core.Update.GrogUpdateCheck.Available? GrogUpdate { get; private set; }
    public bool HasGrogUpdate => GrogUpdate is not null;
    public string GrogUpdateText => GrogUpdate is { } u ? $"v{u.Version} available" : "";
    /// <summary>Version and action in one control; a separate text block did not fit the row at 1366.</summary>
    public string GrogUpdateButtonText => GrogUpdate is { } u ? $"View v{u.Version}" : "";

    [RelayCommand]
    private void OpenGrogRelease() { if (GrogUpdate is { } u) _root.OpenUrl(u.Url); }

    /// <summary>The row's reply line, shown ONLY after a MANUAL check answers "you're current" --
    /// the static description is gone (owner call 08-30), but a pressed button still owes a reply.</summary>
    public string GrogUpdateRowDesc => ShowGrogUpdateRowDesc
        ? $"You're on the latest version (v{VersionString})." : "";
    public bool ShowGrogUpdateRowDesc => _grogUpdateChecked && !HasGrogUpdate;
    private bool _grogUpdateChecked;

    /// <summary>The row's Check Now: same request as startup, but explicit -- so it answers either way,
    /// where the startup check stays silent on "nothing new". Works with the startup toggle OFF; the
    /// toggle governs the automatic check only.</summary>
    [RelayCommand]
    private async Task CheckGrogUpdatesNow()
    {
        var found = await Grog.Core.Update.GrogUpdateCheck.CheckAsync(_apiHttp, VersionString);
        _grogUpdateChecked = true;
        if (found is { } upd)
        {
            GrogUpdate = upd;
            Log($"Grog v{upd.Version} is available (you are on v{VersionString}).");
        }
        else Log($"Checked for a newer Grog: none found (you are on v{VersionString}).");   // (09-19) Check Now answers either way, in the log too
        OnPropertyChanged(nameof(GrogUpdateRowDesc)); OnPropertyChanged(nameof(ShowGrogUpdateRowDesc));
        OnPropertyChanged(nameof(GrogUpdateText)); OnPropertyChanged(nameof(GrogUpdateButtonText));
        OnPropertyChanged(nameof(HasGrogUpdate));
    }

    /// <summary>Render-harness seam: the update-found row state only exists after a live GitHub answer, and
    /// conditional states are exactly what a readability pass must render.</summary>
    internal void SeedGrogUpdate(string version, string url)
    {
        GrogUpdate = new Grog.Core.Update.GrogUpdateCheck.Available(version, url);
        OnPropertyChanged(nameof(GrogUpdateText)); OnPropertyChanged(nameof(GrogUpdateButtonText)); OnPropertyChanged(nameof(HasGrogUpdate));
    }
    public int SettingMaxConcurrent
    {
        get => _xfer.MaxConcurrentDownloads;
        set
        {
            _xfer.MaxConcurrentDownloads = Math.Clamp(value, 1, 5);
            SaveTransferSettings();
            if (!_xfer.AutoConcurrency)
                _liveEngine?.SetMaxConcurrent(_xfer.MaxConcurrentDownloads);   // live push: apply to a running download now
            OnPropertyChanged(); OnPropertyChanged(nameof(ConcurrencyDisplay));
        }
    }

    /// <summary>Auto checkbox on the same row. On: the advisor drives the engine (live on a running job)
    /// and the number box shows its current pick, grayed. Off: the saved number, always obeyed.</summary>
    public bool SettingAutoConcurrency
    {
        get => _xfer.AutoConcurrency;
        set
        {
            if (_xfer.AutoConcurrency == value) return;
            _xfer.AutoConcurrency = value;
            SaveTransferSettings();
            if (_liveEngine is { } eng)
            {
                if (value) ApplyAutoConcurrency(eng, force: true);
                else eng.SetMaxConcurrent(_xfer.MaxConcurrentDownloads);
            }
            OnPropertyChanged(); OnPropertyChanged(nameof(ConcurrencyDisplay)); OnPropertyChanged(nameof(ConcurrencyBoxEnabled));
        }
    }

    /// <summary>What the number box shows: the auto pick while Auto is on, else the saved number. Writes
    /// only land when Auto is off (the box is disabled otherwise, so this is belt and braces).</summary>
    public int ConcurrencyDisplay
    {
        get => _xfer.AutoConcurrency ? _autoPick : _xfer.MaxConcurrentDownloads;
        set { if (!_xfer.AutoConcurrency) SettingMaxConcurrent = value; }
    }
    public bool ConcurrencyBoxEnabled => !_xfer.AutoConcurrency;
    // Bandwidth cap: the engine value is derived in exactly one place (EffectiveBytesPerSecondLimit).
    // Off, or a non-positive rate, yields 0 = the engine's throttle path is skipped entirely, so
    // "disabled" can never mean "capped by a leftover number".
    public bool SettingLimitDownloadSpeed
    {
        get => _xfer.DownloadLimitEnabled;
        set
        {
            _xfer.DownloadLimitEnabled = value;
            SaveTransferSettings();
            PushSpeedLimit();   // live: applies to the in-flight file, not just the next one
            OnPropertyChanged();
        }
    }
    public int SettingDownloadLimitKBps
    {
        get => _xfer.DownloadLimitKBps;
        set
        {
            _xfer.DownloadLimitKBps = Math.Clamp(value, 32, 1_000_000);
            SaveTransferSettings();
            PushSpeedLimit();
            OnPropertyChanged();
        }
    }
    internal long EffectiveBytesPerSecondLimit =>
        _xfer.DownloadLimitEnabled && _xfer.DownloadLimitKBps > 0
            ? _xfer.DownloadLimitKBps * 1024L : 0;
    private void PushSpeedLimit()
    {
        if (_liveEngine is { } e) e.BytesPerSecondLimit = EffectiveBytesPerSecondLimit;
    }

    /// <summary>Asks GitHub whether a newer Grog exists and, if so, surfaces a quiet line: the Settings row
    /// grows a View button and the Activity log gets one entry. Finds, never downloads.</summary>
    internal async Task CheckGrogUpdateAsync()
    {
        var found = await Grog.Core.Update.GrogUpdateCheck.CheckAsync(_apiHttp, VersionString);
        if (found is not { } upd) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            GrogUpdate = upd;
            OnPropertyChanged(nameof(GrogUpdateText)); OnPropertyChanged(nameof(GrogUpdateButtonText));
            OnPropertyChanged(nameof(HasGrogUpdate));
            Log($"Grog v{upd.Version} is available (you are on v{VersionString}). Settings has the link.");
        });
    }

    // ---- Auto simultaneous downloads: advisor pick from the queue head, pushed live to the engine ----
    private int _autoPick = 2;
    /// <summary>Re-read the queue head and push the advisor's pick to the engine when it changed. Called at
    /// run start and after every transition pass (UI thread); no-op when Auto is off. <paramref name="force"/>
    /// pushes even an unchanged pick (turning Auto on mid-run must override the manual number).</summary>
    internal void ApplyAutoConcurrency(DownloadEngine engine, bool force = false)
    {
        if (!_xfer.AutoConcurrency) return;
        var live = engine.Snapshot.Where(t => t.State is DownloadTaskState.Pending or DownloadTaskState.Active).ToList();
        var head = live.Take(Grog.Core.Download.ConcurrencyAdvisor.HeadWindow)
                       .Select(t => t.BytesTotal ?? t.File.ExpectedSizeBytes ?? 0L).ToList();
        int pick = Grog.Core.Download.ConcurrencyAdvisor.Pick(head, live.Count);
        if (pick == _autoPick && !force) return;
        _autoPick = pick;
        engine.SetMaxConcurrent(pick, preempt: false);   // advisor changes never yank a running file
        OnPropertyChanged(nameof(ConcurrencyDisplay));
    }

    /// <summary>Opt-in retention: keep the build an update replaces, under "Old Versions/". NOT scope -- GOG
    /// offers no old versions, so it stays out of completeness; Folders still counts it as a content type.</summary>
    public bool KeepOldVersions
    {
        // The manifest owns the fact (it travels with the backup, so the CLI honours it); the app setting is
        // only the pre-09-02 home, kept in step so a downgrade still reads something sensible.
        get => _manifest?.Current.KeepOldVersions ?? _settings.KeepOldVersions;
        set
        {
            if (KeepOldVersions == value) return;
            _settings.KeepOldVersions = value;
            _settings.SaveSoon();
            if (_manifest is not null) { _manifest.Current.KeepOldVersions = value; _manifest.SaveSoon(); }
            if (_liveEngine is not null) _liveEngine.KeepOldVersions = value;
            OnPropertyChanged();
        }
    }

    /// <summary>One-way migration of the pre-09-02 app setting into the manifest, on every load (idempotent).</summary>
    private void MigrateKeepOldVersions()
    {
        if (_manifest is not null && _settings.KeepOldVersions && !_manifest.Current.KeepOldVersions)
        { _manifest.Current.KeepOldVersions = true; _manifest.SaveSoon(); }
    }

    // Cloud-save retention chooser (Settings). One int: 0 = keep all, 1 = latest only, N >= 2 = keep newest N.
    // Applies to future backups; flipping it never deletes existing history (rule 6).
    public bool CloudKeepAll    => _xfer.KeepCloudSaves == 0;
    public bool CloudKeepLatest => _xfer.KeepCloudSaves == 1;
    public bool CloudKeepCount  => _xfer.KeepCloudSaves >= 2;

    /// <summary>The N shown/edited by the "Last N" stepper. Defaults to 5 when the mode isn't count, so picking
    /// "Last N" starts at a sensible value; editing it also switches the mode to count.</summary>
    public int CloudKeepCountValue
    {
        get => _xfer.KeepCloudSaves >= 2 ? _xfer.KeepCloudSaves : 5;
        set
        {
            var v = Math.Clamp(value, 2, 50);
            if (_xfer.KeepCloudSaves == v) return;
            _xfer.KeepCloudSaves = v;
            SaveTransferSettings();
            RaiseCloudKeep();
        }
    }

    [RelayCommand]
    private void SetCloudKeep(string mode)
    {
        int v = mode switch
        {
            "latest" => 1,
            "count"  => CloudKeepCountValue,   // 5 when coming from all/latest, else the current N
            _        => 0,                     // "all"
        };
        _xfer.KeepCloudSaves = v;
        SaveTransferSettings();
        RaiseCloudKeep();
    }

    private void RaiseCloudKeep()
    {
        OnPropertyChanged(nameof(CloudKeepAll));
        OnPropertyChanged(nameof(CloudKeepLatest));
        OnPropertyChanged(nameof(CloudKeepCount));
        OnPropertyChanged(nameof(CloudKeepCountValue));
    }

    /// <summary>Show desktop (OS) notifications when a background backup finishes or fails.</summary>
    public bool DesktopNotifications
    {
        get => _settings.DesktopNotifications;
        set
        {
            if (_settings.DesktopNotifications == value) return;
            _settings.DesktopNotifications = value;
            _settings.SaveSoon();
            Grog.App.Notifier.Enabled = value;
            OnPropertyChanged();
            // The two event options hang off this master switch (they dim in the UI when it's off).
            OnPropertyChanged(nameof(NotificationsEnabled));
        }
    }

    /// <summary>Master gate for the two event toggles below -- the UI dims + disables them when notifications
    /// are off, so it's clear the choices only apply while the master switch is on.</summary>
    public bool NotificationsEnabled => DesktopNotifications;

    /// <summary>Notify when a queued operation finishes cleanly (no failed files). Persisted.</summary>
    public bool NotifyOnComplete
    {
        get => _settings.NotifyOnComplete;
        set
        {
            if (_settings.NotifyOnComplete == value) return;
            _settings.NotifyOnComplete = value;
            _settings.SaveSoon();
            OnPropertyChanged();
        }
    }

    /// <summary>Notify when a run finishes with one or more failed files. Persisted.</summary>
    public bool NotifyOnErrors
    {
        get => _settings.NotifyOnErrors;
        set
        {
            if (_settings.NotifyOnErrors == value) return;
            _settings.NotifyOnErrors = value;
            _settings.SaveSoon();
            OnPropertyChanged();
        }
    }

    // ---- Tray vocabulary: macOS has no "system tray" - the icon lives in the MENU BAR, and the Dock
    // is a different thing entirely. Same feature, honest per-platform words (owner call, mac walk).
    public string TrayPillLabel => OperatingSystem.IsMacOS() ? "Keep Running in Menu Bar" : "Keep Running in Tray";
    public string TrayPillTip => OperatingSystem.IsMacOS()
        ? "Grog keeps running with a menu-bar icon; closing or minimizing hides the window to it."
        : "Grog keeps running with a system-tray icon; closing or minimizing hides the window to it.";
    /// <summary>The tray pill is only offered where the desktop actually has a tray.</summary>
    public bool TrayAvailable => TraySupport.IsLikelySupported();

    /// <summary>Close/minimize to the system tray instead of quitting, keeping the in-app scheduler alive with
    /// no visible window. Persisted; the window reads it to decide whether to hide or really close.</summary>
    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set
        {
            if (_settings.MinimizeToTray == value) return;
            _settings.MinimizeToTray = value;
            _settings.MinimizeToTrayChosen = true;   // an explicit toggle is a real choice
            _settings.SaveSoon();
            OnPropertyChanged();
        }
    }

    /// <summary>What the X button does. The dialog only ACTS on a click; only this decides, so a one-off
    /// click can never silently rewrite a persistent preference.</summary>
    public CloseWindowAction CloseAction
    {
        get => _settings.CloseAction;
        set
        {
            if (_settings.CloseAction == value) return;
            _settings.CloseAction = value;
            _settings.SaveSoon();
            OnPropertyChanged();
            RaiseCloseAction();
            // ONE control now governs the tray: picking the tray pill turns the icon on, anything else
            // turns it off (the close-confirm dialog's one-off Minimize forces the icon itself).
            MinimizeToTray = value == CloseWindowAction.MinimizeToTray && TraySupport.IsLikelySupported();
        }
    }

    public bool CloseAsksOn    => CloseAction == CloseWindowAction.Ask;
    public bool CloseTrayOn    => CloseAction == CloseWindowAction.MinimizeToTray;
    public bool CloseQuitsOn   => CloseAction == CloseWindowAction.Quit;

    private void RaiseCloseAction()
    {
        OnPropertyChanged(nameof(CloseAsksOn)); OnPropertyChanged(nameof(CloseTrayOn)); OnPropertyChanged(nameof(CloseQuitsOn));
    }

    [RelayCommand]
    private void SetCloseAction(string which) => CloseAction = which switch
    {
        "tray" => CloseWindowAction.MinimizeToTray,
        "quit" => CloseWindowAction.Quit,
        _      => CloseWindowAction.Ask,
    };

    // ---- Accent theme (Settings) ----
    public bool ThemeIsDefault => _settings.Theme == GrogTheme.Default;
    public bool ThemeIsGog => _settings.Theme == GrogTheme.OdeToGog;
    public bool ThemeIsTavern => _settings.Theme == GrogTheme.Tavern;
    /// <summary>Switch the theme: re-tints the whole UI live (no restart) and persists the choice.</summary>
    [RelayCommand]
    private void SetTheme(string? which)
    {
        var theme = which?.ToLowerInvariant() switch
        {
            "gog" => GrogTheme.OdeToGog,
            "tavern" => GrogTheme.Tavern,
            _ => GrogTheme.Default,
        };
        if (_settings.Theme == theme) return;
        _settings.Theme = theme; _settings.SaveSoon();
        ThemeService.Apply(theme);
        // The Game chip's swatch is the shared accent brush; the computed fill/text must re-read the new color.
        foreach (var c in _library.AllChips) c.RaiseColors();
        OnPropertyChanged(nameof(ThemeIsDefault));
        OnPropertyChanged(nameof(ThemeIsGog));
        OnPropertyChanged(nameof(ThemeIsTavern));
    }

    // ---- Start with a fresh library (Settings card): untrack EVERYTHING, keep everything else ----
    // Items + queue only. Accounts, settings, storage roots and every byte on disk stay; the next scan
    // rebuilds the library and re-adopts downloaded files. Typed RESET gate: it empties the whole grid.
    [ObservableProperty] private bool _showFreshLibraryConfirm;
    [ObservableProperty] private string _freshTypedConfirm = "";
    public bool FreshLibraryConfirmed => string.Equals(FreshTypedConfirm.Trim(), "RESET", StringComparison.Ordinal);
    partial void OnFreshTypedConfirmChanged(string value) => OnPropertyChanged(nameof(FreshLibraryConfirmed));

    public string FreshLibraryWarningText
    {
        get
        {
            var text = $"All {_manifest?.Current.Items.Count ?? 0} games leave the library and the download "
                + "queue empties. Accounts, settings, storage and every downloaded file stay exactly where "
                + "they are; the next scan rebuilds the library and re-adopts what is already on disk.";
            // Delisted titles exist ONLY in this manifest: GOG no longer lists them, so no scan can
            // re-add them. Their files stay on disk, but tracking, serial keys and version records go.
            int delisted = _manifest?.Current.Items.Count(i => i.IsDelisted) ?? 0;
            if (delisted > 0)
                text += $" WARNING: {delisted} delisted game{(delisted == 1 ? "" : "s")} (no longer sold by GOG) "
                      + $"can NEVER be re-added by a scan - {(delisted == 1 ? "its" : "their")} downloaded files "
                      + "stay on disk but Grog stops tracking them, including serial keys.";
            return text;
        }
    }

    [RelayCommand]
    private void StartFreshLibrary()
    {
        if (_manifest is null || _manifest.Current.Items.Count == 0) { _root.StatusText = "The library is already empty."; return; }
        FreshTypedConfirm = "";
        OnPropertyChanged(nameof(FreshLibraryWarningText));
        ShowFreshLibraryConfirm = true;
    }

    [RelayCommand]
    private void CancelFreshLibrary() { ShowFreshLibraryConfirm = false; FreshTypedConfirm = ""; }

    [RelayCommand]
    private async Task ConfirmFreshLibrary()
    {
        if (!FreshLibraryConfirmed || _manifest is null) return;
        ShowFreshLibraryConfirm = false;
        FreshTypedConfirm = "";
        try { _root.CancelDownloads(); } catch { }   // stop the run + clear engine state before the wipe
        _root.StopSizing();
        try { await _root.Storage.StopMoveForResetAsync(); } catch { }   // a running move would stamp records the wipe is about to drop (QA 09-30 B13)
        int n = _manifest.Read(m => Grog.Core.Sync.LibraryUntrack.RemoveAllItems(m));   // (manifest gate 09-08)

        _library.ClearSelection();
        // Hidden ids name games that no longer exist; a stale id would silently re-hide a title the
        // moment the rebuild scan brings it back -- first-run means first-run.
        _library.ClearHiddenIds();
        // Every library-derived readout starts over too: the product count behind "N non-downloadable",
        // the last scan's error/account lines, and the last run's failure (a run against games that no
        // longer exist). Content chips, mature filter and legacy-movies stay: they are VIEW preferences,
        // and the confirm text says so. Back to the never-scanned state too, or the bar reads "Library
        // Backed Up" over an empty grid (0 items = 0 gap; CatalogScanned was this session's). Owner-hit 09-01.
        _library.ResetScanRecord();
        _root.Overview.ClearRunJournalFailure();
        _settings.SaveSoon();
        await _manifest.SaveAsync();
        _root.RefreshFromManifest();
        _root.Storage.RefreshBackupLocations();
        _root.RaiseBarState();
        _root.StatusText = "Library reset. Run a scan to rebuild it.";
        Log($"library reset: {n} game(s) removed from tracking (accounts, settings and downloaded files kept)");
    }

    // ---- Extras folder layout (Settings "Where Extras are stored" card) ----
    // The null-manifest fallback must agree with LibraryManifest.ExtrasLayout's initializer (WithGame).
    /// <summary>The COMMITTED extras shape, for the policy summary and the "what is in effect" readouts.
    /// Read-only since 09-11: writing it used to commit on every pill click and kick a migration, which is
    /// exactly the behaviour the staged card replaced. The card writes through ExtrasPlacementRun.</summary>
    public Grog.Core.Models.ExtrasPlacement ExtrasLayoutValue
        => _manifest?.Current.ExtrasLayout ?? Grog.Core.Models.ExtrasPlacement.WithGame;
}

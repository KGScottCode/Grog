// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Grog.App;

public enum CloseWindowAction
{
    Ask,
    MinimizeToTray,
    Quit,
}

public sealed class AppSettings
{
    // --- persisted fields ---
    public int Schema { get; set; } = 1;
    public string? ChosenRoot { get; set; }
    public bool IncludeExtras { get; set; } = true;
    public bool IncludeGames { get; set; } = true;
    public List<string> Languages { get; set; } = new();
    public bool LanguagesChosen { get; set; }

    public List<string>? ExtraLanguages { get; set; }
    public bool ExtraLanguagesChosen { get; set; }

    public List<string> Platforms { get; set; } = new();
    public bool PlatformsChosen { get; set; }

    public bool KeepOldVersions { get; set; }

    public int KeepCloudSaves { get; set; }
    public bool TransferSettingsMigrated { get; set; }

    public bool CheckForUpdatesOnLaunch { get; set; }
    public bool CheckGrogUpdatesOnLaunch { get; set; } = true;
    public int MaxConcurrentDownloads { get; set; } = 2;

    public bool AutoConcurrency { get; set; } = true;

    public string FirstUnattendedRunSummary { get; set; } = "";
    public bool FirstUnattendedRunNoticed { get; set; }

    public bool SystemDriveHintDismissed { get; set; }

    public bool ConcurrencyDefaultMigrated { get; set; }
    public bool DownloadLimitEnabled { get; set; }
    public int DownloadLimitKBps { get; set; } = 1024;
    public int LastProductsSeen { get; set; }

    public bool ShowDetailArt { get; set; } = true;
    public bool ShowGridArt { get; set; } = true;
    public bool ShowLegacyMovies { get; set; }
    public System.Collections.Generic.List<string> DeselectedContentChips { get; set; } = new();
    public List<long> HiddenIds { get; set; } = new();

    public bool HideMatureContent { get; set; }

    public List<long> MatureKeptIds { get; set; } = new();
    public int MatureAgeThreshold { get; set; } = 18;

    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int? WindowPosX { get; set; }
    public int? WindowPosY { get; set; }
    public bool WindowMaximized { get; set; }
    public double[]? GridColumnWidths { get; set; }
    public int GridColumnLayoutVersion { get; set; }

    public bool RailCollapsed { get; set; }

    public bool FirstRunGuideDone { get; set; }

    public bool WelcomeShown { get; set; }

    public bool DefaultRootAutoCreated { get; set; }

    public List<string> GuideAcked { get; set; } = new();

    public int GuidePosition { get; set; }

    public bool SuppressMarkNotBackedUpWarning { get; set; }

    public bool SuppressRemoveAccountConfirm { get; set; }

    public bool SuppressRemoveWontFitConfirm { get; set; }

    public bool SuppressDeleteConfirm { get; set; }

    public double RailExpandedWidth { get; set; } = 205;   // RailWidth clamps up to the expanded minimum

    public double ActivityTopHeight { get; set; } = 192;

    public bool MinimizeToTray { get; set; }
    public string LibrarySortPath { get; set; } = "Title";
    public bool LibrarySortDescending { get; set; }
    public bool MinimizeToTrayChosen { get; set; }

    public CloseWindowAction CloseAction { get; set; } = CloseWindowAction.Ask;

    public bool ShowOverduePrompt { get; set; } = true;

    public bool DesktopNotifications { get; set; } = true;

    public bool NotifyOnComplete { get; set; } = true;
    public bool NotifyOnErrors { get; set; } = true;

    public GrogTheme Theme { get; set; } = GrogTheme.Default;


    // --- persistence ---
    private static string SettingsPath =>
        Path.Combine(Grog.Core.Storage.GrogPaths.ResolveConfigDir(), Grog.Core.Storage.GrogPaths.SettingsFileName);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool LoadedFromFallback { get; private set; }

    public static AppSettings Load()
    {
        var live = TryLoad(SettingsPath);
        var s = live ?? TryLoad(SettingsPath + ".bak") ?? new AppSettings();
        s.LoadedFromFallback = live is null && File.Exists(SettingsPath);
        s.MaxConcurrentDownloads = Math.Clamp(s.MaxConcurrentDownloads, 1, 5);
        s.DownloadLimitKBps = Math.Clamp(s.DownloadLimitKBps, 32, 1_000_000);

        if (!s.ConcurrencyDefaultMigrated)
        {
            if (s.MaxConcurrentDownloads == 1) s.MaxConcurrentDownloads = 2;
            s.ConcurrencyDefaultMigrated = true;   // persists with the next real save; Load itself never writes
        }
        return s;
    }

    private static AppSettings? TryLoad(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
        }
        catch { /* corrupt/partial -- caller tries the next source */ }
        return null;
    }

    private static readonly object _ioLock = new();
    private static readonly object _pendingLock = new();
    private static (string Json, AppSettings Owner, long Seq)? _pending;
    private static long _seq, _written;
    private static System.Threading.Timer? _pendingTimer;
    private const int SaveSoonDelayMs = 500;

    private string Snapshot() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public void Save()
    {
        string json;
        try { json = Snapshot(); } catch { return; }
        long seq = System.Threading.Interlocked.Increment(ref _seq);
        lock (_pendingLock) { _pending = null; }
        WriteJson(json, this, seq);
    }

    public void SaveSoon()
    {
        string json;
        try { json = Snapshot(); } catch { return; }
        long seq = System.Threading.Interlocked.Increment(ref _seq);
        lock (_pendingLock)
        {
            _pending = (json, this, seq);
            _pendingTimer ??= new System.Threading.Timer(_ => FlushPendingSave(), null,
                System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            try { _pendingTimer.Change(SaveSoonDelayMs, System.Threading.Timeout.Infinite); } catch (ObjectDisposedException) { }
        }
    }

    public static void FlushPendingSave()
    {
        (string Json, AppSettings Owner, long Seq)? take;
        lock (_pendingLock)
        {
            take = _pending; _pending = null;
            try { _pendingTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); } catch (ObjectDisposedException) { }
        }
        if (take is { } t) WriteJson(t.Json, t.Owner, t.Seq);
    }

    public static Action<string>? Log { get; set; }
    public static string? LastSaveError { get; private set; }
    private static string? _lastLoggedSaveError;

    private static void WriteJson(string json, AppSettings owner, long seq)
    {
        lock (_ioLock)
        {
            if (seq < _written) return;   // a newer snapshot already landed
            _written = seq;
            try
            {
                var path = SettingsPath;
                var tmp = path + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }
                if (File.Exists(path) && !owner.LoadedFromFallback)
                {
                    try { File.Copy(path, path + ".bak", overwrite: true); } catch { /* best-effort */ }
                }
                File.Move(tmp, path, overwrite: true);
                owner.LoadedFromFallback = false;   // the live file is good again from here on
                LastSaveError = null;
            }
            catch (Exception ex)
            {
                LastSaveError = ex.Message;
                if (ex.Message != _lastLoggedSaveError)
                {
                    _lastLoggedSaveError = ex.Message;
                    try { Log?.Invoke($"Settings could not be saved: {ex.Message}"); } catch { }
                }
            }
        }
    }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Grog.App;

/// <summary>What the window's X button does.</summary>
public enum CloseWindowAction
{
    /// <summary>Ask, so the cost of closing (scheduled backups stop) is stated before it happens.</summary>
    Ask,
    /// <summary>Keep running in the tray. Scheduled backups continue.</summary>
    MinimizeToTray,
    /// <summary>Quit. In-flight work pauses and the schedule stops until Grog is reopened.</summary>
    Quit,
}

/// <summary>Per-machine preferences stored in the OS per-user config dir (outside any backup root), resolved
/// via <see cref="Grog.Core.Storage.GrogPaths.ResolveConfigDir"/> so all of Grog's state sits in one folder.</summary>
public sealed class AppSettings
{
    // --- persisted fields ---
    /// <summary>Settings shape version (09-08). Migrations key off this instead of ad-hoc flags from here on.</summary>
    public int Schema { get; set; } = 1;
    public string? ChosenRoot { get; set; }
    // LEGACY: the backup-scope block (IncludeExtras/IncludeGames/Languages/ExtraLanguages/Platforms + *Chosen)
    // only seeds a pre-scope manifest's first load; the manifest (LibraryManifest.Scope) owns scope.
    /// <summary>Include extra files (soundtracks, manuals, wallpapers) in backups by default.</summary>
    public bool IncludeExtras { get; set; } = true;
    public bool IncludeGames { get; set; } = true;   // games (installers/patches/DLC); with IncludeExtras defines scope
    /// <summary>Languages to back up. Empty = every language -- never "English"; the app makes a real
    /// first-run choice instead (see LanguagesChosen), so nothing is ever dropped by accident.</summary>
    public List<string> Languages { get; set; } = new();
    /// <summary>True once the user (or first-run seeding) has decided; distinguishes "wants everything"
    /// from "hasn't been asked".</summary>
    public bool LanguagesChosen { get; set; }

    /// <summary>Languages to back up FOR EXTRAS. NULL means "same as Languages", NOT "all" -- widening is a
    /// deliberate opt-in; an EMPTY list still means every language, matching the Languages contract.</summary>
    public List<string>? ExtraLanguages { get; set; }
    /// <summary>True once extras have been given their OWN pick, as opposed to still following games.</summary>
    public bool ExtraLanguagesChosen { get; set; }

    /// <summary>Platforms to back up ("windows"/"mac"/"linux"). Empty = every platform; PlatformsChosen lets
    /// first run seed the machine's own OS without overriding a real choice.</summary>
    public List<string> Platforms { get; set; } = new();
    public bool PlatformsChosen { get; set; }

    /// <summary>Keep the superseded build under "Old Versions/" when an update replaces it. Off by default;
    /// on, it holds the only copy of a build GOG no longer offers.</summary>
    public bool KeepOldVersions { get; set; }

    /// <summary>Cloud-save snapshots retained per game: 0 = keep all (default), N = keep the newest N.
    /// Governs FUTURE backups only -- lowering it never deletes existing history.</summary>
    public int KeepCloudSaves { get; set; }
    /// <summary>(S2.3) MaxConcurrentDownloads / AutoConcurrency / DownloadLimit* / KeepCloudSaves were copied
    /// into the manifest once; the fields above stay only so an old profile can be read for that copy.</summary>
    public bool TransferSettingsMigrated { get; set; }

    /// <summary>Check GOG for updates on launch (off by default -- no surprise network/disk on open).</summary>
    public bool CheckForUpdatesOnLaunch { get; set; }
    /// <summary>Check GitHub for a newer GROG on launch. ON by default:
    /// one anonymous request, finds and never downloads, silent on any failure -- unlike the GOG check it
    /// touches no user data and costs no disk.</summary>
    public bool CheckGrogUpdatesOnLaunch { get; set; } = true;
    /// <summary>Max concurrent downloads. Kept modest -- Grog is a guest on GOG's servers.</summary>
    // Default 2 (owner call 09-01): on strings of tiny files the per-file round-trips dominate, and a
    // second worker overlaps them; on big files two streams just share the pipe, which is harmless.
    public int MaxConcurrentDownloads { get; set; } = 2;

    /// <summary>Auto simultaneous downloads (default ON, owner call 09-01): the engine's worker count
    /// follows ConcurrencyAdvisor's read of the queue head, re-evaluated per completion. Off = the
    /// number above is obeyed exactly.</summary>
    public bool AutoConcurrency { get; set; } = true;

    /// <summary>Morning-after notice (PO, 09-01): the first backup that ran UNATTENDED (a scheduled slot,
    /// no human watching) leaves a one-line account of what it did on the Overview until dismissed. Empty
    /// = nothing to show; Noticed = it has fired once and never will again.</summary>
    public string FirstUnattendedRunSummary { get; set; } = "";
    public bool FirstUnattendedRunNoticed { get; set; }

    /// <summary>Storage-page hint dismissed: "your primary storage is the system drive and the library
    /// won't fit". Dismiss = the user has a plan; never nag again.</summary>
    public bool SystemDriveHintDismissed { get; set; }

    /// <summary>One-time marker for the 1->2 concurrency default change: a profile saved before it
    /// carries the OLD default (1) indistinguishably from a deliberate 1, so Load bumps 1->2 exactly
    /// once and sets this; a 1 chosen after that stays 1 forever.</summary>
    public bool ConcurrencyDefaultMigrated { get; set; }
    /// <summary>Bandwidth cap toggle + rate. The cap applies ONLY when enabled AND the rate is positive;
    /// disabled always means uncapped (the engine gets a literal 0), never a stale or implicit limit.</summary>
    public bool DownloadLimitEnabled { get; set; }
    public int DownloadLimitKBps { get; set; } = 1024;
    /// <summary>Raw owned-product count from the last sync (GOG's total incl. non-game items), so the
    /// composition line can show on launch before any sync this session.</summary>
    public int LastProductsSeen { get; set; }

    /// <summary>Show the landscape logo in the detail pane / the portrait cover in the grid column.</summary>
    public bool ShowDetailArt { get; set; } = true;
    public bool ShowGridArt { get; set; } = true;
    /// <summary>Show GOG's legacy "Movie" products in the grid. Default off.</summary>
    public bool ShowLegacyMovies { get; set; }
    /// <summary>Content filter chips currently OFF. Empty = all selected = show everything (default).</summary>
    public System.Collections.Generic.List<string> DeselectedContentChips { get; set; } = new();
    /// <summary>GogIds hidden from the grid. Hiding conceals names, never bytes -- hidden items stay backed up;
    /// the blacklist is the separate "don't download" mechanism. The reveal toggle is never persisted.</summary>
    public List<long> HiddenIds { get; set; } = new();

    // --- content rating filter (parental discretion) ---
    /// <summary>Hide games whose effective age rating is at/above <see cref="MatureAgeThreshold"/>.
    /// Persistent policy, not a transient filter.</summary>
    public bool HideMatureContent { get; set; }

    /// <summary>(09-26, owner) Games the user chose to Keep visible in the mature review; they are exempt from
    /// <see cref="HideMatureContent"/> only (a per-game Hide still applies).</summary>
    public List<long> MatureKeptIds { get; set; } = new();
    /// <summary>Age cutoff: a game hides when GOG's own rating is >= this (the other boards are shown, never
    /// counted). Default 18.</summary>
    public int MatureAgeThreshold { get; set; } = 18;

    // --- window / grid layout ---
    /// <summary>Restored window width; null = design default.</summary>
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    /// <summary>Restored window position; null = OS placement. Applied only when the point still lies on
    /// a connected screen, so the window cannot reopen off-desktop after a monitor change.</summary>
    public int? WindowPosX { get; set; }
    public int? WindowPosY { get; set; }
    /// <summary>Reopen maximized when the app was closed maximized.</summary>
    public bool WindowMaximized { get; set; }
    /// <summary>Per-column pixel widths in grid order; -1 marks a star column that stays star on restore.
    /// Length-checked against the live column count before applying.</summary>
    public double[]? GridColumnWidths { get; set; }
    /// <summary>Schema version of the saved column layout; bump the CURRENT constant whenever a column changes
    /// shape or width kind (fixed &lt;-&gt; star). Widths from a different version are DISCARDED, or a stale
    /// pixel width overrides the new XAML forever.</summary>
    public int GridColumnLayoutVersion { get; set; }

    public bool RailCollapsed { get; set; }

    /// <summary>The first-run guide has been finished or dismissed. Re-runnable from Settings.</summary>
    public bool FirstRunGuideDone { get; set; }

    /// <summary>The welcome card has been shown once and must never fire twice. Separate from
    /// <see cref="FirstRunGuideDone"/>: a restart mid-tour resumes the guide without re-showing the welcome.</summary>
    public bool WelcomeShown { get; set; }

    /// <summary>Grog auto-created the default backup folder and nothing has been written to it; it is the
    /// ONLY folder Grog may tidy up when the user picks somewhere else. Any other folder is theirs and stays.</summary>
    public bool DefaultRootAutoCreated { get; set; }

    /// <summary>Guide steps acknowledged with Next. MUST persist: optional steps have no backing fact, so an
    /// in-memory set would rewind the tour to an already-handled step after a restart.</summary>
    public List<string> GuideAcked { get; set; } = new();

    /// <summary>Which stop of the tour the user is on. The tour is one ordered list walked by a cursor, so
    /// this is all there is to remember: closing the app mid-setup resumes on the same stop rather than
    /// re-deriving one. Replaced the old trail, which existed only because position was recomputed.</summary>
    public int GuidePosition { get; set; }

    /// <summary>"Don't ask again" for the mark-not-backed-up-with-local-copy warning. A preference about a
    /// prompt, not the backup, so it lives here rather than in the manifest.</summary>
    public bool SuppressMarkNotBackedUpWarning { get; set; }

    /// <summary>"Don't show this again" for the remove-account confirm (which also names the sole-owned
    /// games leaving the library). Same store as the mark warning: a prompt preference, not a backup fact.</summary>
    public bool SuppressRemoveAccountConfirm { get; set; }

    /// <summary>"Don't ask again" for the banner's "Remove them from the queue" (won't-fit rows, 09-11). A
    /// prompt preference, so it lives here, per machine.</summary>
    public bool SuppressRemoveWontFitConfirm { get; set; }

    /// <summary>"Don't ask again" for Delete downloaded files (right-click, 09-11). Per machine.</summary>
    public bool SuppressDeleteConfirm { get; set; }

    /// <summary>User-chosen width (px) of the EXPANDED rail, set by dragging the splitter; the collapsed rail
    /// is a fixed icon strip. Clamped to a sane range on apply.</summary>
    public double RailExpandedWidth { get; set; } = 205;   // RailWidth clamps up to the expanded minimum

    /// <summary>Height (px) of the MOVING pane in Overview's Activity column, above the handle splitting it
    /// from DOWNLOADING. Default aligns the handle with the SCHEDULE card top; clamped on apply so a stale
    /// value can never hide a pane.</summary>
    public double ActivityTopHeight { get; set; } = 192;

    /// <summary>Close/minimize to the system tray so the in-app scheduler keeps running with no window.
    /// Opt-in (default off): needs a tray, which bare GNOME lacks.</summary>
    public bool MinimizeToTray { get; set; }
    /// <summary>(09-22) The Library grid's sort, kept across restarts: the column's SortMemberPath and direction.</summary>
    public string LibrarySortPath { get; set; } = "Title";
    public bool LibrarySortDescending { get; set; }
    /// <summary>True once the Minimize-to-tray default has been applied or the user touched the toggle, so
    /// first-run seeding never overrides a later user choice.</summary>
    public bool MinimizeToTrayChosen { get; set; }

    /// <summary>What the window's X button does. One three-way answer, never a toggle plus a suppression
    /// checkbox; Ask stays the default because closing also stops scheduled backups.</summary>
    public CloseWindowAction CloseAction { get; set; } = CloseWindowAction.Ask;

    /// <summary>Show the "Backup overdue" banner on launch when a scheduled slot passed while Grog was closed.
    /// On by default; the banner's "Don't show this again" checkbox turns it off.</summary>
    public bool ShowOverduePrompt { get; set; } = true;

    /// <summary>Master switch for desktop notifications; off silences both event options below. On by
    /// default; falls back to the in-app toast where the OS has no backend.</summary>
    public bool DesktopNotifications { get; set; } = true;

    /// <summary>Notify when a queued operation finishes with no errors. On by default.</summary>
    public bool NotifyOnComplete { get; set; } = true;
    /// <summary>Notify when a run finishes with one or more failed files. On by default.</summary>
    public bool NotifyOnErrors { get; set; } = true;

    /// <summary>Accent theme: Default (amber) or OdeToGog (purple). Applied at startup.</summary>
    public GrogTheme Theme { get; set; } = GrogTheme.Default;

    // Pause state does NOT live here: it belongs to the download queue and persists in the manifest
    // (LibraryManifest.Downloads.Paused), traveling with the backup folder.

    // --- persistence ---
    // GrogPaths is the ONLY resolver for the state folder; computing the path here would fork it
    // under GROG_CONFIG_DIR.
    private static string SettingsPath =>
        Path.Combine(Grog.Core.Storage.GrogPaths.ResolveConfigDir(), Grog.Core.Storage.GrogPaths.SettingsFileName);

    /// <summary>True when Load() could not read the live file and fell back to the .bak or defaults. Save()
    /// then leaves the .bak alone: copying a corrupt live file over the last-known-good copy on the very
    /// next save would erase the only recovery source.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LoadedFromFallback { get; private set; }

    public static AppSettings Load()
    {
        // Live file, then last-known-good .bak, then defaults -- a truncated write must not wipe preferences.
        var live = TryLoad(SettingsPath);
        var s = live ?? TryLoad(SettingsPath + ".bak") ?? new AppSettings();
        s.LoadedFromFallback = live is null && File.Exists(SettingsPath);
        // Normalize out-of-range persisted values so the rest of the app never re-clamps (UI clamps 1..5 too).
        s.MaxConcurrentDownloads = Math.Clamp(s.MaxConcurrentDownloads, 1, 5);
        s.DownloadLimitKBps = Math.Clamp(s.DownloadLimitKBps, 32, 1_000_000);
        // One-time 1->2 bump for pre-change profiles (owner call 09-01); see the marker's doc comment.
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

    // (UI-thread sweep 09-06) All file writes go through one static gate: the file is one even when two
    // AppSettings instances exist (the window's layout read-modify-write), so a debounced worker write and a
    // synchronous Save() can never interleave and tear the file.
    private static readonly object _ioLock = new();
    private static readonly object _pendingLock = new();
    private static (string Json, AppSettings Owner, long Seq)? _pending;
    private static long _seq, _written;
    private static System.Threading.Timer? _pendingTimer;
    private const int SaveSoonDelayMs = 500;

    private string Snapshot() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Synchronous save (ordinary toggles). Serializes on the caller's thread and writes under the
    /// IO lock; supersedes any pending debounced snapshot, which is older by construction.</summary>
    public void Save()
    {
        string json;
        try { json = Snapshot(); } catch { return; }
        // Under the IO lock end to end: a worker flush that had already taken its (older) snapshot could
        // otherwise write AFTER this one and win (verification 09-06). Sequence numbers settle it: a
        // snapshot only lands if nothing newer has been written.
        long seq = System.Threading.Interlocked.Increment(ref _seq);
        lock (_pendingLock) { _pending = null; }
        WriteJson(json, this, seq);
    }

    /// <summary>(UI-thread sweep 09-06) Debounced, off-thread save for high-frequency setters (splitter drags):
    /// a snapshot is serialized NOW on the caller's thread, so a later mutation cannot tear the file, and the
    /// newest snapshot within ~500 ms is written once on a worker thread.</summary>
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

    /// <summary>(UI-thread sweep 09-06) Write a pending debounced snapshot synchronously, if any. Called at
    /// shutdown and before any read-modify-write Load, so nothing queued is lost or read stale.</summary>
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

    /// <summary>Log sink for a failed save (wired by the shell); a failure is logged once per distinct message.</summary>
    public static Action<string>? Log { get; set; }
    /// <summary>The last save failure, or null once a save lands again.</summary>
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
                // Atomic write: temp file flushed to disk, previous good copy kept as .bak, then swap -- a crash or
                // disk-full can only damage the temp file, never the live settings.
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

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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Verify;
using Grog.Core.Format;

namespace Grog.App.ViewModels;

// Window shell: the collapsible rail, quit / tray hooks, the close-confirmation, notification dispatch and the
// rail status decorations. (The tray pill, close action and notification toggles moved to SettingsViewModel in S3.4.)
public partial class MainWindowViewModel
{
    public string TrayConfirmBody => OperatingSystem.IsMacOS() ? "Or keep it running quietly from the menu bar." : "Or keep it running quietly in the tray.";

    /// <summary>Short version tag for the rail's collapse bar (right of the chevrons).</summary>
    public string RailVersionText => $"v{VersionString}";

    /// <summary>Running from a portable copy (a GrogData folder beside the exe). Drives the rail's dim
    /// "portable" marker, so a portable and an installed copy are never mistaken for each other.</summary>
    public bool IsPortableCopy => Grog.Core.Storage.GrogPaths.PortableInstallDir() is not null;

    // ---- Collapsible rail (icons-only when collapsed) ----
    public bool RailCollapsed
    {
        get => _settings.RailCollapsed;
        set
        {
            if (_settings.RailCollapsed == value) return;
            _settings.RailCollapsed = value; _settings.SaveSoon();
            OnPropertyChanged();
            OnPropertyChanged(nameof(RailExpanded)); OnPropertyChanged(nameof(RailBarSpan));
            Overview.RaiseRailBadge();   // "10 issues" expanded, "10" on the icon rail
            OnPropertyChanged(nameof(RailWidth));
            OnPropertyChanged(nameof(RailMinWidth));
            OnPropertyChanged(nameof(RailMaxWidth));
        }
    }
    /// <summary>MOCK ONLY: drives the welcome artwork opacity from the render harness.</summary>
    [ObservableProperty] private double _mockBgOpacity = 0.25;

    public bool RailExpanded => !RailCollapsed;

    /// <summary>ASSISTANT-ONLY, for the render harness: force the widest readout the rail could ever carry
    /// so the minimum width can be judged against worst-case content.</summary>
    internal void MeasureRailWorstCase()
    {
        Overview.ForceRailWorstCase();
        OnPropertyChanged(nameof(ShowRailCounts));
    }

    /// <summary>Collapsed = a fixed 60px icon strip; expanded = the user's persisted width. The setter is the
    /// splitter's TwoWay write-back: records a drag only while expanded, clamps, persists.</summary>
    public Avalonia.Controls.GridLength RailWidth
    {
        get => new(RailCollapsed ? 60 : System.Math.Clamp(_settings.RailExpandedWidth, RailMinExpanded, RailMaxExpanded));
        set
        {
            // Ignore the collapsed state (fixed strip) and any non-pixel value the splitter might emit.
            if (RailCollapsed || !value.IsAbsolute) return;
            var px = System.Math.Clamp(value.Value, RailMinExpanded, RailMaxExpanded);
            if (System.Math.Abs(_settings.RailExpandedWidth - px) < 0.5) return;
            _settings.RailExpandedWidth = px; _settings.SaveSoon();   // (UI-thread sweep 09-06) per-drag-delta: debounced, off-thread
            OnPropertyChanged();
        }
    }

    /// <summary>Collapsed, the chevron takes the whole bottom strip (2 columns); expanded, it keeps its half
    /// and About owns the other.</summary>
    public int RailBarSpan => RailCollapsed ? 2 : 1;

    /// <summary>Height of the MOVING pane in Overview's Activity column; the setter is the drag handle's
    /// TwoWay write-back, clamped, persisted. Default 192 (measured) aligns with the SCHEDULE card.</summary>
    public Avalonia.Controls.GridLength ActivityTopHeight
    {
        get => new(System.Math.Clamp(_settings.ActivityTopHeight, 96, 420));
        set
        {
            if (!value.IsAbsolute) return;
            var px = System.Math.Clamp(value.Value, 96, 420);
            if (System.Math.Abs(_settings.ActivityTopHeight - px) < 0.5) return;
            _settings.ActivityTopHeight = px; _settings.SaveSoon();   // (UI-thread sweep 09-06) per-drag-delta: debounced, off-thread
            OnPropertyChanged();
        }
    }

    // Expanded-rail bounds; collapsed pins to exactly 60 (min == max) so the splitter cannot nudge it.
    // Measured: "Cloud Saves" ink + worst-case "999/999 games" want ~248px; 260 gives air (GROG_SHOT_RAILWIDE=1).
    private const double RailMinExpanded = 260;
    private const double RailMaxExpanded = 360;
    public double RailMinWidth => RailCollapsed ? 60 : RailMinExpanded;
    public double RailMaxWidth => RailCollapsed ? 60 : RailMaxExpanded;

    [RelayCommand]
    private void ToggleRail() => RailCollapsed = !RailCollapsed;

    /// <summary>True once a real quit has been requested (Exit GROG / tray Quit), so the window's close-to-tray
    /// interceptor lets the close through instead of hiding to the tray.</summary>
    public bool Quitting { get; private set; }

    /// <summary>The OS asked the whole app to quit (Cmd+Q, Dock Quit, logout): mark Quitting so the
    /// window's Closing interceptor lets it through instead of asking close-vs-tray.</summary>
    public void NoteOsQuit() { Quitting = true; PauseAllForShutdown(); }

    /// <summary>Actually quit. Sets <see cref="Quitting"/> first so the tray interceptor stands down.</summary>
    /// <summary>Quit, but only after every file being written is closed and handed to the OS. Pausing cancels the
    /// workers; on a slow stick the process then sat minutes with the window gone while the handles drained and
    /// looked hung. So the window stays up with a "finishing writes" line until the engine and any move report
    /// every file closed, or the user presses Close now.</summary>
    public void RequestQuit() => _ = QuitAsync();

    /// <summary>A download or move still has a file open.</summary>
    public bool HasWritesInFlight => !(_liveEngine?.WhenStopped ?? Task.CompletedTask).IsCompleted || !Storage.WhenMoveStopped.IsCompleted;
    [ObservableProperty] private bool _showClosingWait;
    [ObservableProperty] private string _closingWaitText = "";
    [ObservableProperty] private bool _closingWaitCanForce;
    private TaskCompletionSource? _closeNow;
    [RelayCommand] private void CloseNow() => _closeNow?.TrySetResult();

    /// <summary>Raised when the closing wait begins, so a window hidden in the tray comes back to show it.</summary>
    public event Action? ClosingWaitShown;

    private async Task QuitAsync()
    {
        if (Quitting) return;
        Quitting = true;
        PauseAllForShutdown();   // every route: cancel the workers first, or the wait would be for the whole run
        var engine = _liveEngine?.WhenStopped ?? Task.CompletedTask;
        var move = Storage.WhenMoveStopped;
        if (!engine.IsCompleted || !move.IsCompleted)
        {
            var what = !move.IsCompleted ? "the move in progress" : "the files being downloaded";
            ClosingWaitText = $"Closing: finishing writes for {what}…";
            ClosingWaitCanForce = false;
            ShowClosingWait = true;
            ClosingWaitShown?.Invoke();
            _closeNow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = Task.WhenAll(engine, move);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!pending.IsCompleted && !_closeNow.Task.IsCompleted)
            {
                await Task.WhenAny(pending, _closeNow.Task, Task.Delay(500));
                if (sw.Elapsed > TimeSpan.FromSeconds(10) && !ClosingWaitCanForce)
                {
                    ClosingWaitCanForce = true;   // a dead drive must never trap a quit
                    ClosingWaitText = $"Still finishing writes for {what}… Close now loses only the file in progress.";
                }
            }
            Log(pending.IsCompleted ? "Closed after the last file in progress was handed to the drive." : "You closed before the last file finished; it resumes next time.");
            ShowClosingWait = false;
        }
        ShutdownNow();
    }

    private void ShutdownNow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
            // macOS: Shutdown() completes every managed step - Closing proceeds uncanceled, the call
            // returns - and the native window and run loop just carry on.
            // The failure is below the framework, so end the process honestly. Work is already parked:
            // every quit route calls PauseAllForShutdown before reaching here, and the queue + pause
            // flag live in the manifest, saved by those paths, so a hard exit loses nothing.
            if (OperatingSystem.IsMacOS())
                Environment.Exit(0);
        }
    }

    // ---- Close-confirmation (an X-close asks "Confirm Close or Minimize?") ----
    /// <summary>(09-09) Queue sort pills: the two-word labels fit on one line at 1920 and WRAP at 1366,
    /// and the wrapped second row overlaps the pane's Bottom-docked stats footer (a DockPanel does not clip,
    /// so a Top child that outgrows its share paints over the Bottom one). Shortening the labels below this
    /// width keeps the row to one line instead; the tooltips carry the full wording either way.</summary>
    public const double NarrowChromeWidth = 1500;
    [ObservableProperty] private bool _narrowChrome;
    partial void OnNarrowChromeChanged(bool value)
    {
        OnPropertyChanged(nameof(SortLabelInstallers)); OnPropertyChanged(nameof(SortLabelExtras));
    }
    // Kind is TWO pills, not one toggle (owner 09-10). A single pill has to name one end of the toggle, and
    // then says the opposite of what it does after the second click -- what made "Primary first" unreadable.
    // Two pills mean each caption is a promise about what pressing it does.
    public string SortLabelInstallers => NarrowChrome ? "Installers" : "Installers first";
    public string SortLabelExtras => NarrowChrome ? "Extras" : "Extras first";

    [ObservableProperty] private bool _showCloseConfirm;

    /// <summary>The dialog's "Remember my choice" tick, reset each open: written to <see cref="SettingsViewModel.CloseAction"/>
    /// only when a button is pressed with the box ticked, so Cancel never persists anything.</summary>
    [ObservableProperty] private bool _rememberCloseChoice;

    /// <summary>Pause any in-flight download and move so a close/quit never corrupts partial work. Safe to call
    /// when nothing is running (both pause primitives no-op on an empty queue).</summary>
    private bool _shutdownFlushed;
    public void PauseAllForShutdown()
    {
        if (_shutdownFlushed) return;   // every quit route calls this, and the close dialog's route calls it twice
        _shutdownFlushed = true;
        Schedule.EndScheduleCountdown();   // a countdown must not fire a backup into a closing process
        StopSizing();
        try { PauseDownloads(); } catch { }
        Storage.PauseReorgForShutdown();
        // PauseDownloads' own manifest save is fire-and-forget; block on one final bounded flush so shutdown
        // cannot lose the just-paused queue. Task.Run keeps it off the UI-thread sync context -> no deadlock.
        try { Task.Run(() => _manifest?.SaveAsync() ?? Task.CompletedTask).Wait(TimeSpan.FromSeconds(5)); }
        catch { /* best-effort: never block a quit on a failing disk */ }
        try { _logFile?.Flush(TimeSpan.FromSeconds(2)); _logFile?.Close(); } catch { }   // drain the background writer, then release the file
        try { AppSettings.FlushPendingSave(); } catch { }   // (UI-thread sweep 09-06) a debounced splitter save must not die with the process
    }

    /// <summary>Tray escalation levels for a notification the user might MISS (window hidden/minimized):
    /// the code-behind paints the tray icon by level and resets it on restore. 0 = informational.</summary>
    public const int TrayAttentionWarning = 1;
    public const int TrayAttentionProblem = 2;
    /// <summary>Raised with a notification's severity and short text; the window decides if it was missable
    /// (hidden/minimized), paints the tray, and replays via <see cref="ShowMissedNotices"/> on return.</summary>
    public event Action<int, string, ToastFix?>? TrayAttentionRequested;

    private void NotifyUser(string title, string body, bool isError = true, string? toastBody = null,
                            int? attention = null, ToastFix? fixNav = null)
    {
        // (09-19) Logged either way: a desktop notification leaves no trace in the app, so "did it fire?" had
        // no answer on a walk. The line says which path carried it.
        bool os = Grog.App.Notifier.Notify(title, body);
        if (!os) ShowToast(toastBody ?? body, isError ? 2 : 0, fixNav);
        Log($"Notification ({(os ? "desktop" : "in-app")}): {title}", isError: false);
        var level = attention ?? (isError ? TrayAttentionProblem : 0);
        if (level > 0) TrayAttentionRequested?.Invoke(level, toastBody ?? body, fixNav);
    }

    /// <summary>On restore the window hands back what was missed and it replays as a toast; worst severity
    /// wins the styling, the Activity log holds the detail.</summary>
    public void ShowMissedNotices(IReadOnlyList<(int Level, string Body, ToastFix? FixNav)> missed)
    {
        if (missed.Count == 0) return;
        int worst = missed.Max(x => x.Level);
        var latest = missed[^1];
        ShowToast(missed.Count == 1
            ? $"While you were away: {latest.Body}"
            : $"While you were away ({missed.Count} notices): {latest.Body}  More in the Activity log.",
            worst, latest.FixNav);
    }

    // ---- Rail status decorations (badge, counts, activity chip) and the fits-nowhere flag they read ----
    /// <summary>Health badge visibility in the rail: rail status decorations stay hidden until the tour's
    /// last stop introduces them. Named separately so the underlying signal stays readable at call sites.</summary>
    public bool ShowHealthBadgeInRail => Overview.ShowHealthBadge && ShowRailStatus;
    internal void RaiseHealthBadgeInRail() => OnPropertyChanged(nameof(ShowHealthBadgeInRail));

    /// <summary>Rail numeric counts: hidden by a collapsed rail and by the guide until its last stop.
    /// One property, because an element cannot carry two IsVisible bindings.</summary>
    public bool ShowRailCounts => RailExpanded && ShowRailStatus;

    /// <summary>Rail status is off on About: its black ground defeats the status colors, and half a status
    /// system is worse than none. Also hidden while the guide runs, until the settings-look stop.</summary>
    public bool ShowRailStatus => !ShowAboutView
        && (!Guide.GuideActive || Guide.IsAcked("settings-look"));

    /// <summary>Files still to fetch, shown on the rail's Overview row while a run is on; amber because work
    /// outstanding is Attention, not a fault.</summary>
    public bool ShowRailActivity => ShowRailStatus && (Overview.RunActive || QueueHasUnplaceable || QueueNeedsAccount);
    /// <summary>The rail's one FILE count sits beside two GAME fractions, so it wears its unit.
    /// Outside a run FilesLeftText reads "--", so the idle red states (fits-nowhere, no account)
    /// count the queue directly.</summary>
    public string RailActivityText
    {
        get
        {
            // A queue whose every row is red is not "0 Files" (QA 09-18): the rail counts the queue when nothing
            // can land, the reachable files while a run has work.
            var n = Overview.RunActive && Overview.ReachableCount > 0 ? Overview.FilesLeftText : Overview.DownloadQueueCount.ToString();
            return $"{n} File{(n == "1" ? "" : "s")}";
        }
    }
    /// <summary>(Rail exceptions 09-09) Overview's outstanding items. Only states that BLOCK the queue are
    /// reported here, and they are Faults: work merely outstanding is not an exception, it is the standing
    /// fact line 2 already carries ("N files queued"). This is what retired the red count pill -- a queue
    /// count is Attention at most, and red on it broke the colour law (red is loss or breakage, never
    /// "not done yet").</summary>
    public IReadOnlyList<RailException> RailOverviewExceptions
    {
        get
        {
            var list = new List<RailException>();
            if (QueueNeedsAccount) list.Add(new("no account", Grog.Core.Sync.Severity.Fault));
            if (QueueHasUnplaceable)
                list.Add(new($"{Grog.Core.Format.ByteFormat.Size(QueueUnplaceableBytes)} won't fit", Grog.Core.Sync.Severity.Fault));
            return list;
        }
    }

    /// <summary>Queued work with NO connected account to serve it: as dead as fits-nowhere, so it
    /// shares the red (owner 08-31). Cleared the moment any account connects.</summary>
    public bool QueueNeedsAccount => !IsConnected && !Overview.QueueIsEmpty;
    /// <summary>The queue chip's color IS the queue's health: amber while everything queued has a home
    /// (work outstanding is Attention), red once part of the queue fits on no device - the owner's chosen
    /// global signal for the fits-nowhere state (folder dots keep meaning folder connectivity only) -
    /// or once no connected account can serve it.</summary>
    public IBrush RailActivityBrush => QueueHasUnplaceable || QueueNeedsAccount ? Palette.ErrorRed : Palette.AccentAmber;
    public string RailActivityTip => QueueNeedsAccount
        ? "Files are queued but no GOG account is connected - connect an account to download"
        : QueueHasUnplaceable
            ? "Part of the download queue fits on no device - see Storage"
            : "Files still to download";

    /// <summary>Bytes of the queue that fit on no device. The rail states the SIZE, not just the fact: "won't
    /// fit" alone gives you nothing to act on, while "40.2 GB won't fit" tells you how much room to find.
    /// Set beside <see cref="QueueHasUnplaceable"/> from the same placement pass.</summary>
    private long _queueUnplaceableBytes;
    public long QueueUnplaceableBytes
    {
        get => _queueUnplaceableBytes;
        set
        {
            if (_queueUnplaceableBytes == value) return;
            _queueUnplaceableBytes = value;
            OnPropertyChanged(nameof(QueueUnplaceableBytes));
            OnPropertyChanged(nameof(RailOverviewExceptions));   // (Rail exceptions 09-09)
        }
    }

    private bool _queueHasUnplaceable;
    /// <summary>Set by RefreshBackupLocations from the live placement plan; raising the chip trio here keeps
    /// the rail in step the moment a folder is added, swapped or filled.</summary>
    public bool QueueHasUnplaceable
    {
        get => _queueHasUnplaceable;
        set
        {
            if (_queueHasUnplaceable == value) return;
            _queueHasUnplaceable = value;
            OnPropertyChanged(nameof(QueueHasUnplaceable));
            OnPropertyChanged(nameof(ShowRailActivity));
            OnPropertyChanged(nameof(RailOverviewExceptions));   // (Rail exceptions 09-09)
            OnPropertyChanged(nameof(RailActivityBrush));
            OnPropertyChanged(nameof(RailActivityTip));
        }
    }

    /// <summary>The Overview's copy of the fits-nowhere warning; recomputed with the flag above.</summary>
    [ObservableProperty] private string _queueUnplaceableLine = "";
}

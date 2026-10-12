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

public partial class MainWindowViewModel
{
    public string TrayConfirmBody => OperatingSystem.IsMacOS() ? "Or keep it running quietly from the menu bar." : "Or keep it running quietly in the tray.";

    public string RailVersionText => $"v{VersionString}";

    public bool IsPortableCopy => Grog.Core.Storage.GrogPaths.PortableInstallDir() is not null;

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
    [ObservableProperty] private double _mockBgOpacity = 0.25;

    public bool RailExpanded => !RailCollapsed;

    internal void MeasureRailWorstCase()
    {
        Overview.ForceRailWorstCase();
        OnPropertyChanged(nameof(ShowRailCounts));
    }

    public Avalonia.Controls.GridLength RailWidth
    {
        get => new(RailCollapsed ? 60 : System.Math.Clamp(_settings.RailExpandedWidth, RailMinExpanded, RailMaxExpanded));
        set
        {
            if (RailCollapsed || !value.IsAbsolute) return;
            var px = System.Math.Clamp(value.Value, RailMinExpanded, RailMaxExpanded);
            if (System.Math.Abs(_settings.RailExpandedWidth - px) < 0.5) return;
            _settings.RailExpandedWidth = px; _settings.SaveSoon();   // per-drag-delta: debounced, off-thread
            OnPropertyChanged();
        }
    }

    public int RailBarSpan => RailCollapsed ? 2 : 1;

    public Avalonia.Controls.GridLength ActivityTopHeight
    {
        get => new(System.Math.Clamp(_settings.ActivityTopHeight, 96, 420));
        set
        {
            if (!value.IsAbsolute) return;
            var px = System.Math.Clamp(value.Value, 96, 420);
            if (System.Math.Abs(_settings.ActivityTopHeight - px) < 0.5) return;
            _settings.ActivityTopHeight = px; _settings.SaveSoon();   // per-drag-delta: debounced, off-thread
            OnPropertyChanged();
        }
    }

    private const double RailMinExpanded = 260;
    private const double RailMaxExpanded = 360;
    public double RailMinWidth => RailCollapsed ? 60 : RailMinExpanded;
    public double RailMaxWidth => RailCollapsed ? 60 : RailMaxExpanded;

    [RelayCommand]
    private void ToggleRail() => RailCollapsed = !RailCollapsed;

    public bool Quitting { get; private set; }

    public void NoteOsQuit() { Quitting = true; PauseAllForShutdown(); }

    public void RequestQuit() => _ = QuitAsync();

    public bool HasWritesInFlight => !(_liveEngine?.WhenStopped ?? Task.CompletedTask).IsCompleted || !Storage.WhenMoveStopped.IsCompleted;
    [ObservableProperty] private bool _showClosingWait;
    [ObservableProperty] private string _closingWaitText = "";
    [ObservableProperty] private bool _closingWaitCanForce;
    private TaskCompletionSource? _closeNow;
    [RelayCommand] private void CloseNow() => _closeNow?.TrySetResult();

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
            if (OperatingSystem.IsMacOS())
                Environment.Exit(0);
        }
    }

    public const double NarrowChromeWidth = 1500;
    [ObservableProperty] private bool _narrowChrome;
    partial void OnNarrowChromeChanged(bool value)
    {
        OnPropertyChanged(nameof(SortLabelInstallers)); OnPropertyChanged(nameof(SortLabelExtras));
    }
    public string SortLabelInstallers => NarrowChrome ? "Installers" : "Installers first";
    public string SortLabelExtras => NarrowChrome ? "Extras" : "Extras first";

    [ObservableProperty] private bool _showCloseConfirm;

    [ObservableProperty] private bool _rememberCloseChoice;

    private bool _shutdownFlushed;
    public void PauseAllForShutdown()
    {
        if (_shutdownFlushed) return;   // every quit route calls this, and the close dialog's route calls it twice
        _shutdownFlushed = true;
        Schedule.EndScheduleCountdown();   // a countdown must not fire a backup into a closing process
        StopSizing();
        try { PauseDownloads(); } catch { }
        Storage.PauseReorgForShutdown();
        try { Task.Run(() => _manifest?.SaveAsync() ?? Task.CompletedTask).Wait(TimeSpan.FromSeconds(5)); }
        catch { /* best-effort: never block a quit on a failing disk */ }
        try { _logFile?.Flush(TimeSpan.FromSeconds(2)); _logFile?.Close(); } catch { }   // drain the background writer, then release the file
        try { AppSettings.FlushPendingSave(); } catch { }   // a debounced splitter save must not die with the process
    }

    public const int TrayAttentionWarning = 1;
    public const int TrayAttentionProblem = 2;
    public event Action<int, string, ToastFix?>? TrayAttentionRequested;

    private void NotifyUser(string title, string body, bool isError = true, string? toastBody = null,
                            int? attention = null, ToastFix? fixNav = null)
    {
        bool os = Grog.App.Notifier.Notify(title, body);
        if (!os) ShowToast(toastBody ?? body, isError ? 2 : 0, fixNav);
        Log($"Notification ({(os ? "desktop" : "in-app")}): {title}", isError: false);
        var level = attention ?? (isError ? TrayAttentionProblem : 0);
        if (level > 0) TrayAttentionRequested?.Invoke(level, toastBody ?? body, fixNav);
    }

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
    public bool ShowHealthBadgeInRail => Overview.ShowHealthBadge && ShowRailStatus;
    internal void RaiseHealthBadgeInRail() => OnPropertyChanged(nameof(ShowHealthBadgeInRail));

    public bool ShowRailCounts => RailExpanded && ShowRailStatus;

    public bool ShowRailStatus => !ShowAboutView
        && (!Guide.GuideActive || Guide.IsAcked("settings-look"));

    public bool ShowRailActivity => ShowRailStatus && (Overview.RunActive || QueueHasUnplaceable || QueueNeedsAccount);
    public string RailActivityText
    {
        get
        {
            var n = Overview.RunActive && Overview.ReachableCount > 0 ? Overview.FilesLeftText : Overview.DownloadQueueCount.ToString();
            return $"{n} File{(n == "1" ? "" : "s")}";
        }
    }
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

    public bool QueueNeedsAccount => !IsConnected && !Overview.QueueIsEmpty;
    public IBrush RailActivityBrush => QueueHasUnplaceable || QueueNeedsAccount ? Palette.ErrorRed : Palette.AccentAmber;
    public string RailActivityTip => QueueNeedsAccount
        ? "Files are queued but no GOG account is connected - connect an account to download"
        : QueueHasUnplaceable
            ? "Part of the download queue fits on no device - see Storage"
            : "Files still to download";

    private long _queueUnplaceableBytes;
    public long QueueUnplaceableBytes
    {
        get => _queueUnplaceableBytes;
        set
        {
            if (_queueUnplaceableBytes == value) return;
            _queueUnplaceableBytes = value;
            OnPropertyChanged(nameof(QueueUnplaceableBytes));
            OnPropertyChanged(nameof(RailOverviewExceptions));   // 
        }
    }

    private bool _queueHasUnplaceable;
    public bool QueueHasUnplaceable
    {
        get => _queueHasUnplaceable;
        set
        {
            if (_queueHasUnplaceable == value) return;
            _queueHasUnplaceable = value;
            OnPropertyChanged(nameof(QueueHasUnplaceable));
            OnPropertyChanged(nameof(ShowRailActivity));
            OnPropertyChanged(nameof(RailOverviewExceptions));   // 
            OnPropertyChanged(nameof(RailActivityBrush));
            OnPropertyChanged(nameof(RailActivityTip));
        }
    }

    [ObservableProperty] private string _queueUnplaceableLine = "";
}

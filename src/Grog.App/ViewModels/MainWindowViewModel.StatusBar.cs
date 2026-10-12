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
    [ObservableProperty] private double _downloadSpeedBytes;
    private readonly Grog.Core.Download.ThroughputMeter _meter = new();
    internal Grog.Core.Download.ThroughputMeter Meter => _meter;
    internal int _runFilesDone;
    internal int _runFailed => _runFailedKeys.Count;
    internal readonly HashSet<(long, string)> _runFailedKeys = new();
    internal long _runBytesSettled;
    internal readonly HashSet<Guid> _settledTaskIds = new();

    private void ResetRunCounters()
    {
        _meter.Reset(); _runFilesDone = 0; _runFailedKeys.Clear(); _runBytesSettled = 0; _settledTaskIds.Clear();
        Overview.ResetSpeedGraph();
    }

    public bool HasAnyBackedUp => _manifest?.Current.Items.Any(i => i.Files.Any(f => Grog.Core.Sync.BackupScope.IsPresent(f))) ?? false;

    public bool PrimaryEnabled => !Busy && (!IsConnected || Library.TotalCount == 0 || Library.PendingTaskCount > 0);

    public SetupPhase Phase =>
        !HasLocation ? SetupPhase.NoLocation :
        !IsConnected ? SetupPhase.NoAccount :
        Library.TotalCount == 0 ? SetupPhase.NoLibrary :
        SetupPhase.Cataloged;

    public enum BarState { NotConnected, NoFolder, NeverScanned, Scanning, WorkToDo, UpToDate, Running, Paused }

    public BarState Bar =>
        !IsConnected                                          ? BarState.NotConnected :
        !HasLocation                                          ? BarState.NoFolder :
        Library.CatalogBusy                                           ? BarState.Scanning :
        DownloadPaused                                        ? BarState.Paused :
        DownloadRunning                                       ? BarState.Running :
        _resuming                                             ? BarState.Running :   // bridge the resume gap so the primary never blinks back to "Back up"
        (Library.TotalCount == 0 && !Library.CatalogScanned)                  ? BarState.NeverScanned :
        (Library.PendingTaskCount > 0 || Library.SelectedActionableCount > 0) ? BarState.WorkToDo :
        BarState.UpToDate;

    public bool BarInSetup => Bar is BarState.NotConnected or BarState.NoFolder or BarState.NeverScanned or BarState.Scanning;

    // ---- RIGHT slot: the live "do this now" primary, right-edge anchored, only relabels/restyles ----
    public string BarPrimaryLabel => Bar switch
    {
        BarState.NotConnected => Overview.QueueConnectLabel,
        BarState.NoFolder     => "Choose a Folder",
        BarState.NeverScanned => "Scan GOG Library",
        BarState.Scanning     => "Skip Library Scan",
        BarState.WorkToDo     => QueueHeldResume ? $"Resume · {Grog.Core.Format.Plural.Of(Overview.ResumableCount, "file")}"
                                                 : Library.SyncBackupLabel,   // selection-aware ("Back up · 3 items")
        BarState.UpToDate     => "Library Backed Up",
        BarState.Running      => "Pause",
        BarState.Paused       => Overview.ResumableCount > 0 ? $"Resume · {Grog.Core.Format.Plural.Of(Overview.ResumableCount, "file")}" : "Resume",
        _                     => Library.SyncBackupLabel,
    };

    public System.Windows.Input.ICommand? BarPrimaryCommand => Bar switch
    {
        BarState.NoFolder => ShowLocationPickerCommand,
        BarState.Scanning => CancelWorkCommand,
        BarState.UpToDate => null,
        BarState.Running  => PauseDownloadsCommand,
        BarState.Paused   => ResumeDownloadsCommand,
        BarState.WorkToDo when QueueHeldResume => ResumeDownloadsCommand,   // drains the persisted queue as-is
        _                 => PrimaryActionCommand,
    };

    private bool QueueHeldResume => Overview.ResumableCount > 0 && Library.SelectedActionableCount == 0;

    public bool BarPrimaryEnabled => Bar switch
    {
        BarState.UpToDate => false,           // dimmed, not actionable (the run is done)
        BarState.Scanning => true,            // Skip scan is always live (no dead ends)
        BarState.WorkToDo => PrimaryEnabled,
        _                 => true,
    };

    public string BarPrimaryKind => Bar switch
    {
        BarState.Scanning => "neutral",   // Skip Library Scan skips a step; red is destructive only
        BarState.UpToDate => "dimmed",
        BarState.Paused   => "resume",
        BarState.Running  => "neutral",   // Pause is neutral, never amber
        _                 => "amber",
    };
    public bool BarPrimaryDimmed  => Bar == BarState.UpToDate;    // grayed, not actionable
    public bool BarPrimaryResume  => Bar == BarState.Paused;      // green "Resume"
    public bool BarPrimaryNeutral => Bar is BarState.Running or BarState.Scanning;   // "Pause" and "Skip Library Scan": transport, never amber

    public string BarSecondaryLabel => Bar switch
    {
        BarState.NotConnected => "Scan GOG Library",   // look-ahead preview of the step this unlocks
        BarState.NoFolder     => "Scan GOG Library",
        BarState.NeverScanned => "Back up",
        BarState.Scanning     => "Back up",
        BarState.WorkToDo     => Library.RescanLabel,
        BarState.UpToDate     => Library.RescanLabel,
        BarState.Running      => FinishingCurrent ? "Keep downloading" : "Finish current files",
        BarState.Paused       => _pausedInProgressCount > 0 ? "Finish current files" : Library.RescanLabel,
        _                     => Library.RescanLabel,
    };

    public bool BarSecondaryIsLive => !BarInSetup;
    public bool BarSecondaryEnabled => BarSecondaryIsLive && !(Bar == BarState.Running && !FinishingCurrent && !CanDrain);
    public bool BarSecondaryIsDestructive => false;

    public System.Windows.Input.ICommand? BarSecondaryCommand => Bar switch
    {
        BarState.WorkToDo => CheckForUpdatesCommand,   // Rescan == same command as Scan
        BarState.UpToDate => CheckForUpdatesCommand,
        BarState.Running  => FinishingCurrent ? KeepDownloadingCommand : FinishCurrentFilesCommand,
        BarState.Paused   => _pausedInProgressCount > 0 ? FinishPausedFilesCommand : CheckForUpdatesCommand,
        _                 => null,                      // setup ramp: dimmed preview, not clickable
    };

    public string BarStatusGate => Bar switch
    {
        BarState.NotConnected => "Sign in to GOG to scan your library",
        BarState.NoFolder     => "Pick where your backups live",
        BarState.NeverScanned => "Scan to see everything you own",
        _                     => "",
    };
    public bool BarShowGate => BarStatusGate.Length > 0;

    public bool BarScanActive => Library.CatalogBusy;
    public bool BarScanIndeterminate => Library.CatalogBusy && Library.CatalogIndeterminate;
    public double BarScanProgress => Library.CatalogProgress;
    public string BarScanCountText => Library.CatalogBusy ? Library.CatalogDetail : "";

    internal void RaiseBarState()
    {
        RefreshPausedInProgress();
        OnPropertyChanged(nameof(Bar));
        OnPropertyChanged(nameof(BarInSetup));
        OnPropertyChanged(nameof(BarPrimaryLabel));
        OnPropertyChanged(nameof(BarPrimaryCommand));
        OnPropertyChanged(nameof(BarPrimaryEnabled));
        OnPropertyChanged(nameof(BarPrimaryKind));
        OnPropertyChanged(nameof(BarPrimaryDimmed));
        OnPropertyChanged(nameof(BarPrimaryResume));
        OnPropertyChanged(nameof(BarPrimaryNeutral));
        OnPropertyChanged(nameof(BarSecondaryLabel));

        Library.RaiseLibraryAction();

        OnPropertyChanged(nameof(BarSecondaryIsLive));
        OnPropertyChanged(nameof(BarSecondaryEnabled));
        OnPropertyChanged(nameof(BarSecondaryIsDestructive));
        OnPropertyChanged(nameof(BarSecondaryCommand));
        OnPropertyChanged(nameof(BarStatusGate));
        OnPropertyChanged(nameof(BarShowGate));
        OnPropertyChanged(nameof(BarScanActive));
        OnPropertyChanged(nameof(BarScanIndeterminate));
        OnPropertyChanged(nameof(BarScanProgress));
        OnPropertyChanged(nameof(BarScanCountText));
        Overview.RaiseRunBar();
        Overview.RaiseRunReadouts();
    }
}

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

// The run meter and the state-driven activity bar: the primary/secondary slots, the status gate and the scan
// sub-bar. (The throughput / speed-graph / coverage readouts moved to OverviewViewModel.Run in S3.3.)
public partial class MainWindowViewModel
{
    // ---- The run's throughput meter and per-run counters: fed by the engine host and the progress feed on
    //      this shell (session-level orchestration); the Overview reads them for its readouts. ----
    // Current is a short rolling window; Avg is cumulative over ACTIVE download time only, so a pause
    // never drags it down. ETA derives from Avg, so it does not bounce.
    [ObservableProperty] private double _downloadSpeedBytes;
    private readonly Grog.Core.Download.ThroughputMeter _meter = new();
    internal Grog.Core.Download.ThroughputMeter Meter => _meter;
    internal int _runFilesDone;
    /// <summary>Distinct files given up on this run: a Try Again that fails again is still one file, and a landing clears it.</summary>
    internal int _runFailed => _runFailedKeys.Count;
    internal readonly HashSet<(long, string)> _runFailedKeys = new();
    /// <summary>Bytes of files COMPLETED this run, at their learned sizes (companion to _runFilesDone).</summary>
    internal long _runBytesSettled;
    /// <summary>Tasks whose completion the settle callback has already folded into <see cref="_runBytesSettled"/>.</summary>
    internal readonly HashSet<Guid> _settledTaskIds = new();

    /// <summary>A fresh run's counters: the meter, the done/failed tallies, the settled bytes and the speed graph.</summary>
    private void ResetRunCounters()
    {
        _meter.Reset(); _runFilesDone = 0; _runFailedKeys.Clear(); _runBytesSettled = 0; _settledTaskIds.Clear();
        Overview.ResetSpeedGraph();
    }

    /// <summary>Anything backed up anywhere yet; gates the Folders page's inventory half.</summary>
    public bool HasAnyBackedUp => _manifest?.Current.Items.Any(i => i.Files.Any(f => Grog.Core.Sync.BackupScope.IsPresent(f))) ?? false;

    /// <summary>The primary button is disabled only when there is genuinely nothing to do.</summary>
    public bool PrimaryEnabled => !Busy && (!IsConnected || Library.TotalCount == 0 || Library.PendingTaskCount > 0);

    // ---- SINGLE SOURCE OF TRUTH for what the app is doing ----
    public SetupPhase Phase =>
        !HasLocation ? SetupPhase.NoLocation :
        !IsConnected ? SetupPhase.NoAccount :
        Library.TotalCount == 0 ? SetupPhase.NoLibrary :
        SetupPhase.Cataloged;

    // ---- State-driven activity bar ----
    // ONE enum drives the two-slot corner + status gate + color + scan sub-bar; the primary walks the setup
    // ramp then branches to run controls. No Error member: bad news lives in the HEALTH LEDE and Issues list.
    public enum BarState { NotConnected, NoFolder, NeverScanned, Scanning, WorkToDo, UpToDate, Running, Paused }

    /// <summary>The single source of truth for the activity bar. Order: connect/folder gates first, then
    /// live run/scan/error, then catalog states. At least one corner slot is ALWAYS actionable.</summary>
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

    /// <summary>True during the setup ramp -- the LEFT slot is a dimmed look-ahead preview, not a live
    /// control; once operational it becomes the live secondary (Rescan / Stop / Manage folders).</summary>
    public bool BarInSetup => Bar is BarState.NotConnected or BarState.NoFolder or BarState.NeverScanned or BarState.Scanning;

    // ---- RIGHT slot: the live "do this now" primary, right-edge anchored, only relabels/restyles ----
    public string BarPrimaryLabel => Bar switch
    {
        // Same rule as the queue chip (1039): a REGISTERED account that is merely signed out needs
        // "Log In", not "Connect Account" - the latter reads as "you have no account" (owner, mac 09-02).
        BarState.NotConnected => Overview.QueueConnectLabel,
        BarState.NoFolder     => "Choose a Folder",
        BarState.NeverScanned => "Scan GOG Library",
        // "Skip LIBRARY Scan", not "Skip Scan" (owner 09-10): the folder-import dialog carries its own
        // "Skip Scan & Add", which skips walking a FOLDER for existing backups. Two controls named the same
        // for two different scans is exactly the operational drift the vocabulary rule exists to stop.
        BarState.Scanning     => "Skip Library Scan",
        // (09-22, owner) Files already waiting in the queue and nothing running (a failed run, a network drop, a
        // finished drain) is a RESUME, not a fresh "Back up": the queue is kept and picks up where it stopped.
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
        // Connect / Choose-folder-default / Scan / Back up / Retry all dispatch through PrimaryAction.
        _                 => PrimaryActionCommand,
    };

    /// <summary>The persisted queue holds files and no selection is asking for something else.</summary>
    /// <summary>Files wait in the queue that a Resume would actually start (won't-fit rows do not count: resuming
    /// those only re-opens "nothing fits"), and no Library selection is asking for something else.</summary>
    private bool QueueHeldResume => Overview.ResumableCount > 0 && Library.SelectedActionableCount == 0;

    public bool BarPrimaryEnabled => Bar switch
    {
        BarState.UpToDate => false,           // dimmed, not actionable (the run is done)
        BarState.Scanning => true,            // Skip scan is always live (no dead ends)
        BarState.WorkToDo => PrimaryEnabled,
        _                 => true,
    };

    /// <summary>Style key for the primary. Color language: AMBER = advance the backup ONLY; Pause is a
    /// transport control, NEUTRAL; Resume = green (a "go"); dimmed = up to date.</summary>
    public string BarPrimaryKind => Bar switch
    {
        BarState.Scanning => "neutral",   // Skip Library Scan skips a step; red is destructive only
        BarState.UpToDate => "dimmed",
        BarState.Paused   => "resume",
        BarState.Running  => "neutral",   // Pause is neutral, never amber
        _                 => "amber",
    };
    // Conditional-class bools for the primary's style variants (Avalonia Classes.x bindings).
    public bool BarPrimaryDimmed  => Bar == BarState.UpToDate;    // grayed, not actionable
    public bool BarPrimaryResume  => Bar == BarState.Paused;      // green "Resume"
    public bool BarPrimaryNeutral => Bar is BarState.Running or BarState.Scanning;   // "Pause" and "Skip Library Scan": transport, never amber

    // ---- LEFT slot: dimmed look-ahead during setup, live secondary once operational ----
    public string BarSecondaryLabel => Bar switch
    {
        BarState.NotConnected => "Scan GOG Library",   // look-ahead preview of the step this unlocks
        BarState.NoFolder     => "Scan GOG Library",
        BarState.NeverScanned => "Back up",
        BarState.Scanning     => "Back up",
        BarState.WorkToDo     => Library.RescanLabel,
        BarState.UpToDate     => Library.RescanLabel,
        // (09-19) Run verbs live here, queue verbs in the Downloading pane: Clear left this row for the pane's
        // "Clear queue…", and the running state got the clean stop beside Pause.
        // (09-22, owner) After the click it offers the way back: the "Finishing N files" status moves to the lede.
        BarState.Running      => FinishingCurrent ? "Keep downloading" : "Finish current files",
        BarState.Paused       => _pausedInProgressCount > 0 ? "Finish current files" : Library.RescanLabel,
        _                     => Library.RescanLabel,
    };

    /// <summary>LEFT slot is a live control (operational) vs a dimmed preview (setup ramp).</summary>
    public bool BarSecondaryIsLive => !BarInSetup;
    public bool BarSecondaryEnabled => BarSecondaryIsLive && !(Bar == BarState.Running && !FinishingCurrent && !CanDrain);
    /// <summary>Nothing destructive sits in this row any more (09-19): Clear is the Downloading pane's.</summary>
    public bool BarSecondaryIsDestructive => false;

    public System.Windows.Input.ICommand? BarSecondaryCommand => Bar switch
    {
        BarState.WorkToDo => CheckForUpdatesCommand,   // Rescan == same command as Scan
        BarState.UpToDate => CheckForUpdatesCommand,
        // Confirms first, exactly like the queue pane's Clear -- same action, same guard.
        BarState.Running  => FinishingCurrent ? KeepDownloadingCommand : FinishCurrentFilesCommand,
        // (09-25, owner) Paused with files part-way down: finish just those, then pause again.
        BarState.Paused   => _pausedInProgressCount > 0 ? FinishPausedFilesCommand : CheckForUpdatesCommand,
        _                 => null,                      // setup ramp: dimmed preview, not clickable
    };

    // ---- STATUS gate: during setup, STATUS earns a second job -- it explains the gate ----
    public string BarStatusGate => Bar switch
    {
        BarState.NotConnected => "Sign in to GOG to scan your library",
        BarState.NoFolder     => "Pick where your backups live",
        BarState.NeverScanned => "Scan to see everything you own",
        _                     => "",
    };
    public bool BarShowGate => BarStatusGate.Length > 0;

    // ---- Scan sub-bar: lives in the CORNER under the button, separate zone from the STATUS backup bar ----
    public bool BarScanActive => Library.CatalogBusy;
    public bool BarScanIndeterminate => Library.CatalogBusy && Library.CatalogIndeterminate;
    public double BarScanProgress => Library.CatalogProgress;
    public string BarScanCountText => Library.CatalogBusy ? Library.CatalogDetail : "";

    /// <summary>Raise everything the activity bar derives. Called from the central state/run hooks so the
    /// corner, status gate, color, and scan sub-bar all refresh off the one BarState.</summary>
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
        Library.RaiseLibraryAction();   // the Library foot's own labels follow the same state
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
        // The Status run lede reads CatalogBusy too and is NOT part of RaiseRunBar; raise it here.
        Overview.RaiseRunReadouts();
    }
}

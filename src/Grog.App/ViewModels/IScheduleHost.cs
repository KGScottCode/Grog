// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Threading.Tasks;

namespace Grog.App.ViewModels;

/// <summary>What the scheduler needs from the window. Implemented explicitly by MainWindowViewModel
/// (MainWindowViewModel.ScheduleHost.cs); faked in ScheduleViewModelTests.</summary>
internal interface IScheduleHost
{
    // Facts
    /// <summary>The persisted schedule, or null until services and the manifest are ready.</summary>
    Grog.Core.Scheduling.BackupSchedule? Schedule { get; }
    /// <summary>The manifest's mutate gate, for schedule writes that touch a collection (custom days);
    /// null when no manifest is bound. (manifest gate 09-08)</summary>
    Grog.Core.Manifest.ManifestGate? ManifestGate { get; }

    bool Busy { get; }
    bool DownloadRunning { get; }
    bool IsConnected { get; }
    bool WindowHidden { get; }
    bool NotifyOnErrors { get; }
    AppSettings Settings { get; }
    int RunFailed { get; }
    /// <summary>The last run's journal summary (survives a restart), or null when none was written yet.</summary>
    Grog.Core.Runs.RunSummary? LastRunSummary { get; }
    int RunFilesDone { get; }
    long RunBytesSettled { get; }
    string BackupRoot { get; }
    string ScopeCaption { get; }

    // Actions
    void SaveManifest();
    void Log(string message, bool isError = false, LogCategory category = LogCategory.General);
    /// <summary>The signed-out skip notification (tray warning, Fix goes to Accounts).</summary>
    void NotifySkippedSignedOut();
    /// <summary>Start the unattended, chip-blind, full-scope backup.</summary>
    void StartScheduledBackup();
    /// <summary>The ordinary Back Up Now the overdue banner offers.</summary>
    Task RunBackupNow();
    void Navigate(string view);
    /// <summary>The Health status reads a clock-bound staleness window; the scheduler's tick re-raises it.</summary>
    void RaiseHealthStatus();
}

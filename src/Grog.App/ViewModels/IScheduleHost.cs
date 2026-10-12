// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Threading.Tasks;

namespace Grog.App.ViewModels;

internal interface IScheduleHost
{
    // Facts
    Grog.Core.Scheduling.BackupSchedule? Schedule { get; }
    Grog.Core.Manifest.ManifestGate? ManifestGate { get; }

    bool Busy { get; }
    bool DownloadRunning { get; }
    bool IsConnected { get; }
    bool WindowHidden { get; }
    bool NotifyOnErrors { get; }
    AppSettings Settings { get; }
    int RunFailed { get; }
    Grog.Core.Runs.RunSummary? LastRunSummary { get; }
    int RunFilesDone { get; }
    long RunBytesSettled { get; }
    string BackupRoot { get; }
    string ScopeCaption { get; }

    // Actions
    void SaveManifest();
    void Log(string message, bool isError = false, LogCategory category = LogCategory.General);
    void NotifySkippedSignedOut();
    void StartScheduledBackup();
    Task RunBackupNow();
    void Navigate(string view);
    void RaiseHealthStatus();
}

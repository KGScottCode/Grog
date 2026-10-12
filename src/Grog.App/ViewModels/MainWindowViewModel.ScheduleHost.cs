// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Threading.Tasks;

namespace Grog.App.ViewModels;

public partial class MainWindowViewModel : IScheduleHost
{
    public ScheduleViewModel Schedule { get; }

    internal Grog.Core.Scheduling.BackupSchedule? Sched => _servicesReady ? _manifest.Current.Schedule : null;

    Grog.Core.Scheduling.BackupSchedule? IScheduleHost.Schedule => Sched;
    Grog.Core.Manifest.ManifestGate? IScheduleHost.ManifestGate => _servicesReady ? _manifest.Gate : null;   // 

    bool IScheduleHost.Busy => Busy;
    bool IScheduleHost.DownloadRunning => DownloadRunning;
    bool IScheduleHost.IsConnected => IsConnected;
    bool IScheduleHost.WindowHidden => WindowHidden;
    bool IScheduleHost.NotifyOnErrors => Settings.NotifyOnErrors;
    AppSettings IScheduleHost.Settings => _settings;
    int IScheduleHost.RunFailed => _runFailed;
    Grog.Core.Runs.RunSummary? IScheduleHost.LastRunSummary => Overview.LastRunSummary;
    int IScheduleHost.RunFilesDone => _runFilesDone;
    long IScheduleHost.RunBytesSettled => _runBytesSettled;
    string IScheduleHost.BackupRoot => _backupRoot;
    string IScheduleHost.ScopeCaption => Library.EffectiveScope.Caption;
    void IScheduleHost.SaveManifest() => SaveInBackground("schedule");
    void IScheduleHost.Log(string message, bool isError, LogCategory category) => Log(message, isError: isError, category: category);
    void IScheduleHost.NotifySkippedSignedOut()
        => NotifyUser("Grog - scheduled backup skipped",
               "No account is signed in. Open Grog and sign in from the Accounts page.",
               toastBody: "Scheduled backup skipped - no account signed in.",
               attention: TrayAttentionWarning,    // a skip is a warning; failed transfers stay red
               fixNav: "Accounts");                // Fix goes where the fix IS: the sign-in page
    void IScheduleHost.StartScheduledBackup() => _ = SyncBackupCore(skipCatalogRefresh: false, ignoreContentChips: true, unattended: true);
    Task IScheduleHost.RunBackupNow() => SyncBackup();
    void IScheduleHost.Navigate(string view) => Navigate(view);
    void IScheduleHost.RaiseHealthStatus() => Overview.RaiseHealthStatus();
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Threading.Tasks;

namespace Grog.App.ViewModels;

// The window's side of the scheduler: IScheduleHost, explicitly implemented. No Changed event: nothing
// bound on the window reads scheduler state (the one host effect, RaiseHealthStatus, is an action).
public partial class MainWindowViewModel : IScheduleHost
{
    /// <summary>Automatic backups (timer, countdown, overdue, Settings card). Bound through
    /// <c>DataContext="{Binding Schedule}"</c> in SettingsPage, OverviewPage and the window's foot cards.</summary>
    public ScheduleViewModel Schedule { get; }

    /// <summary>The persisted schedule once services are up; the Health staleness window reads it too.</summary>
    internal Grog.Core.Scheduling.BackupSchedule? Sched => _servicesReady ? _manifest.Current.Schedule : null;

    Grog.Core.Scheduling.BackupSchedule? IScheduleHost.Schedule => Sched;
    Grog.Core.Manifest.ManifestGate? IScheduleHost.ManifestGate => _servicesReady ? _manifest.Gate : null;   // (manifest gate 09-08)

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
    // Chip-blind: content chips are VIEW state and Scope alone decides what is backed up, so an
    // unattended run covers the full scope regardless of how the Library was left filtered.
    void IScheduleHost.StartScheduledBackup() => _ = SyncBackupCore(skipCatalogRefresh: false, ignoreContentChips: true, unattended: true);
    Task IScheduleHost.RunBackupNow() => SyncBackup();
    void IScheduleHost.Navigate(string view) => Navigate(view);
    void IScheduleHost.RaiseHealthStatus() => Overview.RaiseHealthStatus();
}

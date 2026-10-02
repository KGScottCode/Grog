// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Grog.App;
using Grog.App.ViewModels;
using Grog.Core.Scheduling;
using Grog.Core.Tests.Framework;

/// <summary>
/// The scheduler behind IScheduleHost (split 2026-09-02): a fake window supplies the schedule, the facts and
/// counters for the actions. Timers are never armed here (StartScheduleTimer is not called), so nothing
/// needs a dispatcher. AppSettings.Save lands in the test-run temp dir (Program.cs sets GROG_CONFIG_DIR).
/// </summary>
[NewBatch]
[Trait("schedule")]
public sealed class ScheduleViewModelTests
{
    private sealed class FakeHost : IScheduleHost
    {
        public BackupSchedule? Schedule { get; set; }
        public Grog.Core.Manifest.ManifestGate? ManifestGate => null;

        public bool Busy { get; set; }
        public bool DownloadRunning { get; set; }
        public bool IsConnected { get; set; } = true;
        public bool WindowHidden { get; set; }
        public bool NotifyOnErrors { get; set; }
        public AppSettings Settings { get; } = new();
        public int RunFailed { get; set; }
        public Grog.Core.Runs.RunSummary? LastRunSummary { get; set; }
        public int RunFilesDone { get; set; }
        public long RunBytesSettled { get; set; }
        public string BackupRoot { get; set; } = "";
        public string ScopeCaption { get; set; } = "Games & Extras · English";
        public int Saves, Started, RanNow, Notified, HealthRaised;
        public List<string> Logged { get; } = new();
        public List<string> Navigated { get; } = new();
        public void SaveManifest() => Saves++;
        public void Log(string message, bool isError = false, LogCategory category = LogCategory.General) => Logged.Add(message);
        public void NotifySkippedSignedOut() => Notified++;
        public void StartScheduledBackup() => Started++;
        public Task RunBackupNow() { RanNow++; return Task.CompletedTask; }
        public void Navigate(string view) => Navigated.Add(view);
        public void RaiseHealthStatus() => HealthRaised++;
    }

    private static (ScheduleViewModel Vm, FakeHost Host) Rig(BackupSchedule? s)
    {
        var h = new FakeHost { Schedule = s };
        return (new ScheduleViewModel(h), h);
    }

    // Sweep 2 #21: re-enabling kept the anchor from the first enable.
    [Test] void ReEnablingRestampsTheAnchor_SoTimeSpentOffIsNeverAMiss()
    {
        var old = DateTimeOffset.Now.AddDays(-30);
        var (vm, h) = Rig(new BackupSchedule { Enabled = false, Frequency = ScheduleFrequency.Daily, TimeOfDayMinutes = 3 * 60, AnchorDate = old });
        h.Settings.ShowOverduePrompt = true; h.IsConnected = true;

        vm.ScheduleEnabled = true;

        Assert.True(h.Schedule!.AnchorDate > old.AddDays(29), "anchored to the moment it was switched back on");
        vm.CheckOverdueBackupOnLaunch();
        Assert.False(vm.ScheduleOverdue, "yesterday's 03:00 was not a miss: the schedule was off");
    }

    [Test] void EnableStampsAnchorPersistsAndRaisesShape()
    {
        var (vm, h) = Rig(new BackupSchedule { Frequency = ScheduleFrequency.Weekly });
        Assert.Equal("Off", vm.NextRunValue, "off until enabled");

        vm.ScheduleEnabled = true;

        Assert.True(h.Schedule!.Enabled, "flag set");
        Assert.NotNull(h.Schedule.AnchorDate, "enabling stamps the anchor so a pre-schedule slot never reads as missed");
        Assert.Equal(1, h.Saves, "persisted once");
        Assert.True(vm.ShowWeeklyDay && !vm.ShowMonthlyDay && !vm.ShowCustomDays, "frequency rows follow the shape");
        Assert.NotEqual("Off", vm.NextRunValue, "a next run is computed");
        Assert.Equal("Never", vm.LastRunValue, "no run yet");
    }

    [Test] void SettersBeforeServicesAreNoOps()
    {
        var (vm, h) = Rig(null);
        vm.ScheduleEnabled = true; vm.IsFreqWeekly = true; vm.ScheduleTime = TimeSpan.FromHours(5);
        vm.ScheduleMonthlyDay = 3; vm.ScheduleIntervalWeeks = 4; vm.CustomMon = true;
        Assert.Equal(0, h.Saves, "nothing persisted with no manifest");
        Assert.Equal("Off", vm.NextRunValue, "readouts stay in the off state");
        Assert.Equal("Never", vm.LastRunValue, "no run");
        Assert.Equal("Automatic backups are off.", vm.NextFireLine, "settings line");
    }

    [Test] void RecordRunOutcomeStampsAttemptAlwaysAndLastRunOnlyOnFinish()
    {
        var s = new BackupSchedule { Enabled = true, AnchorDate = DateTimeOffset.Now.AddDays(-2) };
        var (vm, h) = Rig(s);
        vm.ScheduleOverdue = true;

        vm.RecordRunOutcome(ScheduleRunOutcome.Canceled);
        Assert.NotNull(s.LastAttempt, "an ending stamps the attempt");
        Assert.Equal(ScheduleRunOutcome.Canceled, s.LastAttemptOutcome, "with its outcome");
        Assert.Null(s.LastRun, "a canceled slot is not a completed run");
        Assert.True(vm.ScheduleOverdue, "and does not silence the overdue banner");
        Assert.Equal(ScheduleRunCopy.Label(ScheduleRunOutcome.Canceled), vm.LastRunResultValue, "result readout");

        vm.RecordRunOutcome(ScheduleRunOutcome.Completed);
        Assert.NotNull(s.LastRun, "a finished run stamps LastRun");
        Assert.False(vm.ScheduleOverdue, "and clears overdue");
        Assert.Equal(2, h.Saves, "each stamp persisted");
    }

    [Test] void LastRunResultSubReadsTheJournalWhenThereIsOne()
    {
        var s = new BackupSchedule { Enabled = true, LastAttempt = DateTimeOffset.Now.AddHours(-1), LastAttemptOutcome = ScheduleRunOutcome.Partial };
        var (vm, h) = Rig(s);
        h.RunFailed = 9;   // the in-memory tally (lost on restart) must not win over the durable record
        h.LastRunSummary = new Grog.Core.Runs.RunSummary { State = "Partial", Command = "download", Total = 1, Completed = 1 };
        Assert.Equal("1 hour ago · 9 files failed", vm.LastRunResultSub, "a one-file click's journal is not the scheduled run's (1283)");
        h.LastRunSummary = new Grog.Core.Runs.RunSummary { State = "Partial", Command = "backup", Total = 12, Completed = 11, Failed = 1 };
        Assert.Equal("1 hour ago · 11 of 12 files, 1 failed", vm.LastRunResultSub);

        h.LastRunSummary = new Grog.Core.Runs.RunSummary { State = "running", Total = 5, Completed = 1 };
        Assert.Equal("1 hour ago · 9 files failed", vm.LastRunResultSub, "a run still going is the live card's, not this one's");

        h.LastRunSummary = null; h.RunFailed = 0;
        Assert.Equal("1 hour ago", vm.LastRunResultSub, "no journal, no failures: the time alone");
    }

    [Test] void OverdueOnLaunchRespectsPromptFlagAnchorAndCoverage()
    {
        var s = new BackupSchedule { Enabled = true, Frequency = ScheduleFrequency.Daily, TimeOfDayMinutes = 3 * 60,
                                     AnchorDate = DateTimeOffset.Now.AddDays(-3) };
        var (vm, h) = Rig(s);

        vm.CheckOverdueBackupOnLaunch();
        Assert.True(vm.ScheduleOverdue, "a daily 3am slot passed since the anchor with no run");
        Assert.True(vm.ScheduleOverdueText.StartsWith("A scheduled backup was due"), vm.ScheduleOverdueText);

        vm.ScheduleOverdue = false; h.Settings.ShowOverduePrompt = false;
        vm.CheckOverdueBackupOnLaunch();
        Assert.False(vm.ScheduleOverdue, "\"Don't show this again\" is honored");

        h.Settings.ShowOverduePrompt = true; s.LastRun = DateTimeOffset.Now;
        vm.CheckOverdueBackupOnLaunch();
        Assert.False(vm.ScheduleOverdue, "a run since the slot covers it");

        s.LastRun = null; h.IsConnected = false;
        vm.CheckOverdueBackupOnLaunch();
        Assert.False(vm.ScheduleOverdue, "signed out: no nag, nothing could run");
    }

    [Test] void StartNowWaitsHonestlyWhenBusy()
    {
        var s = new BackupSchedule { Enabled = true };
        var (vm, h) = Rig(s);
        h.Busy = true;

        vm.StartScheduledRunNowCommand.Execute(null);
        Assert.Equal(0, h.Started, "no second run on top of a running one");
        Assert.Null(s.LastAttemptOutcome, "the slot is kept for the next tick, not stamped away");
        Assert.Equal(1, h.Logged.Count, "one honest log line");

        h.Busy = false;
        vm.StartScheduledRunNowCommand.Execute(null);
        Assert.Equal(1, h.Started, "free: the unattended backup starts");
        Assert.False(vm.ShowScheduleCountdown, "countdown card is down either way");
    }

    // B8: the slot advanced before the countdown, so a manual run started in those 5 s lost it for good.
    // WindowHidden: the countdown's DispatcherTimer is never built; a due tick starts the run directly.
    [Test] void SlotSurvivesAManualRunStartedDuringTheCountdown()
    {
        var s = new BackupSchedule { Enabled = true, AnchorDate = DateTimeOffset.Now.AddDays(-2) };
        var (vm, h) = Rig(s);
        h.WindowHidden = true;
        vm.NextScheduledFireForTests = DateTimeOffset.Now.AddMinutes(-1);

        h.Busy = true;   // a manual run started while the countdown was up: its Start finds the app busy
        vm.StartScheduledRunNowCommand.Execute(null);
        Assert.Equal(0, h.Started, "not started on top of the manual run");
        vm.ScheduleTick();
        Assert.Equal(0, h.Started, "busy: the tick waits");

        h.Busy = false;
        vm.ScheduleTick();
        Assert.Equal(1, h.Started, "the same slot fires once the manual run is over");
        vm.ScheduleTick();
        Assert.Equal(1, h.Started, "advanced on acceptance: the slot does not fire twice");
    }

    [Test] void ATransientRefusalKeepsTheSlotAndRetriesQuietly()
    {
        var s = new BackupSchedule { Enabled = true, AnchorDate = DateTimeOffset.Now.AddDays(-2) };
        var (vm, h) = Rig(s);
        h.WindowHidden = true;
        vm.NextScheduledFireForTests = DateTimeOffset.Now.AddMinutes(-1);

        vm.ScheduleTick();
        Assert.Equal(1, h.Started, "the due slot fires");
        vm.NoteScheduledRunRefused("a move is in progress");
        Assert.Null(s.LastAttemptOutcome, "nothing ran: nothing stamped");
        Assert.Equal(1, h.Logged.FindAll(l => l.StartsWith("Scheduled backup waits")).Count, "the wait is logged once");

        vm.ScheduleTick();
        Assert.Equal(2, h.Started, "the same slot retries");
        Assert.Equal(1, h.Logged.FindAll(l => l.StartsWith("Scheduled backup starting")).Count, "no second starting line: the retry is quiet");
        vm.NoteScheduledRunRefused("a move is in progress");
        Assert.Equal(1, h.Logged.FindAll(l => l.StartsWith("Scheduled backup waits")).Count, "the same reason is not logged again");
    }

    [Test] void ASignInRefusalAdvancesTheSlotAndStampsSkipped()
    {
        var s = new BackupSchedule { Enabled = true, AnchorDate = DateTimeOffset.Now.AddDays(-2) };
        var (vm, h) = Rig(s);
        h.WindowHidden = true; h.NotifyOnErrors = true;
        vm.NextScheduledFireForTests = DateTimeOffset.Now.AddMinutes(-1);

        vm.ScheduleTick();
        Assert.Equal(1, h.Started, "the due slot fires");
        vm.NoteScheduledRunRefused("sign-in is not ready", transient: false);
        Assert.Equal(ScheduleRunOutcome.Skipped, s.LastAttemptOutcome, "settled like the signed-out branch");
        Assert.Equal(1, h.Notified, "the signed-out toast");

        vm.ScheduleTick();
        Assert.Equal(1, h.Started, "the slot advanced: no retry pops a login");
    }

    // 1296 (owner 10-01, reverses 1275): a schedule LIFTS the user's pause; the host no longer refuses a paused queue.
    // Grog's own pause for a missing drive is a passing refusal: the slot is kept and the next tick retries quietly.
    [Test] void ADisconnectedDriveRefusalKeepsTheSlotAndRetriesNextTick()
    {
        var s = new BackupSchedule { Enabled = true, AnchorDate = DateTimeOffset.Now.AddDays(-2) };
        var (vm, h) = Rig(s);
        h.WindowHidden = true; h.NotifyOnErrors = true;
        vm.NextScheduledFireForTests = DateTimeOffset.Now.AddMinutes(-1);
        vm.ScheduleTick();
        Assert.Equal(1, h.Started, "the due slot fires");
        vm.NoteScheduledRunRefused("\"Small\" (Secondary) is disconnected", transient: true);
        Assert.NotEqual(ScheduleRunOutcome.Skipped, s.LastAttemptOutcome, "not settled: the drive may be back next tick");
        Assert.Equal(0, h.Notified, "not a sign-in problem: no toast");
        vm.ScheduleTick();
        Assert.Equal(2, h.Started, "the slot retries");
    }

    [Test] void FirstUnattendedNoticeYieldsToOverdueAndCountdown()
    {
        var (vm, h) = Rig(new BackupSchedule { Enabled = true });
        vm.SeedFirstUnattendedNotice("x");
        Assert.True(vm.ShowFirstUnattendedNotice, "seeded notice shows");
        vm.ScheduleOverdue = true;
        Assert.False(vm.ShowFirstUnattendedNotice, "overdue owns the slot");
        vm.ScheduleOverdue = false;
        vm.ShowScheduleCountdown = true;
        Assert.False(vm.ShowFirstUnattendedNotice, "countdown owns the slot");
        vm.ShowScheduleCountdown = false;

        vm.OpenSettingsFromNoticeCommand.Execute(null);
        Assert.Equal("", h.Settings.FirstUnattendedRunSummary, "opening Settings dismisses the notice");
        Assert.False(vm.ShowFirstUnattendedNotice, "gone");
        Assert.Equal("Settings", Assert.Single(h.Navigated), "and navigates there");
    }

    [Test] void NoteFirstUnattendedRunWritesOnceAndNamesTheScope()
    {
        var (vm, h) = Rig(new BackupSchedule { Enabled = true });
        h.RunFilesDone = 12; h.RunBytesSettled = 4_200_000_000; h.BackupRoot = "F:\\Backup";
        vm.NoteFirstUnattendedRun();
        Assert.Contains("12 files", h.Settings.FirstUnattendedRunSummary, "file count");
        Assert.Contains("games & extras (English)", h.Settings.FirstUnattendedRunSummary, "scope reads as prose");
        Assert.Contains("F:\\Backup", h.Settings.FirstUnattendedRunSummary, "destination named");
        Assert.True(h.Settings.FirstUnattendedRunNoticed, "latched");
        h.Settings.FirstUnattendedRunSummary = "kept";
        vm.NoteFirstUnattendedRun();
        Assert.Equal("kept", h.Settings.FirstUnattendedRunSummary, "a second unattended run writes nothing");
    }
}

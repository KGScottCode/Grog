// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Grog.App.ViewModels;

public sealed partial class ScheduleViewModel : ObservableObject
{
    private readonly IScheduleHost _host;
    internal ScheduleViewModel(IScheduleHost host) => _host = host;

    private DispatcherTimer? _scheduleTimer;
    private DateTimeOffset? _nextScheduledFire;
    internal DateTimeOffset? NextScheduledFireForTests { set => _nextScheduledFire = value; }

    private Grog.Core.Scheduling.BackupSchedule? Sched => _host.Schedule;

    public void StartScheduleTimer()
    {
        RecomputeNextFire();
        if (_scheduleTimer is not null) return;
        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _scheduleTimer.Tick += (_, _) => ScheduleTick();
        _scheduleTimer.Start();
    }

    private void RaiseNextFire()
    {
        OnPropertyChanged(nameof(NextFireLine));
        OnPropertyChanged(nameof(LastRunLine)); OnPropertyChanged(nameof(HasLastRunLine));
    }

    private void RecomputeNextFire()
    {
        _nextScheduledFire = Grog.Core.Scheduling.SchedulePlanner.NextFire(Sched, DateTimeOffset.Now);
        RaiseNextFire();
    }

    internal void ScheduleTick()
    {
        var s = Sched;
        RaiseSchedule();
        _host.RaiseHealthStatus();
        if (s is null || !s.Enabled) return;
        if (_nextScheduledFire is not { } fire) { RecomputeNextFire(); return; }
        var now = DateTimeOffset.Now;
        if (now < fire) return;

        if (Grog.Core.Scheduling.SchedulePlanner.SlotAlreadyCovered(s.LastRun, fire, now))
        {
            _nextScheduledFire = Grog.Core.Scheduling.SchedulePlanner.NextFire(s, DateTimeOffset.Now);
            RaiseNextFire();
            return;
        }

        if (ShowScheduleCountdown) return;
        if (_host.Busy || _host.DownloadRunning) return;
        if (_quietRetry) { StartScheduledRunNow(); return; }

        if (!_host.IsConnected)
        {
            AdvancePastSlot();
            _host.Log("Scheduled backup skipped - no account is signed in. Sign in from the Accounts page.",
                isError: true, category: LogCategory.Download);
            if (_host.NotifyOnErrors) _host.NotifySkippedSignedOut();
            RecordRunOutcome(Grog.Core.Scheduling.ScheduleRunOutcome.Skipped);
            return;
        }
        BeginScheduleCountdown();
    }

    private void AdvancePastSlot()
    {
        _nextScheduledFire = Grog.Core.Scheduling.SchedulePlanner.NextFire(Sched, DateTimeOffset.Now);
        RaiseNextFire();
    }

    public const int ScheduleCountdownSeconds = 5;
    [ObservableProperty] private bool _showScheduleCountdown;
    partial void OnShowScheduleCountdownChanged(bool value) => OnPropertyChanged(nameof(ShowFirstUnattendedNotice));   // shares the bottom slot
    [ObservableProperty] private string _scheduleCountdownText = "";
    private DispatcherTimer? _countdownTimer;
    private int _countdownLeft;

    private void BeginScheduleCountdown()
    {
        if (_host.WindowHidden) { StartScheduledRunNow(); return; }
        ScheduleOverdue = false;   // a run is happening right now - the overdue prompt is moot, and it shares this slot
        _countdownLeft = ScheduleCountdownSeconds;
        ScheduleCountdownText = CountdownLine(_countdownLeft);
        ShowScheduleCountdown = true;
        _countdownTimer?.Stop();
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) =>
        {
            if (--_countdownLeft > 0) { ScheduleCountdownText = CountdownLine(_countdownLeft); return; }
            StartScheduledRunNow();
        };
        _countdownTimer.Start();
    }

    private static string CountdownLine(int s) => $"Starting in {s} second{(s == 1 ? "" : "s")}…";

    public void EndScheduleCountdown()
    {
        _countdownTimer?.Stop(); _countdownTimer = null;
        ShowScheduleCountdown = false;
    }

    [RelayCommand]
    private void StartScheduledRunNow()
    {
        EndScheduleCountdown();
        if (_host.Busy || _host.DownloadRunning)
        {
            _host.Log("Scheduled backup waits - a backup is already running. It starts when that one finishes.", category: LogCategory.Download);
            return;
        }
        _firedSlot = _nextScheduledFire;   // kept so a refused run can hand the slot back
        AdvancePastSlot();
        bool quiet = _quietRetry; _quietRetry = false;
        if (!quiet) _host.Log("Scheduled backup starting…", category: LogCategory.Download);
        _host.StartScheduledBackup();
    }

    private DateTimeOffset? _firedSlot;
    private string? _lastWaitReason;
    private bool _quietRetry;

    public void NoteScheduledRunRefused(string reason, bool transient = true)
    {
        if (!transient)
        {
            _firedSlot = null;
            _host.Log($"Scheduled backup skipped - {reason}. Sign in from the Accounts page.", isError: true, category: LogCategory.Download);
            if (_host.NotifyOnErrors) _host.NotifySkippedSignedOut();
            RecordRunOutcome(Grog.Core.Scheduling.ScheduleRunOutcome.Skipped);
            return;
        }
        if (_firedSlot is { } slot)
        {
            _nextScheduledFire = slot;
            _firedSlot = null;
            RaiseNextFire();
        }
        _quietRetry = true;
        if (reason == _lastWaitReason) return;
        _lastWaitReason = reason;
        _host.Log($"Scheduled backup waits: {reason}. It retries at the next check.", category: LogCategory.Download);
    }

    [RelayCommand]
    private void SkipScheduledRun()
    {
        EndScheduleCountdown();
        AdvancePastSlot();
        _host.Log("Scheduled backup skipped by you. The next slot runs as usual.", category: LogCategory.Download);
        RecordRunOutcome(Grog.Core.Scheduling.ScheduleRunOutcome.Canceled);
    }

    public void RecordRunOutcome(Grog.Core.Scheduling.ScheduleRunOutcome outcome)
    {
        if (Sched is not { } sched) return;
        _firedSlot = null; _lastWaitReason = null; _quietRetry = false;   // the slot settled; a later wait logs afresh
        var now = DateTimeOffset.Now;
        sched.LastAttempt = now;
        sched.LastAttemptOutcome = outcome;
        bool finished = outcome is Grog.Core.Scheduling.ScheduleRunOutcome.Completed
                                or Grog.Core.Scheduling.ScheduleRunOutcome.Partial;
        if (finished) sched.LastRun = now;
        _host.SaveManifest();
        RaiseSchedule();
        if (finished) ScheduleOverdue = false;
        RaiseNextFire();
    }

    [ObservableProperty] private bool _scheduleOverdue;
    partial void OnScheduleOverdueChanged(bool value) => OnPropertyChanged(nameof(ShowFirstUnattendedNotice));
    [ObservableProperty] private string _scheduleOverdueText = "";

    public void CheckOverdueBackupOnLaunch()
    {
        if (!_host.Settings.ShowOverduePrompt) return;   // user ticked "Don't show this again"
        var s = Sched;
        if (s is null || !s.Enabled) return;
        if (!_host.IsConnected) return;   // can't back up while signed out; don't nag

        var now = DateTimeOffset.Now;
        var due = Grog.Core.Scheduling.SchedulePlanner.PreviousFire(s, now);

        if (due is null) return;
        var floor = s.AnchorDate ?? DateTimeOffset.MinValue;

        if (due <= floor) return;
        if (Grog.Core.Scheduling.SchedulePlanner.SlotAlreadyCovered(s.LastRun, due.Value, now)) return;
        var t = due.Value.ToLocalTime();
        var why = Grog.Core.Scheduling.SchedulePlanner.SlotAlreadyCovered(s.LastAttempt, due.Value, now)
                  && s.LastAttemptOutcome is { } lo
            ? ScheduleRunCopy.OverdueBecause(lo)
            : null;
        ScheduleOverdueText = $"A scheduled backup was due {t:dddd, MMM d} at {t:h:mm tt}, {why ?? "but Grog wasn't running."}";
        ScheduleOverdue = true;
    }

    [RelayCommand]
    private async Task RunOverdueBackup() { ScheduleOverdue = false; await _host.RunBackupNow(); }

    [RelayCommand]
    private void DismissOverdue() { ScheduleOverdue = false; }

    public bool DontShowOverdueAgain
    {
        get => !_host.Settings.ShowOverduePrompt;
        set { _host.Settings.ShowOverduePrompt = !value; _host.Settings.SaveSoon(); OnPropertyChanged(); }
    }

    private void PersistSchedule()
    {
        if (Sched is null) return;
        _host.SaveManifest();
        RecomputeNextFire();
    }

    // ---- SCHEDULE
    public string NextRunValue
    {
        get
        {
            var sc = Sched;
            if (sc is null || !sc.Enabled) return "Off";
            if (Grog.Core.Scheduling.SchedulePlanner.NextFire(sc, DateTimeOffset.Now) is not { } f) return "No days";
            var t = f.ToLocalTime();
            return $"{t:MMM d} at {t:h:mm tt}";
        }
    }

    public string NextRunSub
    {
        get
        {
            var sc = Sched;
            if (sc is null || !sc.Enabled) return "Automatic backups are off";
            if (Grog.Core.Scheduling.SchedulePlanner.NextFire(sc, DateTimeOffset.Now) is not { } f)
                return "No days selected";
            var t = f.ToLocalTime();
            var now = DateTimeOffset.Now.ToLocalTime().Date;
            var day = t.Date == now ? "Today" : t.Date == now.AddDays(1) ? "Tomorrow" : $"{t:dddd}";
            return $"{day} · Automatic";
        }
    }
    public IBrush NextRunBrush => Sched is { Enabled: true } ? Palette.InkPrimary : Palette.InkDim;
    public string LastRunValue
    {
        get
        {
            var lr = Sched?.LastRun;
            if (lr is null) return "Never";
            var t = lr.Value.ToLocalTime();
            return $"{t:MMM d} at {t:h:mm tt}";
        }
    }

    public string LastRunResultValue => Sched?.LastAttemptOutcome is { } o
        ? ScheduleRunCopy.Label(o) : "None yet";

    public IBrush LastRunResultBrush => Sched?.LastAttemptOutcome is { } o
        ? SeverityBrush.For(Grog.Core.Scheduling.ScheduleRunOutcomeInfo.SeverityOf(o))
        : Palette.InkDim;

    public string LastRunResultSub
    {
        get
        {
            if (Sched?.LastAttempt is not { } la || Sched?.LastAttemptOutcome is null) return "";
            var ago = $"{Grog.Core.Sync.StatusLede.Describe(DateTimeOffset.Now - la)} ago";
            if (_host.LastRunSummary is { } j && j.State != "running" && j.Total > 0 && j.Command == "backup")
            {
                var line = $"{ago} · {j.Completed} of {j.Total} file{(j.Total == 1 ? "" : "s")}";
                return j.Failed > 0 ? $"{line}, {j.Failed} failed" : line;
            }
            var failed = _host.RunFailed;
            return failed > 0 ? $"{ago} · {failed} file{(failed == 1 ? "" : "s")} failed" : ago;
        }
    }
    public string LastRunSub
    {
        get
        {
            if (Sched?.LastRun is not { } lr) return "";   // redundant with the "Never" value; keep the sub for real runs only
            var t = lr.ToLocalTime();
            return $"{t:dddd} \u00b7 {Grog.Core.Sync.StatusLede.Describe(DateTimeOffset.Now - lr)} ago";
        }
    }

    public IBrush LastRunSubBrush => Palette.InkDim;
    public IBrush LastRunBrush => Sched?.LastRun is null ? Palette.InkDim : Palette.InkPrimary;
    public void RaiseReadouts() => RaiseSchedule();

    private void RaiseSchedule()
    {
        OnPropertyChanged(nameof(NextRunValue)); OnPropertyChanged(nameof(NextRunSub)); OnPropertyChanged(nameof(NextRunBrush));
        OnPropertyChanged(nameof(LastRunValue)); OnPropertyChanged(nameof(LastRunSub)); OnPropertyChanged(nameof(LastRunBrush));
        OnPropertyChanged(nameof(LastRunResultValue)); OnPropertyChanged(nameof(LastRunResultSub)); OnPropertyChanged(nameof(LastRunResultBrush));
    }


    // ---- Bound settings surface ----

    public bool ScheduleEnabled
    {
        get => Sched?.Enabled ?? false;
        set
        {
            var s = Sched; if (s is null) return;
            if (s.Enabled == value)
            {
                // Re-raise even on no-op: bindings evaluated before the manifest loaded may be stale.
                OnPropertyChanged(); RaiseScheduleShape();
                return;
            }
            s.Enabled = value;
            if (value) s.AnchorDate = DateTimeOffset.Now;
            OnPropertyChanged();
            RaiseScheduleShape();
            PersistSchedule();
        }
    }

    private Grog.Core.Scheduling.ScheduleFrequency SchedFreq => Sched?.Frequency ?? Grog.Core.Scheduling.ScheduleFrequency.Daily;
    private void SetFrequency(Grog.Core.Scheduling.ScheduleFrequency f)
    {
        var s = Sched; if (s is null || s.Frequency == f) return;
        s.Frequency = f;
        RaiseScheduleShape();
        PersistSchedule();
    }
    public bool IsFreqDaily   { get => SchedFreq == Grog.Core.Scheduling.ScheduleFrequency.Daily;   set { if (value) SetFrequency(Grog.Core.Scheduling.ScheduleFrequency.Daily); } }
    public bool IsFreqWeekly  { get => SchedFreq == Grog.Core.Scheduling.ScheduleFrequency.Weekly;  set { if (value) SetFrequency(Grog.Core.Scheduling.ScheduleFrequency.Weekly); } }
    public bool IsFreqMonthly { get => SchedFreq == Grog.Core.Scheduling.ScheduleFrequency.Monthly; set { if (value) SetFrequency(Grog.Core.Scheduling.ScheduleFrequency.Monthly); } }
    public bool IsFreqCustom  { get => SchedFreq == Grog.Core.Scheduling.ScheduleFrequency.Custom;  set { if (value) SetFrequency(Grog.Core.Scheduling.ScheduleFrequency.Custom); } }

    public bool ShowWeeklyDay  => SchedFreq == Grog.Core.Scheduling.ScheduleFrequency.Weekly;
    public bool ShowMonthlyDay => SchedFreq == Grog.Core.Scheduling.ScheduleFrequency.Monthly;
    public bool ShowCustomDays => SchedFreq == Grog.Core.Scheduling.ScheduleFrequency.Custom;

    private void RaiseScheduleShape()
    {
        OnPropertyChanged(nameof(IsFreqDaily)); OnPropertyChanged(nameof(IsFreqWeekly));
        OnPropertyChanged(nameof(IsFreqMonthly)); OnPropertyChanged(nameof(IsFreqCustom));
        OnPropertyChanged(nameof(ShowWeeklyDay));
        OnPropertyChanged(nameof(ShowMonthlyDay)); OnPropertyChanged(nameof(ShowCustomDays));
    }

    public void RaiseScheduleBindings()
    {
        OnPropertyChanged(nameof(ScheduleEnabled));
        RaiseScheduleShape();
        OnPropertyChanged(nameof(ScheduleTime));
        OnPropertyChanged(nameof(SelectedWeeklyDay));
        OnPropertyChanged(nameof(ScheduleMonthlyDay));
        OnPropertyChanged(nameof(ScheduleIntervalWeeks));
        OnPropertyChanged(nameof(CustomSun)); OnPropertyChanged(nameof(CustomMon)); OnPropertyChanged(nameof(CustomTue));
        OnPropertyChanged(nameof(CustomWed)); OnPropertyChanged(nameof(CustomThu)); OnPropertyChanged(nameof(CustomFri));
        OnPropertyChanged(nameof(CustomSat));
        RaiseNextFire();
    }

    public TimeSpan? ScheduleTime
    {
        get => Sched is { } s ? TimeSpan.FromMinutes(Math.Clamp(s.TimeOfDayMinutes, 0, 1439)) : TimeSpan.FromHours(3);
        set
        {
            var s = Sched; if (s is null || value is not { } t) return;
            int mins = Math.Clamp((int)t.TotalMinutes, 0, 1439);
            if (s.TimeOfDayMinutes == mins) return;
            s.TimeOfDayMinutes = mins;
            PersistSchedule();
            OnPropertyChanged(); OnPropertyChanged(nameof(NextFireLine));
        }
    }

    private static readonly WeekdayOption[] _weekdays = Enumerable.Range(0, 7)
        .Select(i => new WeekdayOption((DayOfWeek)i, ((DayOfWeek)i).ToString())).ToArray();
    public System.Collections.Generic.IReadOnlyList<WeekdayOption> WeekdayChoices => _weekdays;
    public WeekdayOption? SelectedWeeklyDay
    {
        get { var s = Sched; return s is null ? _weekdays[0] : _weekdays[(int)s.WeeklyDay]; }
        set { var s = Sched; if (s is null || value is null || s.WeeklyDay == value.Day) return;
              s.WeeklyDay = value.Day; OnPropertyChanged(); PersistSchedule(); }
    }

    public decimal ScheduleMonthlyDay
    {
        get => Sched?.MonthlyDay ?? 1;
        set { var s = Sched; int v = Math.Clamp((int)value, 1, 31); if (s is null || s.MonthlyDay == v) return;
              s.MonthlyDay = v; OnPropertyChanged(); PersistSchedule(); }
    }

    public decimal ScheduleIntervalWeeks
    {
        get => Sched?.IntervalWeeks ?? 2;
        set { var s = Sched; int v = Math.Clamp((int)value, 1, 12); if (s is null || s.IntervalWeeks == v) return;
              s.IntervalWeeks = v; OnPropertyChanged(); PersistSchedule(); }
    }

    private bool GetCustomDay(DayOfWeek d) => Sched?.CustomDays.Contains(d) ?? false;
    private void SetCustomDay(DayOfWeek d, bool on)
    {
        var s = Sched; if (s is null) return;
        bool has = s.CustomDays.Contains(d);
        if (!on && !has || on && has) return;
        // A list write on the manifest graph: under the gate when a store is bound.
        var gate = _host.ManifestGate;
        if (gate is not null)
        {
            using (gate.Enter()) { if (on) s.CustomDays.Add(d); else s.CustomDays.Remove(d); }
        }
        else if (on) s.CustomDays.Add(d); else s.CustomDays.Remove(d);

        RaiseNextFire();
        PersistSchedule();
    }
    public bool CustomSun { get => GetCustomDay(DayOfWeek.Sunday);    set => SetCustomDay(DayOfWeek.Sunday, value); }
    public bool CustomMon { get => GetCustomDay(DayOfWeek.Monday);    set => SetCustomDay(DayOfWeek.Monday, value); }
    public bool CustomTue { get => GetCustomDay(DayOfWeek.Tuesday);   set => SetCustomDay(DayOfWeek.Tuesday, value); }
    public bool CustomWed { get => GetCustomDay(DayOfWeek.Wednesday); set => SetCustomDay(DayOfWeek.Wednesday, value); }
    public bool CustomThu { get => GetCustomDay(DayOfWeek.Thursday);  set => SetCustomDay(DayOfWeek.Thursday, value); }
    public bool CustomFri { get => GetCustomDay(DayOfWeek.Friday);    set => SetCustomDay(DayOfWeek.Friday, value); }
    public bool CustomSat { get => GetCustomDay(DayOfWeek.Saturday);  set => SetCustomDay(DayOfWeek.Saturday, value); }

    public string NextFireLine
    {
        get
        {
            var s = Sched;
            if (s is null || !s.Enabled) return "Automatic backups are off.";
            var f = Grog.Core.Scheduling.SchedulePlanner.NextFire(s, DateTimeOffset.Now);
            if (f is null) return "Pick at least one day for the schedule to run.";
            var t = f.Value.ToLocalTime();
            return $"Next backup {t:dddd, MMM d} at {t:h:mm tt}";
        }
    }

    public string LastRunLine
    {
        get
        {
            var s = Sched;
            if (s is null || !s.Enabled) return "";
            return s.LastRun is { } lr ? $"Last run {lr.ToLocalTime():MMM d, h:mm tt}" : "";
        }
    }
    public bool HasLastRunLine => LastRunLine.Length > 0;


    public bool ShowFirstUnattendedNotice => !string.IsNullOrEmpty(_host.Settings.FirstUnattendedRunSummary) && !ScheduleOverdue && !ShowScheduleCountdown;
    public string FirstUnattendedRunText => _host.Settings.FirstUnattendedRunSummary;

    public void NoteFirstUnattendedRun()
    {
        if (_host.Settings.FirstUnattendedRunNoticed) return;
        var when = DateTimeOffset.Now;
        var cap = _host.ScopeCaption.Split(" · ", 2);
        var scope = cap.Length == 2 ? $"{cap[0].ToLowerInvariant()} ({cap[1]})" : cap[0].ToLowerInvariant();
        var files = _host.RunFilesDone;
        var bytes = Grog.Core.Format.ByteFormat.Size(_host.RunBytesSettled);
        var where = string.IsNullOrEmpty(_host.BackupRoot) ? "your storage" : _host.BackupRoot;
        var text = files > 0
            ? $"{when:dddd h:mm tt}: {files} file{(files == 1 ? "" : "s")} ({bytes}) of {scope} were backed up to {where}. "
              + "What gets backed up is set in Settings; where it goes, on the Storage page."
            : $"{when:dddd h:mm tt}: the scheduled backup ran and found nothing new to fetch for {scope}. "
              + "What gets backed up is set in Settings; where it goes, on the Storage page.";
        _host.Settings.FirstUnattendedRunSummary = text;
        _host.Settings.FirstUnattendedRunNoticed = true;
        _host.Settings.SaveSoon();
        if (Dispatcher.UIThread.CheckAccess()) RaiseFirstUnattendedNotice();
        else Dispatcher.UIThread.Post(RaiseFirstUnattendedNotice);
    }

    private void RaiseFirstUnattendedNotice()
    {
        OnPropertyChanged(nameof(ShowFirstUnattendedNotice)); OnPropertyChanged(nameof(FirstUnattendedRunText));
    }

    [RelayCommand]
    private void DismissFirstUnattendedNotice()
    {
        _host.Settings.FirstUnattendedRunSummary = ""; _host.Settings.SaveSoon();
        RaiseFirstUnattendedNotice();
    }

    [RelayCommand]
    private void OpenSettingsFromNotice() { DismissFirstUnattendedNotice(); _host.Navigate("Settings"); }

    internal void SeedFirstUnattendedNotice(string text)
    {
        _host.Settings.FirstUnattendedRunSummary = text;
        RaiseFirstUnattendedNotice();
    }
}

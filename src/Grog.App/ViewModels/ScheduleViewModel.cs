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

/// <summary>
/// Automatic backups: the in-app timer, next-fire math, the five-second countdown, overdue-on-launch, the
/// morning-after notice, and the Settings card's bound surface. Reads the window through
/// <see cref="IScheduleHost"/>; every bound name is unchanged from the four MainWindowViewModel partials
/// it was gathered from on 2026-09-02, so the Settings card, the Overview SCHEDULE card and the three
/// foot cards rebind by DataContext alone. Both timers are DispatcherTimers created on the UI thread by
/// host calls, never in the constructor, so a test constructs this freely.
/// </summary>
public sealed partial class ScheduleViewModel : ObservableObject
{
    private readonly IScheduleHost _host;
    internal ScheduleViewModel(IScheduleHost host) => _host = host;

    // Off by default. When enabled, an in-app timer fires a Sync-backup on the configured cadence while Grog
    // is open (or in tray). NOT an OS task: a slot that elapses while the app is closed is simply skipped.

    private DispatcherTimer? _scheduleTimer;
    /// <summary>The next instant we intend to fire, held in memory. Recomputed whenever the schedule changes
    /// and after each run; a fire only happens once "now" crosses it.</summary>
    private DateTimeOffset? _nextScheduledFire;
    /// <summary>Test seam: put the slot in the past so a tick is due without waiting for the clock.</summary>
    internal DateTimeOffset? NextScheduledFireForTests { set => _nextScheduledFire = value; }

    private Grog.Core.Scheduling.BackupSchedule? Sched => _host.Schedule;

    /// <summary>Host entry: arm the 30s timer once services are ready. Nothing runs before this call.</summary>
    public void StartScheduleTimer()
    {
        RecomputeNextFire();
        if (_scheduleTimer is not null) return;
        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _scheduleTimer.Tick += (_, _) => ScheduleTick();
        _scheduleTimer.Start();
    }

    /// <summary>The next-fire readouts (Overview card + Health sub-line), raised as ONE set from every site
    /// that recomputes the fire time.</summary>
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
        // The schedule readouts are clock-dependent ("Today, 3:00 AM" goes stale the moment 3am passes),
        // so re-raise every tick regardless of firing.
        RaiseSchedule();
        _host.RaiseHealthStatus();
        if (s is null || !s.Enabled) return;
        if (_nextScheduledFire is not { } fire) { RecomputeNextFire(); return; }
        // ONE reading of the clock for the whole tick. Two calls to Now can straddle the slot and disagree
        // with each other about whether it is due.
        var now = DateTimeOffset.Now;
        if (now < fire) return;

        // A backup that already ran at/after this slot covers it: advance past the slot instead of firing a
        // redundant run. Shared predicate, never hand-rolled -- it also rejects a stamp in the FUTURE.
        if (Grog.Core.Scheduling.SchedulePlanner.SlotAlreadyCovered(s.LastRun, fire, now))
        {
            _nextScheduledFire = Grog.Core.Scheduling.SchedulePlanner.NextFire(s, DateTimeOffset.Now);
            RaiseNextFire();
            return;
        }

        // Time to fire. If a backup / move is already running, leave _nextScheduledFire in the past so the
        // next tick retries once the slot is free rather than skipping this run entirely. The countdown holds the
        // slot the same way: it advances only once the run is accepted (or the user skips it).
        if (ShowScheduleCountdown) return;
        if (_host.Busy || _host.DownloadRunning) return;
        // A retry after a passing refusal (a move held the slot, a run was live) already announced itself: no
        // second countdown card, no second "starting" line.
        if (_quietRetry) { StartScheduledRunNow(); return; }

        // Signed out entirely: an unattended slot must never pop a login or gate dialog -- log, toast if
        // enabled, try again next slot. Partially signed out still runs; those accounts are skipped.
        if (!_host.IsConnected)
        {
            AdvancePastSlot();
            _host.Log("Scheduled backup skipped - no account is signed in. Sign in from the Accounts page.",
                isError: true, category: LogCategory.Download);
            if (_host.NotifyOnErrors) _host.NotifySkippedSignedOut();
            // Stamp the slot as ended: the next launch must not report "Grog wasn't running" when it was.
            RecordRunOutcome(Grog.Core.Scheduling.ScheduleRunOutcome.Skipped);
            return;
        }
        BeginScheduleCountdown();
    }

    /// <summary>Advance to the following slot once this one is settled (run accepted, skipped, or signed out), so a
    /// long backup cannot re-trigger the same slot. The "last backup" stamp is written when the run COMPLETES.</summary>
    private void AdvancePastSlot()
    {
        _nextScheduledFire = Grog.Core.Scheduling.SchedulePlanner.NextFire(Sched, DateTimeOffset.Now);
        RaiseNextFire();
    }

    // ---- Fire-time countdown (owner 09-02): a scheduled run announces itself for five seconds with
    // Start now / Skip this run, so someone at the keyboard is never surprised by a download starting.
    // Unattended (tray, asleep) it simply elapses; five seconds is noise against a backup.
    public const int ScheduleCountdownSeconds = 5;
    [ObservableProperty] private bool _showScheduleCountdown;
    partial void OnShowScheduleCountdownChanged(bool value) => OnPropertyChanged(nameof(ShowFirstUnattendedNotice));   // shares the bottom slot
    [ObservableProperty] private string _scheduleCountdownText = "";
    private DispatcherTimer? _countdownTimer;
    private int _countdownLeft;

    private void BeginScheduleCountdown()
    {
        // Nobody can click Start/Skip on a window hidden in the tray: run at once, as before the countdown.
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
        // A manual run may have started during the countdown: the slot stays where it is (in the past) and the
        // next tick retries once that run is over, instead of losing the slot to a "starting" line.
        if (_host.Busy || _host.DownloadRunning)
        {
            _host.Log("Scheduled backup waits - a backup is already running. It starts when that one finishes.", category: LogCategory.Download);
            return;
        }
        _firedSlot = _nextScheduledFire;   // kept so a refused run can hand the slot back
        AdvancePastSlot();
        bool quiet = _quietRetry; _quietRetry = false;
        if (!quiet) _host.Log("Scheduled backup starting…", category: LogCategory.Download);
        // Chip-blind: content chips are VIEW state and Scope alone decides what is backed up, so an
        // unattended run covers the full scope regardless of how the Library was left filtered.
        _host.StartScheduledBackup();
    }

    /// <summary>The slot the current unattended run was fired for, until it is accepted or handed back.</summary>
    private DateTimeOffset? _firedSlot;
    private string? _lastWaitReason;
    /// <summary>The held slot's next attempt goes straight to the run: no countdown card, no "starting" line.</summary>
    private bool _quietRetry;

    /// <summary>Host callback: the unattended run was refused before it started. A passing reason (a move held
    /// the activity slot, a run was live) hands the slot back so the next tick retries quietly, exactly as a Busy
    /// tick holds it; the wait is logged once per distinct reason. A reason a retry cannot clear (sign-in is not
    /// ready) settles the slot like the signed-out branch: advance, log, stamp Skipped, so no tick pops a login.</summary>
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
        // A deliberate skip is an attempt that ended, not a completion: reword the overdue prompt, keep LastRun.
        RecordRunOutcome(Grog.Core.Scheduling.ScheduleRunOutcome.Canceled);
    }

    /// <summary>Stamp the end of a run: every ending stamps LastAttempt/LastAttemptOutcome, but only
    /// Completed/Partial stamps LastRun -- a canceled or skipped slot must reword the overdue prompt, not silence it.</summary>
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
        // Completion/error toasts come from the download-engine completion callback; nothing to announce here.
    }

    // Overdue-on-launch: at startup, if a due slot elapsed with no backup since, prompt.
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
        // Only count slots after the schedule was switched on; enabling it must not claim a pre-schedule miss.
        var floor = s.AnchorDate ?? DateTimeOffset.MinValue;
        if (due <= floor) return;
        // Already backed up on/after the due slot; same shared predicate the tick uses (rejects future stamps).
        if (Grog.Core.Scheduling.SchedulePlanner.SlotAlreadyCovered(s.LastRun, due.Value, now)) return;
        var t = due.Value.ToLocalTime();
        // The stamped outcome says WHY the slot was missed (stopped, skipped-signed-out, app closed), so the
        // banner reports the recorded reason instead of guessing "Grog wasn't running".
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

    /// <summary>"Don't show this again" on the overdue banner -- stops future overdue prompts. Persists
    /// immediately (the inverse of AppSettings.ShowOverduePrompt), so ticking it sticks even before dismiss.</summary>
    public bool DontShowOverdueAgain
    {
        get => !_host.Settings.ShowOverduePrompt;
        set { _host.Settings.ShowOverduePrompt = !value; _host.Settings.SaveSoon(); OnPropertyChanged(); }
    }

    /// <summary>Persist the schedule after a UI edit and re-arm the timer / refresh the summary.</summary>
    private void PersistSchedule()
    {
        if (Sched is null) return;
        _host.SaveManifest();
        RecomputeNextFire();
    }

    // ---- SCHEDULE section: two cards, Next Run and Last Run ----
    /// <summary>Same shape as LAST COMPLETED RUN beside it: stamp on the value line, day word on the sub-line.
    /// Not the Settings NextFireLine shape: a leading day word wraps in a 208px card column (measured 1366).</summary>
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
            // The value is the single word "Off" (a stamp); the sentence-length explanation lives here.
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
            // Date and time on one row; the weekday sits on the sub-line so the stamp never wraps
            // in a third-width column.
            var t = lr.Value.ToLocalTime();
            return $"{t:MMM d} at {t:h:mm tt}";
        }
    }

    /// <summary>How the last run ENDED, distinct from the last run that FINISHED beside it -- when they
    /// disagree (a canceled slot after a good one), that disagreement is the point.</summary>
    public string LastRunResultValue => Sched?.LastAttemptOutcome is { } o
        ? ScheduleRunCopy.Label(o) : "None yet";

    /// <summary>Through the SHARED severity map, never a hand-rolled brush: Completed grades Quiet (plain ink);
    /// green is the assumed state and is never reported.</summary>
    public IBrush LastRunResultBrush => Sched?.LastAttemptOutcome is { } o
        ? SeverityBrush.For(Grog.Core.Scheduling.ScheduleRunOutcomeInfo.SeverityOf(o))
        : Palette.InkDim;

    /// <summary>When it ended, plus the failure count when this run left files behind -- the count sits under
    /// the word that says "Partial", not under a completion timestamp. Empty in the "None yet" state.</summary>
    public string LastRunResultSub
    {
        get
        {
            if (Sched?.LastAttempt is not { } la || Sched?.LastAttemptOutcome is null) return "";
            var ago = $"{Grog.Core.Sync.StatusLede.Describe(DateTimeOffset.Now - la)} ago";
            // The journal is the durable record: its counts survive a restart, where the in-memory run
            // tally does not. A journal from a run still going belongs to the live card, not to this one.
            // Only a BACKUP run's journal: a one-file click ("download") writes the same current.json and read as the
            // scheduled run's "1 of 1 files" (QA 09-30 C11).
            if (_host.LastRunSummary is { } j && j.State != "running" && j.Total > 0 && j.Command == "backup")
            {
                var line = $"{ago} · {j.Completed} of {j.Total} file{(j.Total == 1 ? "" : "s")}";
                return j.Failed > 0 ? $"{line}, {j.Failed} failed" : line;
            }
            var failed = _host.RunFailed;
            return failed > 0 ? $"{ago} · {failed} file{(failed == 1 ? "" : "s")} failed" : ago;
        }
    }
    /// <summary>The WEEKDAY (the half of the stamp displaced off the value line), then how long ago.
    /// LastRun stamps whenever a run ENDS unpaused, including one that gave up on files.</summary>
    public string LastRunSub
    {
        get
        {
            if (Sched?.LastRun is not { } lr) return "";   // redundant with the "Never" value; keep the sub for real runs only
            var t = lr.ToLocalTime();
            return $"{t:dddd} \u00b7 {Grog.Core.Sync.StatusLede.Describe(DateTimeOffset.Now - lr)} ago";
        }
    }

    /// <summary>Plain dim, always: a weekday and an interval are neutral facts, never color-flagged.</summary>
    public IBrush LastRunSubBrush => Palette.InkDim;
    public IBrush LastRunBrush => Sched?.LastRun is null ? Palette.InkDim : Palette.InkPrimary;
    /// <summary>Re-raise the Overview card readouts; the host calls this from its health raise because the "ago" lines are clock-bound.</summary>
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
            // EVERY enable re-stamps the anchor. It was stamped only when null, so a schedule switched off for a
            // month and back on kept the old one: the launch check then reported a "missed" backup from a day
            // the schedule was off, and the every-N-weeks cadence counted from a week long gone (sweep 2 #21).
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

    /// <summary>Reveal gates for the frequency rows; keyed to frequency only -- the card shows dimmed until enabled.</summary>
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

    /// <summary>Refresh every scheduler binding from persisted state once services + manifest load;
    /// the Settings view binds at window-create time, before the manifest loads.</summary>
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

    // TimePicker (owner 09-02: the typed box + quarter-hour suggestions read as "only these times" and gave
    // no acceptance signal). Hour / minute / AM-PM spinners at one-minute granularity; the value IS the
    // saved value, and the Next-backup line beside it restates it.
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
        // A list write on the manifest graph: under the gate when a store is bound. (manifest gate 09-08)
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

    /// <summary>Next-run half of the schedule summary; split from the last-run half so it can sit
    /// inline on the "At" row while the last-run line stays below the block.</summary>
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

    /// <summary>Last-run half; empty when none so the gated TextBlock leaves no blank line
    /// (an empty-string TextBlock still measures a full line).</summary>
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



    // ---- Morning-after notice: the first scheduled backup explains itself once (PO, 09-01) ----
    // A stranger who skipped the tour, connected and set a slot never saw a scan summary or the scope.
    // The Overview's numbers are all there the next morning; this is the one sentence that ties them.
    public bool ShowFirstUnattendedNotice => !string.IsNullOrEmpty(_host.Settings.FirstUnattendedRunSummary) && !ScheduleOverdue && !ShowScheduleCountdown;
    public string FirstUnattendedRunText => _host.Settings.FirstUnattendedRunSummary;

    /// <summary>Called by the host on the UI thread after an unattended run settles.</summary>
    public void NoteFirstUnattendedRun()
    {
        if (_host.Settings.FirstUnattendedRunNoticed) return;
        var when = DateTimeOffset.Now;
        // "Games & Extras · English" -> "games & extras (English)": the kind reads as prose, the
        // language keeps its capital.
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
        // The raise alone is deferred one dispatcher turn (the pre-split shape); the text above is already
        // composed from the counters as they stood when the run settled. Headless tests never arm a
        // dispatcher, so fall back to a direct raise when there is no UI thread to post to.
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

    /// <summary>Assistant-only: seed the notice so the harness can render it without an overnight run.</summary>
    internal void SeedFirstUnattendedNotice(string text)
    {
        _host.Settings.FirstUnattendedRunSummary = text;
        RaiseFirstUnattendedNotice();
    }
}

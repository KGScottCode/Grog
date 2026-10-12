// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Download;
using Grog.Core.Format;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Verify;
using Grog.App.Runtime;

namespace Grog.App.ViewModels;

// Layout: the flat-to-category migration offer, the layout-change offer, the interrupted-reorganize resume,
// and resolving a stopped move (put the files back, or accept them where they landed).
public sealed partial class StorageViewModel
{
    private bool _layoutOfferBusy, _migrateCheckBusy;

    internal async Task OfferLayoutReorg()
    {
        if (_session.Manifest is null || string.IsNullOrEmpty(_session.BackupRoot)) return;
        if (_layoutOfferBusy) return;   // 
        _layoutOfferBusy = true;
        try
        {
            // Core plans off the dispatcher (BackupLayout probes every root on disk; the planner is pure).
            var manifest = _session.Manifest; var root = _session.BackupRoot; var layoutAtStart = _root.Settings.ExtrasLayoutValue;
            var plan = await Grog.Core.Runs.LayoutMigrationRun.ForLayoutChangeAsync(manifest, root, _session.Cts?.Token ?? default);
            // the plan is for the layout that was current when we started.
            if (!ReferenceEquals(_session.Manifest, manifest) || _session.BackupRoot != root || _root.Settings.ExtrasLayoutValue != layoutAtStart) return;
            if (plan is null) return;   // nothing on disk is in the wrong place
            if (plan.HasUnavailableDrives) { BlockMoveForDrives(plan.UnavailableDrives); return; }

            MigrateSummary = $"{plan.Count} file(s) across {plan.Games} game(s) are still in the previous layout. "
                           + "Grog can move them to match. Nothing is deleted, and you can leave them where they are.";
            OnPropertyChanged(nameof(MigrateSummary));
            _layoutPlan = plan;
            ShowMigratePrompt = true;
        }
        catch (Exception ex) { _root.Log($"Couldn't work out what to move: {ex.Message}", isError: true); }
        finally { _layoutOfferBusy = false; }
    }

    private Grog.Core.Runs.LayoutMigrationPlan? _layoutPlan;
    private Grog.Core.Volumes.ReorgJob? _supersededJob;

    internal void OfferExtrasPlacementPlan(Grog.Core.Runs.LayoutMigrationPlan plan)
    {
        if (plan.HasUnavailableDrives) { BlockMoveForDrives(plan.UnavailableDrives); return; }
        if (plan.IsEmpty) return;
        MigrateSummary = $"{plan.Count} file(s) across {plan.Games} game(s) are not where the new setting puts them. "
                       + "Grog can move them now. Nothing is deleted, and you can leave them where they are.";
        OnPropertyChanged(nameof(MigrateSummary));
        _layoutPlan = plan; _layoutPlanIsExtras = true;
        ShowMigratePrompt = true;
    }
    private bool _layoutPlanIsExtras;

    // ---- Layout migration (flat -> Games/Extras/Cloud Saves) ----
    [ObservableProperty] private bool _showMigratePrompt;
    public string MigrateSummary { get; internal set; } = "";   // internal: the headless harness seeds it

    // "Don't ask again" lives in the MANIFEST Settings (a property of the backup, so it travels with the
    // drive). It only silences THIS automatic on-load offer -- a deliberate layout change in Settings
    // still prompts via OfferLayoutReorg, because the user just asked for it.
    private const string SuppressLayoutPromptKey = "SuppressLayoutReorgPrompt";
    [ObservableProperty] private bool _migrateDontAskAgain;

    internal async void CheckLayoutMigration()
    {
        if (_session.Manifest is null || string.IsNullOrEmpty(_session.BackupRoot)) return;
        // Its own flag: sharing OfferLayoutReorg's dropped the flat-layout prompt on every
        // layout change from Settings, since the offer always held the flag first. The two prompts are
        // different plans (layout change vs old flat layout) and were both shown before the sweep.
        if (_migrateCheckBusy || ShowMigratePrompt) return;
        _migrateCheckBusy = true;
        try
        {
            if (_session.Manifest.Current.Settings.TryGetValue(SuppressLayoutPromptKey, out var v) && v == "1") return;
            var manifest = _session.Manifest; var root = _session.BackupRoot;
            // Core plans off the dispatcher: the journal read (an interrupted reorganize OWNS the misplaced
            // files, so no second offer), the layout's root probes, and one File.Exists per planned move (only
            // what is ACTUALLY on disk is offered).
            var plan = await Grog.Core.Runs.LayoutMigrationRun.PlanAsync(manifest, root, _session.Cts?.Token ?? default);
            if (plan is null || !ReferenceEquals(_session.Manifest, manifest) || _session.BackupRoot != root) return;
            _layoutPlan = plan;
            MigrateSummary = $"{plan.Count} file(s) across {plan.Games} game(s) are in the old flat layout. " +
                             "Grog can move them into the new Games and Extras folders. Nothing is deleted; they are only reorganized.";
            OnPropertyChanged(nameof(MigrateSummary));
            MigrateDontAskAgain = false;   // fresh tick each time the prompt opens
            ShowMigratePrompt = true;
        }
        catch { /* never block startup on this */ }
        finally { _migrateCheckBusy = false; }
    }

    private void PersistMigrateDontAskAgain()
    {
        if (!MigrateDontAskAgain || _session.Manifest is null) return;
        _session.Manifest.Mutate(m => m.Settings[SuppressLayoutPromptKey] = "1");   // 
        _session.Manifest.SaveSoon();   // a suppress flag

    }

    // AllowConcurrentExecutions: an async command is DISABLED while its last run is still awaited, and that run is
    // the whole move. A second "Reorganize Now" asked while a move was running showed a dead button.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task MigrateNow()
    {
        ShowMigratePrompt = false;
        if (_session.Manifest is null) return;
        var plan = _layoutPlan;
        _layoutPlan = null;
        if (plan is null) return;

        // THE LATEST DECISION WINS. A move still running was planned for a setting that has just been
        // replaced: the file being copied right now is abandoned (its source is untouched until a copy is verified,
        // so it simply stays where it was), the files still waiting are dropped, and the plan is made again against
        // where everything actually is now. No "what about the files already moved?" question: the new plan answers it.
        bool fromExtrasCard = _layoutPlanIsExtras; _layoutPlanIsExtras = false;
        bool replan = fromExtrasCard;
        if (fromExtrasCard && IsReorgRunning && _reorgControl is { } running)
        {
            // Counted from the job as it stands: what has landed, of what it held (joined files included).
            var (done, total) = _activeReorgJob is { } aj ? (aj.Settled, aj.Total) : (0, 0);
            _root.Log($"Setting changed mid-move: stopped after {done} of {Grog.Core.Format.Plural.Of(total, "file")}; planning the rest again.", category: LogCategory.Move);
            _supersededJob = _activeReorgJob;   // by identity, not by a time window: its end-of-run pass lands later
            running.Cancel();
            for (int i = 0; i < 1200 && IsReorgRunning; i++) await Task.Delay(100);   // a multi-GB copy stops at its next buffer
            if (IsReorgRunning) { _root.Log("The running move did not stop in time; the new move was not started. Try again when it ends.", isError: true, category: LogCategory.Move); return; }
            ClearReorgSurfaces();
            replan = true;
        }
        if (replan)
        {
            var m = _session.Manifest.Current;
            var extrasRoot = m.Routing.RoleRootOrPrimary(Grog.Core.Models.ContentRole.Extras, m.PrimaryRootId);
            if (string.IsNullOrEmpty(extrasRoot)) return;
            plan = await Grog.Core.Runs.ExtrasPlacementRun.ApplyAndPlanAsync(_session.Manifest, _session.BackupRoot, m.ExtrasLayout, extrasRoot!, _session.Cts?.Token ?? default);
            if (plan is null || plan.IsEmpty) { _root.Log("Nothing is out of place after stopping the move.", category: LogCategory.Move); return; }
            if (plan.HasUnavailableDrives) { BlockMoveForDrives(plan.UnavailableDrives); return; }
        }
        // Resolve (config-dir probe) runs off the dispatcher.
        var manifest = _session.Manifest; var root = _session.BackupRoot;
        var configDir = await Task.Run(() => Grog.Core.Storage.GrogPaths.Resolve(root).ConfigDir);
        if (BlockIfDrivesUnavailable(plan.Job)) return;
        // The pane drives plan.Job; Core's LayoutMigrationRun journals, runs and saves it.
        await RunReorgWithPaneAsync(plan.Job, configDir,
            execute: (progress, control) => Grog.Core.Runs.LayoutMigrationRun.RunAsync(manifest, root, plan,
                new MainWindowViewModel.AppRunHost(_root, LogCategory.Move), progress, CancellationToken.None, control, configDir));
    }

    [RelayCommand]
    private void SkipMigrate()
    {
        ShowMigratePrompt = false;
        _layoutPlan = null;
        PersistMigrateDontAskAgain();
        _root.Log("Left the existing backups where they are. New files will use the layout you chose.");
    }

    // ---- Reorg resume: surface an interrupted reorganize on launch (Resume is the default) --------
    [ObservableProperty] private bool _showReorgResumePrompt;
    [ObservableProperty] private string _reorgResumeSummary = "";
    [ObservableProperty] private bool _showReorgCancelConfirm;

    internal async void CheckReorgResume()
    {
        if (_session.Manifest is null || string.IsNullOrEmpty(_session.BackupRoot)) return;
        try
        {
            // journal read + config-dir probe off the dispatcher (this runs at startup).
            var root = _session.BackupRoot;
            var pending = await Task.Run(() =>
                new Grog.Core.Volumes.ReorgJournalStore(Grog.Core.Storage.GrogPaths.Resolve(root).ConfigDir).Load());
            if (pending is null || pending.IsComplete || _session.BackupRoot != root) return;

            // A job the user already STOPPED is a different question from one that was merely interrupted:
            // it needs resolving (put the files back, or accept them), not a "resume?" prompt.
            if (pending.Canceled) { OpenStopChoice(pending); return; }

            ReorgResumeSummary = $"A reorganize was interrupted -- {pending.Settled} of {pending.Total} file(s) were moved. " +
                                 "Resume to finish moving the rest. Nothing was deleted.";
            ShowReorgResumePrompt = true;
        }
        catch { /* never block startup on this */ }
    }

    [RelayCommand]
    private async Task ResumeReorg()
    {
        ShowReorgResumePrompt = false;
        if (_session.Manifest is null || string.IsNullOrEmpty(_session.BackupRoot)) return;
        // journal read + config-dir probe off the dispatcher; the slot is taken inside RunReorgWithPaneAsync.
        var root = _session.BackupRoot;
        var manifest = _session.Manifest; var pendingWrites = _session.PendingWrites;
        var (pending, configDir, dropped) = await Task.Run(() =>
        {
            var dir = Grog.Core.Storage.GrogPaths.Resolve(root).ConfigDir;
            var job = new Grog.Core.Volumes.ReorgJournalStore(dir).Load();
            int n = 0;
            if (job is not null)
            {
                var layout = new Grog.Core.Volumes.BackupLayout(manifest.Current, root);
                // Drives may have come back under another letter since the plan was written, and may have
                // filled since: paths first, then the same room rule a new move applies.
                int rebased = Grog.Core.Volumes.ReorgPlanner.Rebase(job, manifest.Current, layout);
                if (rebased > 0) new Grog.Core.Volumes.ReorgJournalStore(dir).Save(job);
                if (job.Pending().FirstOrDefault(x => x.ToRootId is not null)?.ToRootId is { } target)
                    n = Grog.Core.Volumes.ReorgPlanner.DropWhatNoLongerFits(job, MoveRoomFor(manifest, pendingWrites, layout, target));
            }
            return (job, dir, n);
        });
        if (pending is null) return;
        NoteDroppedOnResume(pending, dropped);
        await RunReorgWithPaneAsync(pending, configDir);
    }

    internal int DropWhatNoLongerFitsFor(Grog.Core.Volumes.ReorgJob job)
    {
        if (_session.Manifest is not { } manifest) return 0;
        var layout = new Grog.Core.Volumes.BackupLayout(manifest.Current, _session.BackupRoot);
        Grog.Core.Volumes.ReorgPlanner.Rebase(job, manifest.Current, layout);   // a drive re-lettered while paused
        if (job.Pending().FirstOrDefault(x => x.ToRootId is not null)?.ToRootId is not { } target) return 0;
        return Grog.Core.Volumes.ReorgPlanner.DropWhatNoLongerFits(job, MoveRoomFor(manifest, _session.PendingWrites, layout, target));
    }
    internal void NoteDroppedOnResume(Grog.Core.Volumes.ReorgJob job, int dropped)
    {
        if (dropped <= 0) return;
        var first = job.NotFitting[0];
        _root.Log($"Resume: {Grog.Core.Format.Plural.Of(dropped, "file")} left in place, {first.Reason} ({ByteFormat.Size(first.SizeBytes)} {first.FileName}).", category: LogCategory.Move);
        _root.ShowToast($"{Grog.Core.Format.Plural.Of(dropped, "file")} of this move no longer fit and stay where they are: {first.Reason}.", 1);
    }

    // ---- Resolving a stopped move: put the files back, or accept them where they landed ----------------
    [ObservableProperty] private bool _showMoveStopChoice;
    [ObservableProperty] private bool _showOrphanConfirm;
    [ObservableProperty] private string _moveStopSummary = "";
    [ObservableProperty] private string _moveStopOrphanWarning = "";
    [ObservableProperty] private string _moveStopWhat = "";
    [ObservableProperty] private string _revertOldFolder = "";
    [ObservableProperty] private string _revertNewFolder = "";
    public string RevertHeadline => $"{MoveStopWhat} was stopped part-way, so Grog is returning the files it "
                                  + "already moved. Nothing is deleted.";
    public string RevertStopWarning => $"The files not yet returned stay in {RevertNewFolder} while Grog looks "
                                     + $"in {RevertOldFolder} - they'll show as missing until you re-adopt them "
                                     + "with Import.";
    partial void OnMoveStopWhatChanged(string value) { OnPropertyChanged(nameof(RevertHeadline)); }
    partial void OnRevertNewFolderChanged(string value) { OnPropertyChanged(nameof(RevertStopWarning)); }
    partial void OnRevertOldFolderChanged(string value) { OnPropertyChanged(nameof(RevertStopWarning)); }
    [ObservableProperty] private bool _reverting;

    private async void OpenStopChoice(Grog.Core.Volumes.ReorgJob? preloaded = null)
    {
        try { await OpenStopChoiceCore(preloaded); }
        catch (Exception ex) { _root.Log($"Couldn't read the stopped move: {ex.Message}", isError: true); }   // async void: never unobserved
    }
    private async Task OpenStopChoiceCore(Grog.Core.Volumes.ReorgJob? preloaded)
    {
        if (string.IsNullOrEmpty(_session.BackupRoot)) return;
        var job = preloaded ?? await LoadStoppedJobAsync();   // journal read off the dispatcher
        if (job is null) return;

        int moved = job.Moves.Count(m => m.State == Grog.Core.Volumes.ReorgMoveState.Done);
        if (moved == 0)
        {
            // Nothing was actually moved, so there is nothing to resolve -- just drop the plan.
            await DiscardStoppedJobAsync();
            _root.Log("Move stopped before anything was moved. Nothing to undo.", category: LogCategory.Move);
            return;
        }

        // ONLY a ChangeFolder job can orphan: PathHint flips only when the WHOLE job finishes, so half-done
        // moved files resolve to the old folder. Layout/Relocate repath each file as it settles -- merely
        // half-migrated, never orphaned, and a scary dialog must not say otherwise.
        bool orphanRisk = job.Reason == Grog.Core.Volumes.ReorgReason.ChangeFolder;

        // A stopped FOLDER move has no good "leave it" answer, so put the files back automatically;
        // stopping the put-back takes a deliberate act with the consequence spelled out.
        MoveStopWhat = DescribeJob(job);
        if (orphanRisk)
        {
            RevertOldFolder = FolderSideOf(job, destination: false);
            RevertNewFolder = FolderSideOf(job, destination: true);
            _root.Log($"{MoveStopWhat} was stopped after {moved} file(s). Putting them back.", category: LogCategory.Move);
            _ = RevertStoppedMoveCore(job);
            return;
        }

        MoveStopSummary = $"{MoveStopWhat} was stopped. {moved} file(s) moved, and Grog is tracking them there.";
        MoveStopOrphanWarning = $"{moved} file(s) stay where they moved to and stay tracked. "
                              + "Your library is just half in each arrangement. Nothing is deleted.";
        ShowMoveStopChoice = true;
    }

    private string DescribeJob(Grog.Core.Volumes.ReorgJob job) => job.Reason switch
    {
        // "Changing the Primary folder" would read as changing WHICH folder is primary. It is the path that
        // moved, so say location.
        Grog.Core.Volumes.ReorgReason.ChangeFolder =>
            $"Changing the location of the {RootLabelForJob(job)} storage",
        Grog.Core.Volumes.ReorgReason.Relocate =>
            $"Moving files to {LabelOfRoot(job.Moves.FirstOrDefault(m => m.ToRootId is not null)?.ToRootId)}",
        _ => "Reorganising your extras",
    };

    private string RootLabelForJob(Grog.Core.Volumes.ReorgJob job)
        => LabelOfRoot(job.Moves.FirstOrDefault()?.FromRootId);

    internal string SlotNameOf(string? rootId)
    {
        if (_session.Manifest is null) return "";
        var id = rootId ?? _session.Manifest.Current.PrimaryRootId;
        if (id == _session.Manifest.Current.PrimaryRootId) return "Primary";
        if (SecondaryLocation is { } sec && sec.RootId == id) return "Secondary";
        return LabelOfRoot(id);
    }

    internal string LabelOfRoot(string? rootId)
    {
        if (_session.Manifest is null || rootId is null) return "backup";
        var r = _session.Manifest.Current.Roots.FirstOrDefault(x => x.Id == rootId);
        if (r is null) return "backup";
        // "Primary" is a role, not a name -- say it when it applies, since that is how the folder is labeled
        // everywhere else in the app.
        return rootId == _session.Manifest.Current.PrimaryRootId ? "Primary" : (string.IsNullOrWhiteSpace(r.Label) ? "backup" : r.Label);
    }

    private static string FolderSideOf(Grog.Core.Volumes.ReorgJob job, bool destination)
    {
        var mv = job.Moves.FirstOrDefault();
        if (mv is null) return "";
        var abs = destination ? mv.ToAbs : mv.FromAbs;
        var rel = destination ? mv.ToRel : mv.FromRel;
        var native = rel.Replace('/', System.IO.Path.DirectorySeparatorChar);
        return abs.EndsWith(native, StringComparison.OrdinalIgnoreCase) && native.Length > 0
            ? abs[..^native.Length].TrimEnd(System.IO.Path.DirectorySeparatorChar)
            : System.IO.Path.GetDirectoryName(abs) ?? "";
    }

    // the stopped-job journal is read/deleted off the dispatcher; the config dir is
    // captured on the UI thread (the active job's, else resolved -- with its disk probe -- in the Task.Run).
    private Task<Grog.Core.Volumes.ReorgJob?> LoadStoppedJobAsync()
    {
        var known = _activeReorgConfigDir; var root = _session.BackupRoot;
        return Task.Run(() =>
        {
            try { return new Grog.Core.Volumes.ReorgJournalStore(known ?? Grog.Core.Storage.GrogPaths.Resolve(root).ConfigDir).Load(); }
            catch { return null; }
        });
    }

    private Task<string> StoppedJobConfigDirAsync()
    {
        var known = _activeReorgConfigDir; var root = _session.BackupRoot;
        return known is not null ? Task.FromResult(known)
             : Task.Run(() => Grog.Core.Storage.GrogPaths.Resolve(root).ConfigDir);
    }

    private async Task DiscardStoppedJobAsync()
    {
        var dir = await StoppedJobConfigDirAsync();
        await Task.Run(() => { try { new Grog.Core.Volumes.ReorgJournalStore(dir).Delete(); } catch { /* best-effort */ } });
        _activeReorgJob = null;
    }

    [RelayCommand]
    private Task RevertStoppedMove() => RevertStoppedMoveCore(null);

    private async Task RevertStoppedMoveCore(Grog.Core.Volumes.ReorgJob? preloaded)
    {
        ShowMoveStopChoice = false;
        // journal read + config dir off the dispatcher, BEFORE the slot as before.
        var configDir = await StoppedJobConfigDirAsync();
        var job = preloaded ?? await LoadStoppedJobAsync();
        if (job is null || _session.Manifest is null) return;
        if (!_root.TryBeginActivitySlot("Can't put the files back right now")) return;
        try
        {
            Reverting = true;
            _revertCts = new CancellationTokenSource();
            var runner = new Grog.Core.Volumes.ReorgRunner(_session.Manifest, new Grog.Core.Volumes.ReorgJournalStore(configDir));
            var res = await Task.Run(() => runner.RevertAsync(job, _revertCts.Token));

            await _root.ReconcileAndRefresh();
            Dispatcher.UIThread.Post(() =>
            {
                RefreshBackupLocations();
                ClearReorgSurfaces();
                var line = $"Put {res.Restored} file(s) back.";
                if (res.Stopped) line += " Stopped before the rest -- those files are still in the new folder.";
                if (res.Unrecoverable > 0) line += $" {res.Unrecoverable} could not be found to put back.";
                if (res.Failed > 0) line += $" {res.Failed} failed.";
                _root.Log(line, isError: !res.Clean, category: LogCategory.Move);
                _root.ShowToast(res.Clean ? "Files put back where they were." : line);
            });
        }
        catch (Exception ex) { _root.Log($"Couldn't put the files back: {ex.Message}", isError: true); }
        finally
        {
            Reverting = false;
            _revertCts?.Dispose();
            _revertCts = null;
            _root.EndActivitySlot();
        }
    }

    // ---- Stopping the put-back: possible, but never casual ---------------------------------------------
    private CancellationTokenSource? _revertCts;
    [ObservableProperty] private bool _showRevertStopConfirm;

    [RelayCommand] private void RequestStopRevert() => ShowRevertStopConfirm = true;
    [RelayCommand] private void BackFromRevertStop() => ShowRevertStopConfirm = false;

    [RelayCommand]
    private void ConfirmStopRevert()
    {
        ShowRevertStopConfirm = false;
        _revertCts?.Cancel();   // cooperative, checked between files -- never mid-copy
        _root.Log("Put-back stopped. Files not yet returned are still in the new folder and will show as missing.",
            isError: true, category: LogCategory.Move);
    }

    // Accepting orphans is the destructive-ish choice, so it gets its own confirm with the count spelled out.
    [RelayCommand] private void RequestAcceptOrphans() { ShowMoveStopChoice = false; ShowOrphanConfirm = true; }
    [RelayCommand] private void BackFromOrphanConfirm() { ShowOrphanConfirm = false; ShowMoveStopChoice = true; }

    [RelayCommand]
    private async Task ConfirmAcceptOrphans()
    {
        ShowOrphanConfirm = false;
        await DiscardStoppedJobAsync();   // 
        ClearReorgSurfaces();
        // Deliberately NOT touching each file's downloaded state: guessing would make health and verify lie.
        // Import re-adopts wanted files; per-file "mark as not downloaded" is the manual escape hatch.
        _root.Log("Move stopped. Already-moved files were left where they are -- use Import to re-adopt them.",
            isError: true, category: LogCategory.Move);
    }
}

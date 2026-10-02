// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Storage;
using Grog.Core.Volumes;

namespace Grog.Core.Runs;

/// <summary>Which question a layout plan answers. The App showed two different prompts for two different
/// plans (final check 09-06) and they must stay distinct: <see cref="FlatLayout"/> is the one-time on-load
/// offer ("files are in the old flat layout"), planned from what is ACTUALLY on disk and withheld while an
/// unfinished reorg journal owns the misplaced files; <see cref="LayoutChange"/> is the offer after the user
/// switched the extras layout in Settings ("files are still in the previous layout"), planned from the
/// manifest alone (never-downloaded leaves are skipped and repathed by the runner).</summary>
public enum LayoutPlanKind { FlatLayout = 0, LayoutChange = 1 }

/// <summary>A planned layout migration: the executable <see cref="Job"/> plus the display facts a host
/// prompts with. <see cref="Moves"/> is what the prompt may promise (for <see cref="LayoutPlanKind.FlatLayout"/>
/// only files present on disk); <see cref="Job"/> is what the runner executes (the whole layout job, exactly
/// as the App's MigrateNow ran it).</summary>
public sealed record LayoutMigrationPlan(
    LayoutPlanKind Kind,
    ReorgJob Job,
    IReadOnlyList<LayoutMigrationService.Move> Moves,
    int Games,
    long Bytes)
{
    public int Count => Moves.Count;
    public bool IsEmpty => Moves.Count == 0;
    /// <summary>Drives the job needs that are offline: a host must block the whole move (never a partial one).</summary>
    public IReadOnlyList<string> UnavailableDrives => Job.UnavailableDrives;
    public bool HasUnavailableDrives => Job.HasUnavailableDrives;
}

/// <summary>How a layout migration ended. <see cref="Canceled"/> covers both the token and a
/// <see cref="ReorgControl"/> cancel; <see cref="Error"/> is a runner-level failure (journal not writable)
/// that the host renders once -- the journal on disk still holds the plan for a later resume.</summary>
public sealed record LayoutMigrationOutcome(
    ReorgOutcome Outcome,
    int Moved,
    int Skipped,
    int Failed,
    bool Canceled,
    string? PauseReason,
    Exception? Error,
    string? PausedForRootId = null)
{
    public bool Completed => Outcome == ReorgOutcome.Completed && Error is null;
}

/// <summary>
/// THE layout migration sequence, shared by the App's migrate prompt and <c>grogcli reorg layout</c>:
/// plan (pure, then one File.Exists per move for the on-load offer), journal, run the
/// <see cref="ReorgRunner"/> to completion with its rename / copy-verify-promote-delete semantics, save.
/// Until 09-08 the App's CheckLayoutMigration / OfferLayoutReorg / MigrateNow and the CLI's Reorg verb
/// each carried a copy of this (and the CLI's never applied the on-disk filter or the journal rule).
/// Disk probes run outside the manifest gate; the runner repaths under it.
/// </summary>
public static class LayoutMigrationRun
{
    /// <summary>The on-load offer (App CheckLayoutMigration): files still in the old flat layout, restricted
    /// to what is actually on disk. Null when nothing needs moving, or when an unfinished reorg journal exists
    /// (that job owns the misplaced files; the resume prompt handles them -- owner-hit 08-30).</summary>
    public static Task<LayoutMigrationPlan?> PlanAsync(IManifestStore store, string backupRoot, CancellationToken ct,
                                                       string? configDir = null)
        => Task.Run<LayoutMigrationPlan?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var pending = new ReorgJournalStore(configDir ?? GrogPaths.Resolve(backupRoot).ConfigDir).Load();
            if (pending is not null && !pending.IsComplete) return null;

            // BackupLayout probes every root on disk: it is built here, off the caller's thread, not under the gate.
            var m = store.Current;
            var layout = new BackupLayout(m, backupRoot);
            List<LayoutMigrationService.Move> moves;
            ReorgJob job;
            using (store.Gate.Enter())   // (manifest gate 09-08) both planners walk the item graph; pure otherwise
            {
                moves = new LayoutMigrationService(m, layout).Plan().ToList();
                job = ReorgPlanner.ForLayoutChange(m, layout);
            }
            // Offer only what is ACTUALLY ON DISK: the plan is manifest-path string logic, so records whose
            // bytes are gone still plan as "movable" and the prompt would promise work it cannot do (owner-hit
            // 08-30). The runner skips+repaths them harmlessly, so the executable job keeps them.
            var onDisk = moves.Where(x => File.Exists(x.FromAbs)).ToList();
            if (onDisk.Count == 0) return null;
            return Build(LayoutPlanKind.FlatLayout, job, onDisk);
        }, ct);

    /// <summary>The offer after a layout switch (App OfferLayoutReorg): every tracked file not in the current
    /// <c>ExtrasLayout</c> shape, planned from the manifest (the runner skips never-downloaded leaves). Null when
    /// nothing is out of place. A plan with <see cref="LayoutMigrationPlan.HasUnavailableDrives"/> is returned
    /// (not null) so the host can say which drive to reconnect -- it must not be run.</summary>
    public static Task<LayoutMigrationPlan?> ForLayoutChangeAsync(IManifestStore store, string backupRoot, CancellationToken ct)
        => Task.Run<LayoutMigrationPlan?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var m = store.Current;
            var layout = new BackupLayout(m, backupRoot);
            ReorgJob job;
            using (store.Gate.Enter()) job = ReorgPlanner.ForLayoutChange(m, layout);   // (manifest gate 09-08)
            if (job.HasUnavailableDrives)
                return new LayoutMigrationPlan(LayoutPlanKind.LayoutChange, job, Array.Empty<LayoutMigrationService.Move>(), 0, 0);
            if (job.Moves.Count == 0) return null;
            var moves = job.Moves
                .Select(mv => new LayoutMigrationService.Move(mv.GogId, mv.Title, mv.FromRel, mv.ToRel, mv.FromAbs, mv.ToAbs))
                .ToList();
            return Build(LayoutPlanKind.LayoutChange, job, moves);
        }, ct);

    private static LayoutMigrationPlan Build(LayoutPlanKind kind, ReorgJob job, List<LayoutMigrationService.Move> moves)
    {
        var shown = new HashSet<(long, string)>(moves.Select(x => (x.GogId, x.FromRel.Replace('\\', '/'))));
        long bytes = job.Moves.Where(mv => shown.Contains((mv.GogId, mv.FromRel.Replace('\\', '/')))).Sum(mv => mv.SizeBytes);
        int games = moves.Select(x => x.GogId).Distinct().Count();
        return new LayoutMigrationPlan(kind, job, moves, games, bytes);
    }

    /// <summary>Executes a plan: journals the job (so the plan is on disk before a single file moves), runs the
    /// <see cref="ReorgRunner"/> -- same-drive rename, or copy -> verify -> promote -> delete source -- repathing
    /// the manifest as each file lands, and saves. Resumable: an interrupted run leaves the journal for
    /// <c>reorg resume</c> / the App's resume prompt. <paramref name="control"/> lets a host pause/cancel
    /// between files and chunks; the token alone also cancels.</summary>
    public static async Task<LayoutMigrationOutcome> RunAsync(IManifestStore store, string backupRoot, LayoutMigrationPlan plan,
                                                              IBackupHost host, Action<ReorgProgress>? progress, CancellationToken ct,
                                                              ReorgControl? control = null, string? configDir = null)
    {
        if (plan.HasUnavailableDrives)
        {
            var msg = "Can't move files: " + string.Join(", ", plan.UnavailableDrives) + " is not connected. Reconnect it and try again.";
            host.Log(msg, isError: true);
            return new LayoutMigrationOutcome(ReorgOutcome.Paused, 0, 0, 0, false, msg, null);
        }
        var job = plan.Job;
        if (job.Moves.Count == 0)
            return new LayoutMigrationOutcome(ReorgOutcome.Completed, 0, 0, 0, false, null, null);

        var dir = configDir ?? await Task.Run(() => GrogPaths.Resolve(backupRoot).ConfigDir, ct);
        var runner = new ReorgRunner(store, new ReorgJournalStore(dir));
        if (progress is not null) runner.Progress += progress;
        // A token cancel (Ctrl-C) reads as an interruption -- the runner leaves the journal Paused, resumable --
        // while a ReorgControl.Cancel is the user's deliberate stop that needs resolving (revert or accept).
        control ??= new ReorgControl();

        ReorgResult result;
        try
        {
            result = await runner.RunAsync(job, control, ct);
        }
        catch (OperationCanceledException)
        {
            return new LayoutMigrationOutcome(ReorgOutcome.Canceled, job.Moves.Count(x => x.State == ReorgMoveState.Done),
                job.Moves.Count(x => x.State == ReorgMoveState.Skipped), job.Moves.Count(x => x.State == ReorgMoveState.Failed),
                true, null, null);
        }
        catch (Exception ex)
        {
            // A runner-level failure (journal not writable) surfaces; the host renders Error from the result.
            return new LayoutMigrationOutcome(ReorgOutcome.CompletedWithErrors, 0, 0, 0, false, null, ex);
        }
        try { await store.SaveAsync(CancellationToken.None); }
        catch (Exception ex) { host.Log($"Manifest save after the move failed: {ex.Message}", isError: true); }

        bool canceled = result.Outcome == ReorgOutcome.Canceled || ct.IsCancellationRequested;
        return new LayoutMigrationOutcome(result.Outcome, result.Moved, result.Skipped, result.Failed, canceled, result.PauseReason, null, result.PausedForRootId);
    }
}

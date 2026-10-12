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

public enum LayoutPlanKind { FlatLayout = 0, LayoutChange = 1 }

public sealed record LayoutMigrationPlan(
    LayoutPlanKind Kind,
    ReorgJob Job,
    IReadOnlyList<LayoutMigrationService.Move> Moves,
    int Games,
    long Bytes)
{
    public int Count => Moves.Count;
    public bool IsEmpty => Moves.Count == 0;
    public IReadOnlyList<string> UnavailableDrives => Job.UnavailableDrives;
    public bool HasUnavailableDrives => Job.HasUnavailableDrives;
}

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

public static class LayoutMigrationRun
{
    public static Task<LayoutMigrationPlan?> PlanAsync(IManifestStore store, string backupRoot, CancellationToken ct,
                                                       string? configDir = null)
        => Task.Run<LayoutMigrationPlan?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var pending = new ReorgJournalStore(configDir ?? GrogPaths.Resolve(backupRoot).ConfigDir).Load();
            if (pending is not null && !pending.IsComplete) return null;

            var m = store.Current;
            var layout = new BackupLayout(m, backupRoot);
            List<LayoutMigrationService.Move> moves;
            ReorgJob job;
            using (store.Gate.Enter())   // both planners walk the item graph; pure otherwise
            {
                moves = new LayoutMigrationService(m, layout).Plan().ToList();
                job = ReorgPlanner.ForLayoutChange(m, layout);
            }
            var onDisk = moves.Where(x => File.Exists(x.FromAbs)).ToList();
            if (onDisk.Count == 0) return null;
            return Build(LayoutPlanKind.FlatLayout, job, onDisk);
        }, ct);

    public static Task<LayoutMigrationPlan?> ForLayoutChangeAsync(IManifestStore store, string backupRoot, CancellationToken ct)
        => Task.Run<LayoutMigrationPlan?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var m = store.Current;
            var layout = new BackupLayout(m, backupRoot);
            ReorgJob job;
            using (store.Gate.Enter()) job = ReorgPlanner.ForLayoutChange(m, layout);   // 
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
            return new LayoutMigrationOutcome(ReorgOutcome.CompletedWithErrors, 0, 0, 0, false, null, ex);
        }
        try { await store.SaveAsync(CancellationToken.None); }
        catch (Exception ex) { host.Log($"Manifest save after the move failed: {ex.Message}", isError: true); }

        bool canceled = result.Outcome == ReorgOutcome.Canceled || ct.IsCancellationRequested;
        return new LayoutMigrationOutcome(result.Outcome, result.Moved, result.Skipped, result.Failed, canceled, result.PauseReason, null, result.PausedForRootId);
    }
}

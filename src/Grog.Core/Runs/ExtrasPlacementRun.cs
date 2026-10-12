// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Volumes;

namespace Grog.Core.Runs;

public static class ExtrasPlacementRun
{
    public static Task<LayoutMigrationPlan?> ApplyAndPlanAsync(
        IManifestStore store, string backupRoot, ExtrasPlacement placement, string extrasRootId,
        CancellationToken ct = default)
        => Task.Run<LayoutMigrationPlan?>(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var m = store.Current;
            var primary = m.PrimaryRootId ?? extrasRootId;
            var targetExtrasRoot = placement == ExtrasPlacement.WithGame ? primary : extrasRootId;
            var layout = new BackupLayout(m, backupRoot);
            ReorgJob job;
            using (store.Gate.Enter())   // 
            {
                job = PlanMoves(m, layout, placement, targetExtrasRoot, primary);
                if (job.HasUnavailableDrives)
                    return new LayoutMigrationPlan(LayoutPlanKind.LayoutChange, job,
                                                   Array.Empty<LayoutMigrationService.Move>(), 0, 0);   // NOT committed
                Commit(m, placement, targetExtrasRoot, primary);
            }
            await store.SaveAsync(ct).ConfigureAwait(false);

            if (job.Moves.Count == 0) return null;

            var moves = job.Moves
                .Select(mv => new LayoutMigrationService.Move(mv.GogId, mv.Title, mv.FromRel, mv.ToRel, mv.FromAbs, mv.ToAbs))
                .ToList();
            var shown = new HashSet<(long, string)>(moves.Select(x => (x.GogId, x.FromRel.Replace('\\', '/'))));
            long bytes = job.Moves.Where(mv => shown.Contains((mv.GogId, mv.FromRel.Replace('\\', '/')))).Sum(mv => mv.SizeBytes);
            int games = moves.Select(x => x.GogId).Distinct().Count();
            return new LayoutMigrationPlan(LayoutPlanKind.LayoutChange, job, moves, games, bytes);
        }, ct);

    private static void Commit(LibraryManifest m, ExtrasPlacement placement, string extrasRootId, string primary)
    {
        m.SetExtrasLayout(placement);
        var routing = m.Routing;
        routing.Mode = RoutingMode.Overflow;              // overflow always on, as SetExtrasLocation had it
        routing.SetRole(ContentRole.Games, primary);      // games always to the primary
        routing.SetRole(ContentRole.Extras, extrasRootId);
    }

    public static ReorgJob PlanMoves(LibraryManifest m, IBackupLayout layout,
                                       ExtrasPlacement placement, string extrasRootId, string primaryRootId)
    {
        var oldLayout = m.ExtrasLayout; var oldInside = m.ExtrasInsideGame;
        var oldMode = m.Routing.Mode;
        m.Routing.RoleRoots.TryGetValue(ContentRole.Games, out var oldGames);
        m.Routing.RoleRoots.TryGetValue(ContentRole.Extras, out var oldExtras);
        Commit(m, placement, extrasRootId, primaryRootId);
        try { return PlanAgainstCurrent(m, layout, placement, extrasRootId, primaryRootId); }
        finally
        {
            m.ExtrasLayout = oldLayout; m.ExtrasInsideGame = oldInside;
            m.Routing.Mode = oldMode;
            if (oldGames is null) m.Routing.RoleRoots.Remove(ContentRole.Games); else m.Routing.RoleRoots[ContentRole.Games] = oldGames;
            if (oldExtras is null) m.Routing.RoleRoots.Remove(ContentRole.Extras); else m.Routing.RoleRoots[ContentRole.Extras] = oldExtras;
        }
    }

    private static ReorgJob PlanAgainstCurrent(LibraryManifest m, IBackupLayout layout,
                                                ExtrasPlacement placement, string extrasRootId, string primaryRootId)
    {
        // 1. Extras that must change DEVICE
        var crossByTarget = new Dictionary<string, List<(LibraryItem Item, GameFile File)>>();
        foreach (var item in m.Items)
        {
            string? home = placement == ExtrasPlacement.WithGame ? GameHomeRoot(m, item, primaryRootId) : extrasRootId;
            if (home is null) continue;
            foreach (var f in item.Files)
            {
                if (f.Kind != FileKind.Extra) continue;
                if (string.IsNullOrEmpty(f.LocalRelativePath)) continue;
                if ((f.RootId ?? primaryRootId) == home) continue;   // already there
                if (!crossByTarget.TryGetValue(home, out var list)) crossByTarget[home] = list = new();
                list.Add((item, f));
            }
        }

        var cross = new ReorgJob { Reason = ReorgReason.Relocate };
        foreach (var (target, files) in crossByTarget)
        {
            var part = ReorgPlanner.ForFileMoves(m, layout, files, target);
            cross.Moves.AddRange(part.Moves);
            foreach (var d in part.UnavailableDrives) cross.NoteUnavailableDrive(d);
        }

        // 2. Everything still in the wrong SHAPE on the device it already sits on
        var claimed = new HashSet<(long, string)>(cross.Moves.Select(mv => (mv.GogId, mv.FileKey)));
        var renames = ReorgPlanner.ForLayoutChange(m, layout);
        var job = new ReorgJob { Reason = cross.Moves.Count > 0 ? ReorgReason.Relocate : ReorgReason.Layout };
        job.Moves.AddRange(cross.Moves);
        job.Moves.AddRange(renames.Moves.Where(mv => !claimed.Contains((mv.GogId, mv.FileKey))));

        foreach (var d in cross.UnavailableDrives) job.NoteUnavailableDrive(d);
        foreach (var d in renames.UnavailableDrives) job.NoteUnavailableDrive(d);
        return job;
    }

    internal static string? GameHomeRoot(LibraryManifest m, LibraryItem item, string primaryRootId)
    {
        var routed = m.Routing.ResolveRootId(item.GogId, FileKind.Installer, primaryRootId, ExtrasPlacement.WithGame);
        var held = item.Files.Where(f => f.Kind != FileKind.Extra && !f.IsOldVersion && !string.IsNullOrEmpty(f.LocalRelativePath))
                             .GroupBy(f => f.RootId ?? primaryRootId)
                             .OrderByDescending(g => g.Sum(f => f.LocalSizeBytes ?? f.ExpectedSizeBytes ?? 0)).ToList();
        var home = held.Count == 0 || held.Any(g => g.Key == routed) ? routed : held[0].Key;
        return m.Roots.FirstOrDefault(r => r.Id == home)?.State == RootState.Detached ? null : home;
    }
}

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

/// <summary>
/// THE extras-placement change: shape and storage committed together, as ONE move job.
///
/// Until 09-11 these were two settings with two commit paths. The shape pills wrote
/// <c>ExtrasLayout</c> and offered a reorg; the storage pills wrote <c>Routing.RoleRoots[Extras]</c>
/// and offered nothing, because the only planner wired to that screen
/// (<see cref="ReorgPlanner.ForLayoutChange"/>) plans same-root renames and never reads routing. A user
/// could therefore select "Separate folder, on the second drive", be shown a folder preview promising
/// exactly that, and end with every existing extra still on the primary -- silently, permanently, and with
/// the UI insisting otherwise (owner-hit, macOS walk 09-11).
///
/// One entry point now answers "what has to move to make the preview true", composing the two existing
/// planners rather than inventing a third: cross-root moves for extras that must change device, renames for
/// everything still in the wrong shape on the device it is already on.
/// </summary>
public static class ExtrasPlacementRun
{
    /// <summary>Plans the moves that make the target placement true on disk, and commits the placement to the
    /// manifest ONLY when that plan can run. Null when the placement was committed and nothing needs to move --
    /// a preference with no files to move is not a failed migration.
    ///
    /// A plan whose job reports <see cref="ReorgJob.HasUnavailableDrives"/> is RETURNED with NOTHING committed
    /// (09-13). Before, shape and storage were written and saved first and the offline drive was discovered
    /// after: the manifest and the preview said "extras on drive 2", every extra stayed on the primary, the card
    /// reset clean, and nothing ever re-offered the move -- the exact silent mismatch this run exists to kill.
    /// The host names the drive; the card stays dirty so Apply is still there when the drive is back.</summary>
    /// <param name="extrasRootId">Root the extras tree should live on. Ignored by
    /// <see cref="ExtrasPlacement.WithGame"/>, where an extra always follows its game; pass the primary.</param>
    public static Task<LayoutMigrationPlan?> ApplyAndPlanAsync(
        IManifestStore store, string backupRoot, ExtrasPlacement placement, string extrasRootId,
        CancellationToken ct = default)
        => Task.Run<LayoutMigrationPlan?>(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var m = store.Current;
            var primary = m.PrimaryRootId ?? extrasRootId;

            // WithGame overrides routing at placement time (RoutingPolicy.ResolveRootId), so storing anything
            // but the primary for the Extras role would be a lie the next reader has to know to ignore.
            var targetExtrasRoot = placement == ExtrasPlacement.WithGame ? primary : extrasRootId;

            // BackupLayout probes every root on disk: built off the gate. It reads shape and routing LIVE off the
            // manifest, so PlanMoves can point it at the target without a commit.
            var layout = new BackupLayout(m, backupRoot);
            ReorgJob job;
            using (store.Gate.Enter())   // (manifest gate 09-08)
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

    /// <summary>The two halves, written together. Caller holds the gate.</summary>
    private static void Commit(LibraryManifest m, ExtrasPlacement placement, string extrasRootId, string primary)
    {
        m.SetExtrasLayout(placement);
        var routing = m.Routing;
        routing.Mode = RoutingMode.Overflow;              // overflow always on, as SetExtrasLocation had it
        routing.SetRole(ContentRole.Games, primary);      // games always to the primary
        routing.SetRole(ContentRole.Extras, extrasRootId);
    }

    /// <summary>Pure: the combined move set that makes <paramref name="placement"/> on <paramref name="extrasRootId"/>
    /// true, for a manifest holding ANY committed state -- the manifest is left exactly as found. Separated from
    /// the commit so it can be tested, and called, without a store or a save: the Settings card counts what
    /// Apply would move before the user commits to anything. Caller holds the gate.</summary>
    public static ReorgJob PlanMoves(LibraryManifest m, IBackupLayout layout,
                                       ExtrasPlacement placement, string extrasRootId, string primaryRootId)
    {
        // Plan AGAINST THE TARGET. ForLayoutChange, NewRelativeFor and the layout's SubPathFor all read the
        // shape and the routing live off the manifest, so the target is placed there for the length of the
        // plan and the committed values put back. Planning against the committed state returned the moves
        // that UNDO the change: the card footer said "nothing to move" for a shape change over 400 extras,
        // then Apply offered to move all 400 (09-13). Caller holds the gate; nothing else reads a half-swap.
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
        // 1. Extras that must change DEVICE. A separate tree gathers every held extra on the extras root.
        //    WithGame sends an extra to ITS GAME's drive (09-19): "nothing to move" was assumed here, which holds
        //    only until a separate tree on the secondary has been applied once -- reverting then changed the
        //    shape and left the extras on the secondary while their installers sat on the primary.
        var crossByTarget = new Dictionary<string, List<(LibraryItem Item, GameFile File)>>();
        foreach (var item in m.Items)
        {
            string? home = placement == ExtrasPlacement.WithGame ? GameHomeRoot(m, item, primaryRootId) : extrasRootId;
            if (home is null) continue;
            foreach (var f in item.Files)
            {
                if (f.Kind != FileKind.Extra) continue;
                // A never-downloaded extra has no bytes to move (same test ForLayoutChange makes). Planned
                // anyway, it reached the runner with FromAbs "" and paused the whole job (sweep 2 #1).
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

        // 2. Everything still in the wrong SHAPE on the device it already sits on. ForFileMoves already
        //    lands its files in the correct shape (it asks the layout for the sub-path), so anything it
        //    claimed is excluded here rather than planned twice -- two moves for one file would have the
        //    second one fail on a source that is no longer there.
        var claimed = new HashSet<(long, string)>(cross.Moves.Select(mv => (mv.GogId, mv.FileKey)));
        var renames = ReorgPlanner.ForLayoutChange(m, layout);

        // Reason drives the resume prompt's wording, so it names what the job actually is: a device change
        // where one exists, a shape change otherwise.
        var job = new ReorgJob { Reason = cross.Moves.Count > 0 ? ReorgReason.Relocate : ReorgReason.Layout };
        job.Moves.AddRange(cross.Moves);
        job.Moves.AddRange(renames.Moves.Where(mv => !claimed.Contains((mv.GogId, mv.FileKey))));

        // An offline drive on EITHER side blocks the whole job; losing one half's warning would let a
        // partial move run.
        foreach (var d in cross.UnavailableDrives) job.NoteUnavailableDrive(d);
        foreach (var d in renames.UnavailableDrives) job.NoteUnavailableDrive(d);
        return job;
    }

    /// <summary>(09-19) The drive a game LIVES on, for WithGame: where its held installers are (the routed drive
    /// when they are split and it is one of them, else the drive holding the most of them); with none held,
    /// where routing would put them (pin, else primary). Null = leave the extras be: the game sits on Other
    /// storage, which is never written to.</summary>
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

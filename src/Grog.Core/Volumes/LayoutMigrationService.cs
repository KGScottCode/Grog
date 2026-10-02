// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Volumes;

/// <summary>
/// Migrates a flat backup layout (&lt;root&gt;/&lt;slug&gt;) to the category layout (Games/, Extras/).
/// <see cref="Plan"/> is pure (no disk) for dry-run display; execution is the journaled ReorgRunner.
/// </summary>
public sealed class LayoutMigrationService
{
    public sealed record Move(long GogId, string Title, string FromRel, string ToRel, string FromAbs, string ToAbs);

    private readonly LibraryManifest _m;
    private readonly IBackupLayout _layout;

    public LayoutMigrationService(LibraryManifest manifest, IBackupLayout layout)
    {
        _m = manifest;
        _layout = layout;
    }

    /// <summary>Computes the file's correct relative path under the chosen layout (null if already there).
    /// The tail is just the filename, so this is robust regardless of current nesting; the extras shape
    /// matches BackupLayout.SubPathFor exactly. Pure string logic, internal for testing.</summary>
    internal static string? NewRelativeFor(string relPath, string slug, FileKind kind, ExtrasPlacement layout, GameFile? file = null)
    {
        var rel = relPath.Replace('\\', '/');
        var name = Path.GetFileName(rel);
        string target;
        if (RoutingPolicy.RoleOf(kind) == ContentRole.Extras)
        {
            if (layout == ExtrasPlacement.WithGame)
                target = $"Games/{slug}/Extras/{name}";   // flat, inside the game's own folder
            else
            {
                var bucket = Sync.ExtraClassifier.FolderFor(Sync.ExtraClassifier.Classify(file?.Name, file?.ExtraType));
                target = layout == ExtrasPlacement.SeparateByGame
                    ? $"Extras/{slug}/{bucket}/{name}" : $"Extras/{bucket}/{slug}/{name}";
            }
        }
        else target = $"Games/{slug}/{name}";
        return target == rel ? null : target;
    }

    /// <summary>Plans the moves needed; no disk access. Files already under a category folder are skipped.</summary>
    public IReadOnlyList<Move> Plan()
    {
        var moves = new List<Move>();
        foreach (var item in _m.Items)
        {
            foreach (var f in item.Files)
            {
                var rel = f.LocalRelativePath;
                if (string.IsNullOrEmpty(rel)) continue;
                var toRel = NewRelativeFor(rel, item.Slug, f.Kind, _m.ExtrasLayout, f);
                if (toRel is null) continue;   // already in the right place

                var fromAbs = _layout.ResolvePath(f);
                var root = _layout.RootPath(f.RootId ?? _m.PrimaryRootId);
                if (fromAbs is null || root is null) continue;   // root offline
                var toAbs = Path.Combine(root, toRel.Replace('/', Path.DirectorySeparatorChar));
                moves.Add(new Move(item.GogId, item.Title, rel, toRel, fromAbs, toAbs));
            }
        }
        return moves;
    }
}

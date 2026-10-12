// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Format;
using Grog.Core.Models;

namespace Grog.Core.Download;

public static class MisnamedFileRepair
{
    public static List<(LibraryItem Item, GameFile File)> FindCandidates(LibraryManifest manifest)
    {
        var list = new List<(LibraryItem, GameFile)>();
        foreach (var item in manifest.Items)
            foreach (var f in item.Files)
            {
                if (string.IsNullOrEmpty(f.LocalRelativePath) || !Sync.BackupScope.HasLocalCopy(f)) continue;
                var onDisk = Path.GetFileName(f.LocalRelativePath.Replace('\\', '/'));
                if (string.Equals(onDisk, Naming.SafeFileName(f.Name), StringComparison.Ordinal)
                    || string.Equals(onDisk, Naming.WindowsSafe(f.Name), StringComparison.Ordinal))
                    list.Add((item, f));
            }
        return list;
    }

    public static async Task<int> RepairAsync(LibraryManifest manifest, Volumes.IBackupLayout layout,
        Func<GameFile, CancellationToken, Task<string?>> realName, Action<string>? log, CancellationToken ct = default,
        Manifest.ManifestGate? gate = null)
    {
        int renamed = 0;
        foreach (var (item, f) in FindCandidates(manifest))
        {
            ct.ThrowIfCancellationRequested();
            string? real;
            try { real = await realName(f, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke($"repair: could not resolve {item.Title} / {f.Name}: {ex.Message}"); continue; }
            if (string.IsNullOrWhiteSpace(real)) continue;
            var safe = Naming.SafeFileName(real);
            var oldPath = layout.ResolvePath(f);
            if (oldPath is null || !File.Exists(oldPath)) continue;
            var oldName = Path.GetFileName(oldPath);
            if (string.Equals(safe, oldName, StringComparison.Ordinal)) continue;   // label happens to be the real name
            var newPath = Path.Combine(Path.GetDirectoryName(oldPath) ?? "", safe);
            if (File.Exists(newPath))
            {
                log?.Invoke($"repair: {item.Title} / {f.Name}: {safe} already exists, left {oldName} alone");
                continue;
            }
            try { File.Move(oldPath, newPath); }
            catch (Exception ex) { log?.Invoke($"repair: rename failed for {item.Title} / {oldName}: {ex.Message}"); continue; }
            var rel = f.LocalRelativePath!;
            int cut = Math.Max(rel.LastIndexOf('/'), rel.LastIndexOf('\\'));
            if (gate is not null)
            {
                using (gate.Enter())
                {
                    f.LocalRelativePath = cut >= 0 ? rel[..(cut + 1)] + safe : safe;
                    f.ResolvedFileName = safe;
                }
            }
            else
            {
                f.LocalRelativePath = cut >= 0 ? rel[..(cut + 1)] + safe : safe;
                f.ResolvedFileName = safe;
            }
            renamed++;

        }
        return renamed;
    }
}

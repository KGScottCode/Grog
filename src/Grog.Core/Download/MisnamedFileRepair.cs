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

/// <summary>
/// One-time repair for files the 1060-1083 builds saved under GOG's DISPLAY label instead of the real
/// filename ("Vambrace_ Cold Soul (Part 1 of 2)" for a setup .bin, "manual (36 pages)" with no .pdf).
/// The bytes are right (they verified); only the name is wrong. Each candidate is re-resolved for its
/// real name and renamed in place, manifest path updated; nothing is re-downloaded and nothing is ever
/// overwritten (a name clash skips and logs). Safe to run repeatedly: it converges to zero candidates.
/// </summary>
public static class MisnamedFileRepair
{
    /// <summary>Files whose on-disk name IS the sanitized display label -- the 1060-1083 signature.</summary>
    public static List<(LibraryItem Item, GameFile File)> FindCandidates(LibraryManifest manifest)
    {
        var list = new List<(LibraryItem, GameFile)>();
        foreach (var item in manifest.Items)
            foreach (var f in item.Files)
            {
                if (string.IsNullOrEmpty(f.LocalRelativePath) || !Sync.BackupScope.HasLocalCopy(f)) continue;
                var onDisk = Path.GetFileName(f.LocalRelativePath.Replace('\\', '/'));
                // SafeName is platform-dependent (':' survives on Linux, not Windows); a library written on
                // Windows and inspected elsewhere must still match, so compare the Windows form too.
                if (string.Equals(onDisk, Naming.SafeFileName(f.Name), StringComparison.Ordinal)
                    || string.Equals(onDisk, Naming.WindowsSafe(f.Name), StringComparison.Ordinal))
                    list.Add((item, f));
            }
        return list;
    }

    /// <summary>Rename every candidate to its real name. <paramref name="realName"/> asks GOG (the resolve
    /// probe's Content-Disposition); null/empty = leave alone. Returns how many were renamed.</summary>
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
            // Re-point after the rename succeeded, under the store's gate when the caller passes one; the
            // probe + File.Move above stay outside it. (manifest gate 09-08)
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

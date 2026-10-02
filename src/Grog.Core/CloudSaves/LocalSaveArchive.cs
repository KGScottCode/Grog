// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Grog.Core.CloudSaves;

/// <summary>Reads and manages one game's on-disk cloud-save history: the timestamped folders
/// <see cref="CloudSaveService.DownloadLocalSaveAsync"/> writes under &lt;gameDir&gt;/cloud-saves/&lt;utc&gt;/.
/// Pure disk work (no network, no auth); history only grows until the user prunes it.</summary>
public static class LocalSaveArchive
{
    /// <summary>One local save on disk: its folder, the UTC instant it was taken (parsed from the folder
    /// name), and the file count / total bytes it holds.</summary>
    public sealed record LocalSave(string Dir, DateTimeOffset StampUtc, int FileCount, long SizeBytes);

    /// <summary>All localSaves for a game, newest first. <paramref name="gameDir"/> is the same per-game
    /// directory passed to <see cref="CloudSaveService.DownloadLocalSaveAsync"/>. Empty local save folders
    /// (a download that wrote nothing) are skipped so they never read as real history.</summary>
    public static IReadOnlyList<LocalSave> List(string gameDir)
    {
        var result = new List<LocalSave>();
        if (string.IsNullOrEmpty(gameDir)) return result;
        var root = Path.Combine(gameDir, "cloud-saves");
        if (!Directory.Exists(root)) return result;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            List<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            if (files.Count == 0) continue;

            long size = 0;
            foreach (var f in files)
                try { size += new FileInfo(f).Length; } catch { /* skip an unreadable file's bytes */ }

            result.Add(new LocalSave(dir, ParseStamp(Path.GetFileName(dir), dir), files.Count, size));
        }
        return result.OrderByDescending(s => s.StampUtc).ToList();
    }

    /// <summary>Parse "yyyyMMdd-HHmmss" (UTC) from the folder name; fall back to the folder's creation
    /// time so a hand-renamed local save still sorts somewhere sane rather than to the epoch.</summary>
    private static DateTimeOffset ParseStamp(string name, string dir)
    {
        if (DateTimeOffset.TryParseExact(name, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return dt;
        try { return new DirectoryInfo(dir).CreationTimeUtc; } catch { return DateTimeOffset.MinValue; }
    }

    /// <summary>Copy every file in a local save into <paramref name="targetDir"/>, preserving each file's
    /// relative path within the local save (e.g. "saves/slot1.sav"). Overwrites files already present in the
    /// target. Returns the number of files written. Never touches the local save itself.</summary>
    public static int RestoreTo(string localSaveDir, string targetDir)
    {
        if (!Directory.Exists(localSaveDir)) return 0;
        Directory.CreateDirectory(targetDir);
        int written = 0;
        foreach (var src in Directory.EnumerateFiles(localSaveDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(localSaveDir, src);
            var dest = Path.Combine(targetDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
            written++;
        }
        return written;
    }

    /// <summary>Delete one local save folder and everything under it. Irreversible -- the caller confirms.</summary>
    public static void Prune(string localSaveDir)
    {
        if (Directory.Exists(localSaveDir)) Directory.Delete(localSaveDir, recursive: true);
    }

    /// <summary>Enforce a retention cap: keep the newest <paramref name="keep"/> localSaves for a game and delete
    /// the rest. keep &lt;= 0 means keep everything (no-op). Returns the number of localSaves deleted. Called after
    /// a new local save lands, so retention is applied going forward -- it never runs off a settings change alone.</summary>
    public static int PruneToKeep(string gameDir, int keep)
    {
        if (keep <= 0) return 0;
        var all = List(gameDir);            // newest first
        if (all.Count <= keep) return 0;
        int deleted = 0;
        foreach (var snap in all.Skip(keep))
        {
            try { Prune(snap.Dir); deleted++; } catch { /* best-effort; a locked folder is left in place */ }
        }
        return deleted;
    }
}

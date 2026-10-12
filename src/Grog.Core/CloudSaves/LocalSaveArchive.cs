// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Grog.Core.CloudSaves;

public static class LocalSaveArchive
{
    public sealed record LocalSave(string Dir, DateTimeOffset StampUtc, int FileCount, long SizeBytes);

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

    private static DateTimeOffset ParseStamp(string name, string dir)
    {
        if (DateTimeOffset.TryParseExact(name, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return dt;
        try { return new DirectoryInfo(dir).CreationTimeUtc; } catch { return DateTimeOffset.MinValue; }
    }

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

    public static void Prune(string localSaveDir)
    {
        if (Directory.Exists(localSaveDir)) Directory.Delete(localSaveDir, recursive: true);
    }

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

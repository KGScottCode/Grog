// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.IO;
using System.Linq;

namespace Grog.Core.Volumes;

public interface IFolderIo
{
    bool DirectoryExists(string path);
    bool DirectoryIsEmpty(string path);
    void DeleteDirectory(string path);
}

public static class FolderPruner
{
    public sealed class RealFolderIo : IFolderIo
    {
        public static readonly RealFolderIo Instance = new();
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public bool DirectoryIsEmpty(string path) => Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any();
        public void DeleteDirectory(string path) => Directory.Delete(path);
    }

    public static bool DeleteIfEmpty(string dir, IFolderIo? io = null)
    {
        io ??= RealFolderIo.Instance;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!io.DirectoryExists(dir)) return true;
                if (!io.DirectoryIsEmpty(dir)) return false;
                io.DeleteDirectory(dir);
                return true;
            }
            catch (IOException) when (attempt == 0) { System.Threading.Thread.Sleep(100); }
            catch (System.UnauthorizedAccessException) when (attempt == 0) { System.Threading.Thread.Sleep(100); }
            catch { return false; }
        }
        return false;
    }

    public static void PruneEmptyChainAbove(string fromAbs, IFolderIo? io = null)
    {
        var dir = Path.GetDirectoryName(fromAbs);
        if (!string.IsNullOrEmpty(dir)) PruneEmptyChain(dir, io);
    }

    public static void PruneEmptyChain(string dir, IFolderIo? io = null)
    {
        io ??= RealFolderIo.Instance;
        try
        {
            var d = dir;
            for (int depth = 0; depth < 3 && !string.IsNullOrEmpty(d); depth++)
            {
                var name = Path.GetFileName(d);
                var grandparent = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(d) ?? "") ?? "");
                bool perGameExtras = name == "Extras" && grandparent == "Games";   // Games/<slug>/Extras
                bool topLevelCategory = (name is "Games" or "Extras" or "Cloud Saves" or "Old Versions") && !perGameExtras;
                if (topLevelCategory) break;
                if (!io.DirectoryExists(d) || !io.DirectoryIsEmpty(d)) break;
                io.DeleteDirectory(d);
                d = Path.GetDirectoryName(d);
            }
        }
        catch { /* best-effort tidy-up; a locked folder just stays */ }
    }
}

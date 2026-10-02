// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.IO;
using System.Linq;

namespace Grog.Core.Volumes;

/// <summary>The folder half of a file-system seam: what the empty-folder sweep needs to know.</summary>
public interface IFolderIo
{
    bool DirectoryExists(string path);
    bool DirectoryIsEmpty(string path);
    void DeleteDirectory(string path);
}

/// <summary>
/// ONE rule for removing the empty folders Grog itself made, shared by a reorganize (the source chain a
/// move vacated) and a delete (the game folder a removed file leaves behind). Climbs at most a few levels
/// and stops at anything non-empty or at a TOP-LEVEL category folder (Games / Extras / Cloud Saves / Old Versions
/// directly under the storage root) -- only OUR emptied folders go, never user content and never the library's
/// own scaffolding. Before 09-13 the delete sweep had its own copy without the category guard, so a file
/// imported at <c>Games/x.exe</c> took the
/// <c>Games/</c> folder with it.
/// </summary>
public static class FolderPruner
{
    /// <summary>The one real-disk IFolderIo; every empty-folder delete in Core goes through it.</summary>
    public sealed class RealFolderIo : IFolderIo
    {
        public static readonly RealFolderIo Instance = new();
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public bool DirectoryIsEmpty(string path) => Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any();
        public void DeleteDirectory(string path) => Directory.Delete(path);
    }

    /// <summary>Removes ONE folder when it is empty, no climb and no guards: for a folder Grog itself made (.grog-tmp).
    /// A delete refused by a transient lock (an antivirus or indexer peeking in) is retried once after 100 ms. True when
    /// the folder is gone afterwards.</summary>
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

    /// <summary>Prune the chain starting at the folder that held <paramref name="fromAbs"/> (a file path).</summary>
    public static void PruneEmptyChainAbove(string fromAbs, IFolderIo? io = null)
    {
        var dir = Path.GetDirectoryName(fromAbs);
        if (!string.IsNullOrEmpty(dir)) PruneEmptyChain(dir, io);
    }

    /// <summary>Prune the chain starting AT <paramref name="dir"/>.</summary>
    public static void PruneEmptyChain(string dir, IFolderIo? io = null)
    {
        io ??= RealFolderIo.Instance;
        try
        {
            var d = dir;
            for (int depth = 0; depth < 3 && !string.IsNullOrEmpty(d); depth++)
            {
                // The category folders (Games / Extras / Cloud Saves) are kept, but only the TOP-LEVEL ones, the
                // ones directly under the storage root. A game's own Games/<slug>/Extras/ shares the name and
                // must be pruned like any other emptied game folder (owner 09-04: every extras move left its
                // game folder standing, because the walk stopped on the first step).
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

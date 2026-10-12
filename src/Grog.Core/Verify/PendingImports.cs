using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Verify;

public static class PendingImports
{
    public static bool MustDefer(LibraryManifest manifest) => manifest.Items.Count == 0;

    public static IReadOnlyList<BackupRoot> Waiting(LibraryManifest manifest)
        => manifest.Roots.Where(r => r.PendingImport).ToList();

    public static IReadOnlyList<BackupRoot> Runnable(LibraryManifest manifest)
        => MustDefer(manifest) ? Array.Empty<BackupRoot>()
         : manifest.Roots.Where(r => r.PendingImport && r.State == RootState.Online).ToList();

    public static void Mark(BackupRoot root) => root.PendingImport = true;

    public static void Clear(BackupRoot root) => root.PendingImport = false;
}

using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Verify;

/// <summary>
/// Storage added with "Scan &amp; Add" before the first library scan is registered at once and its import
/// deferred: <see cref="ImportPlanner"/> needs library items (names and expected sizes) to match anything.
/// This is the bookkeeping: which roots still owe an import, and when it may run.
/// </summary>
public static class PendingImports
{
    /// <summary>True when the import cannot run yet because the library holds no items.</summary>
    public static bool MustDefer(LibraryManifest manifest) => manifest.Items.Count == 0;

    /// <summary>Roots still waiting for their import, in registration order.</summary>
    public static IReadOnlyList<BackupRoot> Waiting(LibraryManifest manifest)
        => manifest.Roots.Where(r => r.PendingImport).ToList();

    /// <summary>Roots whose deferred import can run now: flagged, online, and the library has items. No disk
    /// probe here (callers may be on a UI thread); the runner checks the folder itself, off-thread.</summary>
    public static IReadOnlyList<BackupRoot> Runnable(LibraryManifest manifest)
        => MustDefer(manifest) ? Array.Empty<BackupRoot>()
         : manifest.Roots.Where(r => r.PendingImport && r.State == RootState.Online).ToList();

    /// <summary>Mark a root as owing an import (idempotent).</summary>
    public static void Mark(BackupRoot root) => root.PendingImport = true;

    /// <summary>The import ran (or the user declined it): the root owes nothing more.</summary>
    public static void Clear(BackupRoot root) => root.PendingImport = false;
}

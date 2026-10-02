// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;

namespace Grog.Core.Runs;

/// <summary>Which problem files a fix re-queues. Combine the flags; <see cref="Game"/> and <see cref="File"/>
/// name a product or a single file instead (every file of the game, present ones included: the
/// "Re-download game" verb).</summary>
[Flags]
public enum FixKind
{
    None = 0,
    /// <summary>Files the last run(s) failed to fetch: strikes on a file that is still a gap (see
    /// <see cref="FixRun.IsFailedDownload"/>). The lede's "Try again".</summary>
    FailedDownloads = 1,
    /// <summary>Files verify condemned (<see cref="FileState.Corrupt"/>).</summary>
    CorruptFiles = 2,
    /// <summary>Files verify found gone from disk (<see cref="FileState.Missing"/>).</summary>
    MissingFiles = 4,
    /// <summary>Every file of <see cref="FixSelector.GogId"/>, whatever its state.</summary>
    Game = 8,
    /// <summary>The one file (<see cref="FixSelector.GogId"/>, <see cref="FixSelector.FileKey"/>).</summary>
    File = 16,
    /// <summary>A named set of files (<see cref="FixSelector.Keys"/>), whatever their state short of present: the
    /// Health card's Try Again names the run's failed files, condemned ones included (QA 09-30 C1).</summary>
    Keys = 32,
}

/// <summary>A fix selection: flags, plus the product / file for the targeted kinds.</summary>
public sealed record FixSelector(FixKind Kinds, long? GogId = null, string? FileKey = null)
{
    /// <summary>The files a <see cref="FixKind.Keys"/> selection names.</summary>
    public IReadOnlySet<(long GogId, string FileKey)>? Keys { get; init; }
    public static readonly FixSelector Failed = new(FixKind.FailedDownloads);
    /// <summary>Every failed download plus these named files (a run's failures by key; Corrupt included), minus
    /// anything already present on disk.</summary>
    public static FixSelector FailedOrKeys(IEnumerable<(long GogId, string FileKey)> keys)
        => new(FixKind.FailedDownloads | FixKind.Keys) { Keys = keys.ToHashSet() };
    public static readonly FixSelector Corrupt = new(FixKind.CorruptFiles);
    public static readonly FixSelector Missing = new(FixKind.MissingFiles);
    /// <summary>Corrupt and missing together (the Overview "Fix It" with both in play).</summary>
    public static readonly FixSelector CorruptOrMissing = new(FixKind.CorruptFiles | FixKind.MissingFiles);
    public static FixSelector Game(long gogId) => new(FixKind.Game, gogId);
    public static FixSelector File(long gogId, string fileKey) => new(FixKind.File, gogId, fileKey);

    /// <summary>Does this file belong to the selection? Pure; the exclusion set is applied by the run.</summary>
    public bool Matches(LibraryItem item, GameFile f)
    {
        if (Kinds.HasFlag(FixKind.File)) return item.GogId == GogId && string.Equals(f.FileKey, FileKey, StringComparison.Ordinal);
        if (Kinds.HasFlag(FixKind.Game)) return item.GogId == GogId;
        if (Kinds.HasFlag(FixKind.Keys) && Keys is { } ks && ks.Contains((item.GogId, f.FileKey)))
            return !BackupScope.IsPresent(f) && f.State != FileState.Unavailable;
        return (Kinds.HasFlag(FixKind.FailedDownloads) && FixRun.IsFailedDownload(f))
            || (Kinds.HasFlag(FixKind.CorruptFiles) && f.State == FileState.Corrupt)
            || (Kinds.HasFlag(FixKind.MissingFiles) && f.State == FileState.Missing);
    }
}

/// <summary>What a fix re-queued, in queue order (front first).</summary>
public sealed record FixResult(IReadOnlyList<(long GogId, string FileKey)> Keys)
{
    public int Count => Keys.Count;
    public static readonly FixResult Nothing = new(Array.Empty<(long, string)>());
}

/// <summary>
/// THE fix sequence: pick the problem files, reset each for a fresh fetch (strikes cleared, Corrupt lifted,
/// partial forgotten, state NotBackedUp) and put them AT THE FRONT of the persisted queue in selection
/// order (a fix the user just clicked outranks a backlog of hundreds), roll the touched products up, save.
/// The App spelled this out in RequeueProblemFiles, RetryRunFailure, RedownloadGame and
/// RedownloadInventoryFile before 09-08. Nothing downloads here: the caller runs a
/// <see cref="BackupRun"/> with <c>FromPersistedQueue</c>, or hands the keys to its live engine.
/// </summary>
public static class FixRun
{
    /// <summary>A download failure worth retrying: strikes on a file that is still a gap. Corrupt is the
    /// verify pass's verdict and belongs to the Fix flow (it confirms before re-fetching); Unavailable is an
    /// entitlement, not a retryable gap. The App's "HasFixable" / "Try again" counts read this.</summary>
    public static bool IsFailedDownload(GameFile f)
        => !BackupScope.IsPresent(f) && f.FailedAttempts > 0
           && f.State is FileState.NotBackedUp or FileState.Missing or FileState.Outdated;

    /// <summary>The files a selector would touch, in manifest order, minus <paramref name="exclude"/> (files a
    /// live engine is mid-attempt on: resetting one under a worker made the manifest lie about the partial
    /// and handed out extra strikes). Read-only; backs counts and the CLI's --dry-run.</summary>
    public static List<(LibraryItem Item, GameFile File)> Select(IManifestStore store, FixSelector selector,
                                                                 IReadOnlySet<(long, string)>? exclude = null)
        => store.Read(m => Select(m, selector, exclude));

    public static List<(LibraryItem Item, GameFile File)> Select(LibraryManifest m, FixSelector selector,
                                                                 IReadOnlySet<(long, string)>? exclude = null)
    {
        var list = new List<(LibraryItem, GameFile)>();
        foreach (var i in m.Items)
            foreach (var f in i.Files)
                if (selector.Matches(i, f) && (exclude is null || !exclude.Contains((i.GogId, f.FileKey))))
                    list.Add((i, f));
        return list;
    }

    /// <summary>How many files a fix of this kind would touch (badge counts).</summary>
    public static int Count(IManifestStore store, FixSelector selector, IReadOnlySet<(long, string)>? exclude = null)
        => Select(store, selector, exclude).Count;

    /// <summary>Re-queue the selection at the front of the persisted queue and save. Returns the keys in
    /// their new queue order (empty when nothing matched; nothing is saved then either).</summary>
    public static async Task<FixResult> RequeueAsync(IManifestStore store, FixSelector selector,
                                                     IReadOnlySet<(long, string)>? exclude = null,
                                                     CancellationToken ct = default)
    {
        List<(long, string)> keys;
        using (store.Gate.Enter())   // (manifest gate 09-08) select + reset + reorder are one atomic change
        {
            var m = store.Current;
            var affected = Select(m, selector, exclude);
            if (affected.Count == 0) return FixResult.Nothing;
            keys = new List<(long, string)>(affected.Count);
            int pos = 0;
            foreach (var (item, f) in affected)
            {
                // Reset the strike count, not just the state: a condemned file sits at FailedAttempts >=
                // MaxAttempts and would be re-condemned instantly. RequeueFresh clears both and never
                // resumes condemned bytes.
                DownloadSettlement.RequeueFresh(m, item.GogId, f);
                m.Downloads.MoveTo(item.GogId, f.FileKey, pos++);   // front of the queue, selection order kept
                keys.Add((item.GogId, f.FileKey));
            }
            foreach (var item in affected.Select(a => a.Item).Distinct())
                LibrarySyncService.RecomputeStatus(item);
        }
        await store.SaveAsync(ct);
        return new FixResult(keys);
    }
}

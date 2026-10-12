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

[Flags]
public enum FixKind
{
    None = 0,
    FailedDownloads = 1,
    CorruptFiles = 2,
    MissingFiles = 4,
    Game = 8,
    File = 16,
    Keys = 32,
}

public sealed record FixSelector(FixKind Kinds, long? GogId = null, string? FileKey = null)
{
    public IReadOnlySet<(long GogId, string FileKey)>? Keys { get; init; }
    public static readonly FixSelector Failed = new(FixKind.FailedDownloads);
    public static FixSelector FailedOrKeys(IEnumerable<(long GogId, string FileKey)> keys)
        => new(FixKind.FailedDownloads | FixKind.Keys) { Keys = keys.ToHashSet() };
    public static readonly FixSelector Corrupt = new(FixKind.CorruptFiles);
    public static readonly FixSelector Missing = new(FixKind.MissingFiles);
    public static readonly FixSelector CorruptOrMissing = new(FixKind.CorruptFiles | FixKind.MissingFiles);
    public static FixSelector Game(long gogId) => new(FixKind.Game, gogId);
    public static FixSelector File(long gogId, string fileKey) => new(FixKind.File, gogId, fileKey);

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

public sealed record FixResult(IReadOnlyList<(long GogId, string FileKey)> Keys)
{
    public int Count => Keys.Count;
    public static readonly FixResult Nothing = new(Array.Empty<(long, string)>());
}

public static class FixRun
{
    public static bool IsFailedDownload(GameFile f)
        => !BackupScope.IsPresent(f) && f.FailedAttempts > 0
           && f.State is FileState.NotBackedUp or FileState.Missing or FileState.Outdated;

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

    public static int Count(IManifestStore store, FixSelector selector, IReadOnlySet<(long, string)>? exclude = null)
        => Select(store, selector, exclude).Count;

    public static async Task<FixResult> RequeueAsync(IManifestStore store, FixSelector selector,
                                                     IReadOnlySet<(long, string)>? exclude = null,
                                                     CancellationToken ct = default)
    {
        List<(long, string)> keys;
        using (store.Gate.Enter())   // select + reset + reorder are one atomic change
        {
            var m = store.Current;
            var affected = Select(m, selector, exclude);
            if (affected.Count == 0) return FixResult.Nothing;
            keys = new List<(long, string)>(affected.Count);
            int pos = 0;
            foreach (var (item, f) in affected)
            {
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

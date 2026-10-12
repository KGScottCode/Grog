// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Grog.Core.Volumes;

public enum ReorgReason { Layout = 0, ChangeFolder = 1, Relocate = 2 }

public sealed class ReorgJob
{
    public const int CurrentSchema = 1;
    public int Schema { get; set; } = CurrentSchema;
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public ReorgReason Reason { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ReorgMove> Moves { get; set; } = new();

    public bool Paused { get; set; }

    public string? RepointRootId { get; set; }
    public string? RepointPath { get; set; }

    [JsonPropertyName("Cancelled")]   // on-disk name pinned: existing journals must still resume
    public bool Canceled { get; set; }

    [JsonIgnore] public object SyncRoot { get; } = new();

    [JsonIgnore] public List<string> UnavailableDrives { get; } = new();

    [JsonIgnore] public bool HasUnavailableDrives => UnavailableDrives.Count > 0;

    [JsonIgnore] public List<ReorgSkipped> NotFitting { get; } = new();

    public void NoteUnavailableDrive(string? label)
    {
        var name = string.IsNullOrWhiteSpace(label) ? "a backup drive" : label!;
        if (!UnavailableDrives.Contains(name, StringComparer.OrdinalIgnoreCase))
            UnavailableDrives.Add(name);
    }


    [JsonIgnore] public ReorgMove? Current { get; set; }

    public int Total => Moves.Count;
    public int Settled => Moves.Count(m => m.IsSettled);
    public bool IsComplete => Moves.All(m => m.IsSettled);
    public bool HasFailures => Moves.Any(m => m.State == ReorgMoveState.Failed);
    public long BytesTotal => Moves.Sum(m => m.SizeBytes);
    public long BytesSettled => Moves.Where(m => m.IsSettled).Sum(m => m.SizeBytes);

    public List<ReorgMove> TryAppend(ReorgJob planned)
    {
        var added = new List<ReorgMove>();
        if (Reason != ReorgReason.Relocate || planned.Reason != ReorgReason.Relocate) return added;
        lock (SyncRoot)
        {
            var waiting = Moves.Where(x => !x.IsSettled).Select(x => (x.GogId, x.FileKey)).ToHashSet();
            foreach (var mv in planned.Moves)
                if (waiting.Add((mv.GogId, mv.FileKey))) { Moves.Add(mv); added.Add(mv); }
        }
        return added;
    }

    public List<(string TargetPath, long Bytes)> RemainingWrites()
    {
        lock (SyncRoot)
            return Moves.Where(x => !x.IsSettled && x.Kind == ReorgMoveKind.CopyVerifyDelete)
                        .Select(x => (x.ToAbs, Math.Max(0L, x.SizeBytes - x.CopiedBytes))).ToList();
    }

    public int Unsettled { get { lock (SyncRoot) return Moves.Count(x => !x.IsSettled); } }

    public IReadOnlyList<ReorgMove> Pending()
    {
        lock (SyncRoot) return Moves.Where(m => m.State == ReorgMoveState.Pending).ToList();
    }
}


public sealed record ReorgSkipped(string Title, string FileName, long SizeBytes, string Reason);

public sealed record MoveRoom(long FreeBytes, long MaxFileBytes, long ClusterBytes = 0)
{
    public long Charge(long bytes) => Grog.Core.Storage.DriveResolver.OnDisk(bytes, ClusterBytes);
    public string LimitReason => $"over this drive's per-file limit ({Format.ByteFormat.Size(MaxFileBytes)})";
    public const string NoRoomReason = "not enough room";
}

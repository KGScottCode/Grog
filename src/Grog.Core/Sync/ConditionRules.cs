// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Volumes;

namespace Grog.Core.Sync;

/// <summary>The durable "what's on disk" axis for one file. Distinct from Activity (transient) and from
/// the grid's BackupStatus (a game-level rollup). Present vs Verified = has-it, hash-checked or not.
/// Detached is the safe cousin of Missing: the bytes are fine, they're just on an unplugged removable
/// drive. Partial means a resumable .grog-tmp is on disk but the file isn't complete.</summary>
public enum FileCondition
{
    NotBackedUp = 0,  // nothing on disk
    Partial = 1,      // a .grog-tmp exists; incomplete
    Present = 2,      // full bytes on disk, not hash-verified
    Verified = 3,     // full bytes, hash-checked
    Outdated = 4,     // present, but GOG shows a newer version
    Corrupt = 5,      // failed verification
    Missing = 6,      // manifest says present, gone from an ONLINE drive -> re-download
    Detached = 7,     // present, but its removable drive is unplugged -> safe, not missing
}

/// <summary>A device's current reachability, as ConditionRules needs it.</summary>
public readonly record struct DeviceAvailability(RootState State, bool Removable)
{
    public bool Online => State == RootState.Online;
    /// <summary>Not reachable right now but declared removable -> anything on it is "safe, detached",
    /// not "missing". Covers Offline (unplugged), Lost, and Replaced removable drives.</summary>
    public bool DetachedDrive => State != RootState.Online && Removable;

    /// <summary>The default when a file names no device (or an unknown one): treat as an online,
    /// non-removable root, so a genuinely-gone file reads Missing rather than being excused as detached.</summary>
    public static readonly DeviceAvailability OnlineFixed = new(RootState.Online, false);
}

/// <summary>
/// Condition as a derived read: resolves the persisted FileState plus device availability, so a present
/// file on an unplugged removable drive is Detached (safe) while a file gone from an online drive is
/// Missing. Pure; feed it a device lookup.
/// </summary>
public static class ConditionRules
{
    /// <summary>Maps a file's device (RootId) to that device's availability. Return
    /// <see cref="DeviceAvailability.OnlineFixed"/> for unknown/unset roots so missing files aren't
    /// silently excused.</summary>
    public delegate DeviceAvailability DeviceLookup(string? rootId);

    /// <summary>Build a lookup from a manifest's roots. Unknown/unset root ids resolve to OnlineFixed.</summary>
    public static DeviceLookup LookupFrom(LibraryManifest manifest)
    {
        var byId = new Dictionary<string, DeviceAvailability>(StringComparer.Ordinal);
        foreach (var r in manifest.Roots) byId[r.Id] = new DeviceAvailability(r.State, r.Removable);
        return rootId => rootId is not null && byId.TryGetValue(rootId, out var a) ? a : DeviceAvailability.OnlineFixed;
    }

    /// <summary>Resolve a file's Condition.</summary>
    public static FileCondition Of(GameFile f, DeviceLookup lookup)
    {
        bool detached = lookup(f.RootId).DetachedDrive;
        return f.State switch
        {
            FileState.Verified => detached ? FileCondition.Detached : FileCondition.Verified,
            FileState.Present => detached ? FileCondition.Detached : FileCondition.Present,
            FileState.Outdated => detached ? FileCondition.Detached : FileCondition.Outdated,
            FileState.Corrupt => FileCondition.Corrupt,
            FileState.Missing => detached ? FileCondition.Detached : FileCondition.Missing,
            // NotDownloaded / Queued / Downloading: on disk we either have a resumable partial or nothing.
            _ => f.HasPartial ? FileCondition.Partial : FileCondition.NotBackedUp,
        };
    }

    /// <summary>True when the file's bytes are on disk and usable (Present/Verified/Outdated), whether or
    /// not the drive is currently reachable (Detached still counts as "have it").</summary>
    public static bool HasBytes(FileCondition c)
        => c is FileCondition.Present or FileCondition.Verified or FileCondition.Outdated or FileCondition.Detached;
}

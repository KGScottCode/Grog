// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Volumes;

namespace Grog.Core.Sync;

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

public readonly record struct DeviceAvailability(RootState State, bool Removable)
{
    public bool Online => State == RootState.Online;
    public bool DetachedDrive => State != RootState.Online && Removable;

    public static readonly DeviceAvailability OnlineFixed = new(RootState.Online, false);
}

public static class ConditionRules
{
    public delegate DeviceAvailability DeviceLookup(string? rootId);

    public static DeviceLookup LookupFrom(LibraryManifest manifest)
    {
        var byId = new Dictionary<string, DeviceAvailability>(StringComparer.Ordinal);
        foreach (var r in manifest.Roots) byId[r.Id] = new DeviceAvailability(r.State, r.Removable);
        return rootId => rootId is not null && byId.TryGetValue(rootId, out var a) ? a : DeviceAvailability.OnlineFixed;
    }

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

    public static bool HasBytes(FileCondition c)
        => c is FileCondition.Present or FileCondition.Verified or FileCondition.Outdated or FileCondition.Detached;
}

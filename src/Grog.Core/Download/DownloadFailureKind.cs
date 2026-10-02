// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;

namespace Grog.Core.Download;

/// <summary>(09-22) What KIND of failure a file hit, decided where the exception is caught (typed, never by
/// reading its message). Journalled per file so every surface says the same thing about a run.</summary>
public enum DownloadFailureKind { Other = 0, Network = 1, DiskFull = 2, DriveGone = 3 }

/// <summary>(09-25) The file's storage is not there (unplugged, ejected, unmounted): nothing about the file failed.</summary>
public sealed class DriveGoneException : IOException
{
    public DriveGoneException(string root) : base($"Storage is not connected: {root}") { }
}

/// <summary>The transfer went silent for longer than the stall timeout: the connection is gone, the .part stays.</summary>
public sealed class StallException : IOException
{
    public StallException(TimeSpan after) : base($"Stalled: no data for {after.TotalSeconds:F0}s") { }
    public StallException(string message) : base(message) { }
}

public static class DownloadFailure
{
    /// <summary>Classify a transfer exception. Disk-full wins (it is the drive, not the link); then any
    /// connection-level cause anywhere in the chain; everything else is Other.</summary>
    public static DownloadFailureKind Of(Exception ex)
    {
        if (DownloadEngine.IsDiskFull(ex)) return DownloadFailureKind.DiskFull;
        for (Exception? e = ex; e is not null; e = e.InnerException)
            // An OperationCanceledException that reaches a failure is a timeout (the resolve / probe round trips cancel
            // on their own clock; the engine's own cancels never settle as failures): the link, not the file (QA 09-30 C9).
            if (e is StallException or HttpRequestException or SocketException or TimeoutException or OperationCanceledException
                || e is HttpIOException)
                return DownloadFailureKind.Network;
        return DownloadFailureKind.Other;
    }
}

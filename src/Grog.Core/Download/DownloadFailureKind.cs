// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;

namespace Grog.Core.Download;

public enum DownloadFailureKind { Other = 0, Network = 1, DiskFull = 2, DriveGone = 3 }

public sealed class DriveGoneException : IOException
{
    public DriveGoneException(string root) : base($"Storage is not connected: {root}") { }
}

public sealed class StallException : IOException
{
    public StallException(TimeSpan after) : base($"Stalled: no data for {after.TotalSeconds:F0}s") { }
    public StallException(string message) : base(message) { }
}

public static class DownloadFailure
{
    public static DownloadFailureKind Of(Exception ex)
    {
        if (DownloadEngine.IsDiskFull(ex)) return DownloadFailureKind.DiskFull;
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is StallException or HttpRequestException or SocketException or TimeoutException or OperationCanceledException
                || e is HttpIOException)
                return DownloadFailureKind.Network;
        return DownloadFailureKind.Other;
    }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Grog.Core.Verify;

/// <summary>One place that turns a file into a hex checksum. Verify (integrity checks) and the reorg
/// runner (post-copy verification) both hash the same way, so the algorithm lives here once.</summary>
public static class Hashing
{
    /// <summary>Lowercase hex MD5 of the file at <paramref name="path"/>. MD5 because that's the
    /// checksum GOG publishes; this is corruption detection, not a security boundary.</summary>
    public static async Task<string> Md5Async(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        using var md5 = MD5.Create();
        return Convert.ToHexString(await md5.ComputeHashAsync(stream, ct)).ToLowerInvariant();
    }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Grog.Core.Verify;

public static class Hashing
{
    public static async Task<string> Md5Async(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        using var md5 = MD5.Create();
        return Convert.ToHexString(await md5.ComputeHashAsync(stream, ct)).ToLowerInvariant();
    }
}

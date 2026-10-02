// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Security.Cryptography;
using Grog.Core.Tests.Framework;

// Download-engine unit coverage that doesn't require GOG: MD5 correctness and the
// rate limiter's timing behavior. Full end-to-end download is exercised as a live
// integration step (M3 checkpoint) rather than a unit test.
public class DownloadHelpersTests
{
    [Test]
    async Task Md5_MatchesKnownVector()
    {
        // MD5("") = d41d8cd98f00b204e9800998ecf8427e
        var dir = Directory.CreateTempSubdirectory("grog-md5-").FullName;
        try
        {
            var path = Path.Combine(dir, "empty.bin");
            await File.WriteAllBytesAsync(path, System.Array.Empty<byte>());
            await using var s = File.OpenRead(path);
            using var md5 = MD5.Create();
            var hash = System.Convert.ToHexString(await md5.ComputeHashAsync(s)).ToLowerInvariant();
            Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", hash, "empty-file md5");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    async Task Md5_KnownContent()
    {
        // MD5("abc") = 900150983cd24fb0d6963f7d28e17f72
        var dir = Directory.CreateTempSubdirectory("grog-md5b-").FullName;
        try
        {
            var path = Path.Combine(dir, "abc.bin");
            await File.WriteAllTextAsync(path, "abc");
            await using var s = File.OpenRead(path);
            using var md5 = MD5.Create();
            var hash = System.Convert.ToHexString(await md5.ComputeHashAsync(s)).ToLowerInvariant();
            Assert.Equal("900150983cd24fb0d6963f7d28e17f72", hash, "abc md5");
        }
        finally { Directory.Delete(dir, true); }
    }

    // SafeName is the sole guard between an untrusted server filename and the on-disk write path, so it
    // must always yield a single, safe component. Reached by reflection (private static, no test seam).
    static string SafeName(string input) => Grog.Core.Format.Naming.SafeFileName(input);

    [Test]
    void SafeName_NeutralizesTraversalAndSeparators()
    {
        // No result may contain a separator or a "..", so it can never escape its target directory.
        foreach (var evil in new[] { "../../etc/passwd", "..\\..\\Windows\\System32\\evil.dll", "/abs/path.bin", "a/b/c.exe" })
        {
            var s = SafeName(evil);
            Assert.False(s.Contains('/') || s.Contains('\\'), $"no separators survive: {evil} -> {s}");
            Assert.False(s.Contains(".."), $"no dot-dot survives: {evil} -> {s}");
            Assert.False(s.Length == 0, "never empty");
        }
    }

    [Test]
    void SafeName_KeepsRealNameButGuardsReservedAndLength()
    {
        Assert.Equal("setup.exe", SafeName("setup.exe"), "an ordinary name is left intact");
        Assert.Equal("_CON", SafeName("CON"), "reserved DOS device name is de-reserved");
        Assert.True(SafeName("CON.txt").StartsWith('_'), "reserved stem with extension is de-reserved too");
        Assert.True(SafeName(new string('a', 400) + ".bin").Length <= 150, "absurd length is capped");
        Assert.True(SafeName(new string('a', 400) + ".bin").EndsWith(".bin"), "extension preserved when capping");
        Assert.Equal("untitled", SafeName("   "), "blank -> a usable fallback");
    }
}

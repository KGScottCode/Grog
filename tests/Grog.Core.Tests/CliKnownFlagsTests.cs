// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Grog.Core.Tests.Framework;

/// <summary>
/// The CLI's unknown-flag validator is a hand-maintained literal set, and twice a shipped flag was missing
/// from it (--help, --retry-failed) - so the app's own validator rejected its own flags. This pins the set
/// to the source: every "--x" literal in Program.cs must appear in knownFlags.
/// </summary>
[NewBatch]
[Trait("cli")]
public class CliKnownFlagsTests
{
    private static string FindCliDir()
    {
        // Walk up to the repo root (marked by Grog.slnx), then down to the CLI source. TWO starting
        // points, because the binary is not always inside the tree: the container build compiles to a
        // scratch folder outside the repo, where a BaseDirectory-only walk can never succeed. That made
        // this test error on every container run, and a suite that always ends in "TEST FAILURE" is how
        // a real failure eventually gets waved through.
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "Grog.slnx")))
                    return Path.Combine(d.FullName, "src", "Grog.Cli");
        throw new InvalidOperationException(
            $"Could not locate the repo root (Grog.slnx) from {AppContext.BaseDirectory} or {Directory.GetCurrentDirectory()}");
    }

    [Test]
    void Every_flag_literal_in_the_CLI_is_in_knownFlags()
    {
        // knownFlags lives in Program.cs; the verbs that READ flags moved to CliVerbs.cs (09-02), so the
        // literal sweep covers every source file in the CLI project.
        var dir = FindCliDir();
        var src = string.Concat(Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                                         .Where(f => !f.Contains("_to_delete") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                                         .Select(File.ReadAllText));

        // The knownFlags initializer: everything between the HashSet's braces.
        var m = Regex.Match(src, @"knownFlags = new HashSet<string>[^{]*\{(?<body>[^}]*)\}", RegexOptions.Singleline);
        Assert.True(m.Success, "found the knownFlags initializer");
        var known = new HashSet<string>(
            Regex.Matches(m.Groups["body"].Value, "\"(--[a-z-]+)\"").Select(x => x.Groups[1].Value),
            StringComparer.OrdinalIgnoreCase);
        Assert.True(known.Count >= 40, $"knownFlags parsed ({known.Count} entries)");

        // Every other "--x" literal in the file is a flag the code reads somewhere.
        var used = Regex.Matches(src, "\"(--[a-z][a-z-]*)\"")
                        .Select(x => x.Groups[1].Value)
                        .Where(f => f != "--")
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = used.Where(f => !known.Contains(f)).OrderBy(f => f).ToList();
        Assert.True(missing.Count == 0,
            missing.Count == 0 ? "every flag literal is known"
                               : "flags read by the CLI but rejected by its own validator: " + string.Join(", ", missing));
    }
}

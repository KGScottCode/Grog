// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using Grog.Core.Format;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

namespace Grog.Core.Tests;

public sealed class EffectiveRootIdTests
{
    [Test]
    public void NullRootId_IsPrimary_SetOneWins()
    {
        var m = new LibraryManifest { PrimaryRootId = "primary" };
        Assert.Equal("primary", m.EffectiveRootId(new GameFile()), "no RootId: the primary");
        Assert.Equal("second", m.EffectiveRootId(new GameFile { RootId = "second" }), "a bound file keeps its root");
        Assert.Null(new LibraryManifest().EffectiveRootId(new GameFile()), "no primary yet: nothing to resolve to");
    }
}

public sealed class PluralTests
{
    [Test]
    public void Pluralise_WithSOrGivenForm()
    {
        Assert.Equal("1 file", Plural.Of(1, "file"), "one");
        Assert.Equal("0 files", Plural.Of(0, "file"), "zero takes the plural");
        Assert.Equal("3 files", Plural.Of(3, "file"), "many");
        Assert.Equal("2 copies", Plural.Of(2, "copy", "copies"), "an irregular plural");
        Assert.Equal("file", Plural.Noun(1, "file"), "the bare noun, singular");
        Assert.Equal("files", Plural.Noun(5, "file"), "the bare noun, plural");
    }
}

public sealed class FolderPrunerDeleteIfEmptyTests
{
    private sealed class FlakyIo : IFolderIo
    {
        public readonly List<string> Deleted = new();
        public int RefuseFirst;
        public bool Exists = true, Empty = true;
        public bool DirectoryExists(string path) => Exists;
        public bool DirectoryIsEmpty(string path) => Empty;
        public void DeleteDirectory(string path) { if (RefuseFirst-- > 0) throw new IOException("in use"); Deleted.Add(path); Exists = false; }
    }

    [Test]
    public void TransientLock_RetriedOnce_ThenGivesUp()
    {
        var once = new FlakyIo { RefuseFirst = 1 };
        Assert.True(FolderPruner.DeleteIfEmpty("/x/.grog-tmp", once), "gone after the retry");
        Assert.Equal(1, once.Deleted.Count, "deleted on the second attempt");

        var twice = new FlakyIo { RefuseFirst = 2 };
        Assert.False(FolderPruner.DeleteIfEmpty("/x/.grog-tmp", twice), "two refusals: left for the next tidy-up");
        Assert.Empty(twice.Deleted, "never deleted");
    }

    [Test]
    public void FolderWithContentOrAbsent_LeftAlone()
    {
        var full = new FlakyIo { Empty = false };
        Assert.False(FolderPruner.DeleteIfEmpty("/x/.grog-tmp", full), "content stays");
        Assert.Empty(full.Deleted, "no delete attempted");
        var gone = new FlakyIo { Exists = false };
        Assert.True(FolderPruner.DeleteIfEmpty("/x/.grog-tmp", gone), "already gone counts as gone");
    }

    [Test]
    public void On_disk_an_empty_folder_goes_and_a_used_one_stays()
    {
        var root = Path.Combine(Path.GetTempPath(), "grog-dif-" + Guid.NewGuid().ToString("N")[..8]);
        var empty = Path.Combine(root, "empty"); var used = Path.Combine(root, "used");
        Directory.CreateDirectory(empty); Directory.CreateDirectory(used); File.WriteAllText(Path.Combine(used, "a"), "x");
        try
        {
            Assert.True(FolderPruner.DeleteIfEmpty(empty), "empty: removed");
            Assert.False(Directory.Exists(empty), "and gone from disk");
            Assert.False(FolderPruner.DeleteIfEmpty(used), "used: kept");
            Assert.True(File.Exists(Path.Combine(used, "a")), "with its content");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}

public sealed class TestTimeoutTests
{
    [Test]
    public void Attribute_BeatsEnvVar_BeatsDefault()
    {
        var prev = Environment.GetEnvironmentVariable("GROG_TEST_TIMEOUT");
        try
        {
            Environment.SetEnvironmentVariable("GROG_TEST_TIMEOUT", null);
            Assert.Equal(120, TestRunner.TimeoutFor(new TestAttribute()), "default");
            Environment.SetEnvironmentVariable("GROG_TEST_TIMEOUT", "7");
            Assert.Equal(7, TestRunner.TimeoutFor(new TestAttribute()), "env var");
            Assert.Equal(3, TestRunner.TimeoutFor(new TestAttribute { TimeoutSeconds = 3 }), "attribute wins");
            Environment.SetEnvironmentVariable("GROG_TEST_TIMEOUT", "junk");
            Assert.Equal(120, TestRunner.TimeoutFor(new TestAttribute()), "an unparsable env var is ignored");
        }
        finally { Environment.SetEnvironmentVariable("GROG_TEST_TIMEOUT", prev); }
    }
}

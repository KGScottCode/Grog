// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Linq;
using Grog.Core.Api;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

// A file GOG lists but refuses to serve this account is NOT corruption and NOT a gap the user can close.
// It was mislabeled "failed verification (corrupt)" and retried forever; these lock the corrected rules.
public class UnavailableFileTests
{
    static LibraryItem Game(params (string key, FileState state)[] files)
    {
        var it = new LibraryItem { GogId = 7, Title = "Venba", Slug = "venba" };
        foreach (var (key, state) in files)
            it.Files.Add(new GameFile
            {
                GameGogId = 7, FileKey = key, Kind = FileKind.Installer, Name = key, State = state,
                LocalRelativePath = state is FileState.Present or FileState.Verified ? $"Games/venba/{key}" : null,
                ExpectedSizeBytes = 100,   // a real download: sizeless files are Unavailable by rule (09-13)
            });
        return it;
    }

    [Test]
    void Unavailable_IsExcludedFromCompleteness()
    {
        // One real installer, backed up; one extra GOG won't serve. That IS a complete backup.
        var it = Game(("installer", FileState.Verified), ("deadextra", FileState.Unavailable));
        var scope = Scope.Both;
        Assert.Equal(1, BackupScope.Scoped(it, scope).Count(), "the refused file is not scored");
        Assert.Equal(BackupStatus.Complete, BackupScope.Status(it, scope),
            "a library is not 'partial' because GOG withheld something");
    }

    [Test]
    void Unavailable_DoesNotMakeTheGameAnError()
    {
        var it = Game(("installer", FileState.Verified), ("deadextra", FileState.Unavailable));
        LibrarySyncService.RecomputeStatus(it);
        Assert.Equal(BackupStatus.Complete, it.Status, "refused != corrupt; the rollup stays green");
    }

    [Test]
    void UnavailableOnly_GameIsNotStuckPartial()
    {
        var it = Game(("deadextra", FileState.Unavailable));
        LibrarySyncService.RecomputeStatus(it);
        Assert.Equal(BackupStatus.Unknown, it.Status, "nothing scoreable left -> not a permanent gap");
    }

    // GOG's refusal must never be mistaken for a flaky download: no strikes, no Corrupt.
    [Test]
    void RefusalCarriesGogsOwnWording()
    {
        const string body = """{"message":"Product is currently in Advanced Access period and account in context does not have a permit"}""";
        var reason = GogApiClient.ExtractRefusalReason(body);
        Assert.Equal("Product is currently in Advanced Access period and account in context does not have a permit",
            reason, "GOG's message is surfaced verbatim");
    }

    [Test]
    void RefusalReason_FallsBackAndIgnoresHtml()
    {
        Assert.Equal("plain refusal text", GogApiClient.ExtractRefusalReason("  plain refusal text  "),
            "bare text is used as-is");
        Assert.Equal("", GogApiClient.ExtractRefusalReason("<html><body>Forbidden</body></html>"),
            "an HTML error page yields nothing usable");
        Assert.Equal("", GogApiClient.ExtractRefusalReason(""), "empty body -> empty reason");
    }

    [Test]
    void RefusalReason_IsCappedForDisplay()
    {
        var reason = GogApiClient.ExtractRefusalReason(new string('x', 500));
        Assert.True(reason.Length <= 303, "long bodies can't become a wall of text");
        Assert.True(reason.EndsWith("..."), "and are visibly truncated");
    }

    // The self-heal: sync is where GOG's view refreshes, so it clears the flag and lets the file retry once.
    [Test]
    void Sync_ClearsUnavailableSoEntitlementChangesSelfHeal()
    {
        var manifest = new Grog.Core.Manifest.LibraryManifest();
        var existing = Game(("deadextra", FileState.Unavailable));
        existing.Files[0].UnavailableReason = "no permit";
        manifest.Items.Add(existing);

        var incoming = Game(("deadextra", FileState.NotBackedUp));
        LibrarySyncService.MergeIntoForTests(existing, incoming);

        Assert.Equal(FileState.NotBackedUp, existing.Files[0].State, "cleared, so the next run re-tries once");
        Assert.True(existing.Files[0].UnavailableReason is null, "stale reason dropped with the state");
    }
}

// The on-disk log exists so an UNATTENDED 03:00 failure is still explainable the next morning -- the
// in-memory list dies with the process, which is exactly backwards for a scheduled backup tool.
public class LogFileTests
{
    static Grog.Core.Storage.LogFile Fresh()
        => new(System.IO.Directory.CreateTempSubdirectory("grog-log-").FullName);

    // The writer holds the file open for the process; a reader opens it shared, as any log viewer must.
    static string[] ReadLines(string path)
    {
        using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
        using var sr = new System.IO.StreamReader(fs);
        var all = sr.ReadToEnd();
        return all.Split('\n', System.StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
    }

    [Test]
    void Append_WritesOneGreppableLinePerEntry()
    {
        var log = Fresh();
        log.Append(new System.DateTimeOffset(2026, 8, 7, 3, 0, 0, System.TimeSpan.Zero), "Download", true, "boom");
        log.Append(new System.DateTimeOffset(2026, 8, 7, 3, 0, 1, System.TimeSpan.Zero), "Verify", false, "fine");
        var lines = ReadLines(log.Path);
        Assert.Equal(2, lines.Length, "one line per entry");
        Assert.True(lines[0].Contains("ERROR") && lines[0].Contains("[Download]") && lines[0].Contains("boom"), "error line");
        Assert.True(lines[1].Contains("INFO") && lines[1].Contains("fine"), "info line");
    }

    [Test]
    void Append_FlattensMultilineSoOneEntryStaysOneLine()
    {
        var log = Fresh();
        log.Append(System.DateTimeOffset.UnixEpoch, "General", true, "line one\r\nline two");
        Assert.Equal(1, ReadLines(log.Path).Length, "a multi-line message can't fake extra entries");
    }

    [Test]
    void Append_IgnoresEmptyMessages()
    {
        var log = Fresh();
        log.Append(System.DateTimeOffset.UnixEpoch, "General", false, "   ");
        Assert.True(!System.IO.File.Exists(log.Path), "nothing to say, nothing written");
    }

    [Test]
    void Rotates_OncePastTheCap()
    {
        // A file already over the cap when the writer first opens (a previous process left it) rotates on open.
        var log = Fresh();
        log.RotateAt = 4096;
        System.IO.File.WriteAllText(log.Path, new string('x', 4097));
        log.Append(System.DateTimeOffset.UnixEpoch, "General", false, "after rotation");
        Assert.True(System.IO.File.Exists(log.PreviousPath), "the oversized file moved aside");
        var now = string.Join("\n", ReadLines(log.Path));
        Assert.True(now.Contains("after rotation") && now.Length < 200, "the live log restarted small");
    }

    [Test]
    void Rotates_OnceTheOpenStreamPassesTheCap()
    {
        // The open writer watches its own length: once past the cap the next line lands in a fresh file.
        var log = Fresh();
        log.RotateAt = 1024;
        for (int i = 0; i < 40; i++) log.Append(System.DateTimeOffset.UnixEpoch, "General", false, "line " + i + " " + new string('y', 40));
        Assert.True(System.IO.File.Exists(log.PreviousPath), "rotated at least once from the running count");
        Assert.True(new System.IO.FileInfo(log.Path).Length < 1024 + 200, "the live file restarted at the rotation point");
        Assert.True(string.Join("\n", ReadLines(log.Path)).Contains("line 39"), "the last line is in the live file");
        log.Close();
    }

    // Generations shift down and the oldest falls off, so disk use is bounded at (Generations + 1) files.
    [Test]
    void Rotation_ShiftsGenerationsAndDropsTheOldest()
    {
        var log = Fresh();
        log.RotateAt = 512;
        for (int i = 1; i <= Grog.Core.Storage.LogFile.Generations + 1; i++)
        {
            for (int k = 0; k < 12; k++) log.Append(System.DateTimeOffset.UnixEpoch, "General", false, "gen" + i + " " + new string('z', 40));
        }
        for (int n = 1; n <= Grog.Core.Storage.LogFile.Generations; n++)
            Assert.True(System.IO.File.Exists(log.GenerationPath(n)), "generation " + n + " kept");
        Assert.True(!System.IO.File.Exists(log.GenerationPath(Grog.Core.Storage.LogFile.Generations + 1)),
            "nothing beyond the cap survives");
        log.Close();
    }

    // The App holds three LogFile instances on one grog.log; two open streams on one file clobbered each other.
    [Test]
    void TwoInstancesOnOnePath_InterleaveWithoutLosingLines()
    {
        var dir = System.IO.Directory.CreateTempSubdirectory("grog-log-").FullName;
        var a = new Grog.Core.Storage.LogFile(dir);
        var b = new Grog.Core.Storage.LogFile(dir);
        a.Append(System.DateTimeOffset.UnixEpoch, "General", false, "a1");
        b.Append(System.DateTimeOffset.UnixEpoch, "General", false, "b1");
        a.Append(System.DateTimeOffset.UnixEpoch, "General", false, "a2");
        b.Append(System.DateTimeOffset.UnixEpoch, "General", false, "b2");
        var lines = ReadLines(a.Path);
        Assert.Equal(4, lines.Length, "every line landed");
        Assert.True(lines[0].EndsWith("a1") && lines[1].EndsWith("b1") && lines[2].EndsWith("a2") && lines[3].EndsWith("b2"), "in order");
        a.Close();
        b.Append(System.DateTimeOffset.UnixEpoch, "General", false, "b3");
        Assert.Equal(5, ReadLines(a.Path).Length, "one instance closing does not close the other's stream");
        b.Close();
    }

    // The CLI appends to the same file from another process; the App's open stream must not overwrite it.
    [Test]
    void AnotherProcessAppending_IsNotOverwritten()
    {
        var log = Fresh();
        log.Append(System.DateTimeOffset.UnixEpoch, "General", false, "app1");
        using (var fs = new System.IO.FileStream(log.Path, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
        using (var w = new System.IO.StreamWriter(fs))
            w.WriteLine("1970-01-01 00:00:00 INFO  [Cli] cli1");
        log.Append(System.DateTimeOffset.UnixEpoch, "General", false, "app2");
        var lines = ReadLines(log.Path);
        Assert.Equal(3, lines.Length, "the other process's line survived");
        Assert.True(lines[1].EndsWith("cli1") && lines[2].EndsWith("app2"), "appended after it, not over it");
        log.Close();
    }
}

// A count the user cannot act on is worse than no count. The grid's per-game columns must agree with the
// queue: THIEF: Definitive Edition showed 10 files while only 9 were reachable, because the 10th was a
// French artwork extra excluded by an English language scope. These lock the SCOPE side of that contract
// (the grid columns consume exactly this predicate).
public class ScopedCountTests
{
    static LibraryItem Thief()
    {
        var it = new LibraryItem { GogId = 7, Title = "THIEF: Definitive Edition", Slug = "thief" };
        for (int i = 1; i <= 7; i++)
            it.Files.Add(new GameFile { GameGogId = 7, FileKey = "en1installer" + i, Kind = FileKind.Installer,
                Name = $"THIEF (Part {i} of 7)", Os = "windows", Language = "English",
                ExpectedSizeBytes = 1000, State = FileState.Verified });
        it.Files.Add(new GameFile { GameGogId = 7, FileKey = "x1", Kind = FileKind.Extra, ExtraType = "audio",
            Name = "soundtrack (MP3)", Os = "", Language = "", ExpectedSizeBytes = 100, State = FileState.Present });
        it.Files.Add(new GameFile { GameGogId = 7, FileKey = "x2", Kind = FileKind.Extra, ExtraType = "artworks",
            Name = "Thief: Tales From The City digital comic (EN)", Os = "", Language = "",
            ExpectedSizeBytes = 100, State = FileState.Present });
        // The phantom: GOG tags no language, so it is inferred from the NAME.
        it.Files.Add(new GameFile { GameGogId = 7, FileKey = "x3", Kind = FileKind.Extra, ExtraType = "artworks",
            Name = "Thief: Tales From The City digital comic (FR)", Os = "", Language = "",
            ExpectedSizeBytes = 100, State = FileState.NotBackedUp });
        return it;
    }

    static readonly Scope EnglishWindows = new(true, true, new[] { "English" }, new[] { "windows" });

    [Test]
    void FrenchExtra_IsInferredFromTheNameAndExcluded()
    {
        var fr = Thief().Files.Single(f => f.FileKey == "x3");
        Assert.Equal("French", Scope.LanguageOf(fr), "language read off the name, not the (empty) tag");
        Assert.True(!EnglishWindows.Includes(fr), "an English scope excludes it");
    }

    [Test]
    void ScopedCount_MatchesWhatCanActuallyBeQueued()
    {
        var it = Thief();
        Assert.Equal(10, it.Files.Count, "GOG lists ten");
        Assert.Equal(9, BackupScope.Scoped(it, EnglishWindows).Count(), "only nine are reachable");
    }

    [Test]
    void WithEveryInScopeFilePresent_TheGameReadsComplete()
    {
        var it = Thief();
        Assert.Equal(BackupStatus.Complete, BackupScope.Status(it, EnglishWindows),
            "the unreachable French extra must not hold the game at Partial forever");
    }

    [Test]
    void WideningTheScope_BringsTheExtraBack()
    {
        var it = Thief();
        var all = new Scope(true, true, null, new[] { "windows" });   // every language
        Assert.Equal(10, BackupScope.Scoped(it, all).Count(), "no language narrowing -> nothing dropped");
        Assert.Equal(BackupStatus.Partial, BackupScope.Status(it, all), "and now it IS a real gap");
    }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

// The Content picker's prices (09-11). The old three pills could not say what any choice cost, so a
// first-run user picked blind; the flyout now shows size and file count per option. These pin the one rule
// that makes those numbers useful -- the axis being priced is LIFTED, everything else still narrows -- and
// the one that makes them agree with the rest of the app.
[Trait("scope")]
public class ScopeTallyTests
{
    static LibraryManifest Library(params GameFile[] files)
    {
        var m = new LibraryManifest();
        var item = new LibraryItem { GogId = 1, Title = "Game", Slug = "game" };
        foreach (var f in files) { f.GameGogId = 1; item.Files.Add(f); }
        m.Items.Add(item);
        return m;
    }

    // Empty rather than null for the two tags: GameFile declares them non-nullable, and Scope reads both
    // through IsNullOrEmpty, so "" is the untagged case it already handles.
    static GameFile Game(long size, string os = "", string lang = "", FileState state = FileState.Present)
        => new() { FileKey = "g" + os + lang + size, Kind = FileKind.Installer, Name = "setup.exe",
                   Os = os, Language = lang, ExpectedSizeBytes = size, State = state };

    static GameFile Extra(long size, string name = "soundtrack", FileState state = FileState.Present)
        => new() { FileKey = "x" + name + size, Kind = FileKind.Extra, Name = name,
                   ExpectedSizeBytes = size, State = state };

    // THE rule. Priced under the current pick, "Extras only" would read 0 B while extras are switched off --
    // which is exactly the number a user weighing whether to switch them ON must not be shown.
    [Test]
    void TheContentAxisIsLifted_SoAnUnpickedOptionStillShowsItsCost()
    {
        var m = Library(Game(100), Extra(30));

        var t = ScopeTally.ByContent(m, Scope.GamesOnly);

        Assert.Equal(30L, t.Extras.Bytes, "extras are priced even though the pick excludes them");
        Assert.Equal(1, t.Extras.Files, "and counted");
        Assert.Equal(100L, t.Games.Bytes, "the picked option is unaffected");
    }

    // Both is the two halves, so the flyout can never show a total that disagrees with its own parts.
    [Test]
    void BothIsExactlyTheSumOfTheParts()
    {
        var m = Library(Game(100), Game(7), Extra(30));

        var t = ScopeTally.ByContent(m, Scope.Both);

        Assert.Equal(t.Games.Files + t.Extras.Files, t.Both.Files, "file counts add");
        Assert.Equal(t.Games.Bytes + t.Extras.Bytes, t.Both.Bytes, "byte weights add");
        Assert.Equal(137L, t.Both.Bytes, "and the sum is the library");
    }

    // The OTHER axes keep narrowing: a price measured with the language pick lifted would promise a backup
    // far larger than the one Apply would actually run.
    [Test]
    void LanguageNarrowingStillApplies()
    {
        var m = Library(Game(100, lang: "English"), Game(500, lang: "Polish"), Extra(30));

        var t = ScopeTally.ByContent(m, Scope.Both.WithLanguages(new[] { "English" }));

        Assert.Equal(100L, t.Games.Bytes, "the polish installer is out of scope and out of the price");
        Assert.Equal(1, t.Games.Files, "one installer, not two");
    }

    // Extras carry their own language list, and the price has to read the list that governs the kind it is
    // counting -- not the games list for everything.
    [Test]
    void ExtrasAreJudgedAgainstTheExtrasLanguageList()
    {
        var m = Library(Game(100), Extra(30, "Russian localization"), Extra(9, "soundtrack"));
        var narrow = Scope.Both.WithLanguages(new[] { "English" }).WithExtraLanguages(new[] { "Russian" });

        var t = ScopeTally.ByContent(m, narrow);

        Assert.Equal(39L, t.Extras.Bytes, "the russian extra is in scope under the extras list; the neutral one always is");
        Assert.Equal(2, t.Extras.Files, "both extras counted");
    }

    // Platform narrowing rides along too -- and extras are platform-neutral, so it must not touch them.
    [Test]
    void PlatformNarrowingAppliesToGamesAndNotToExtras()
    {
        var m = Library(Game(100, os: "windows"), Game(500, os: "osx"), Extra(30));

        var t = ScopeTally.ByContent(m, Scope.Both.WithPlatforms(new[] { "windows" }));

        Assert.Equal(100L, t.Games.Bytes, "the mac installer is excluded by the platform pick");
        Assert.Equal(30L, t.Extras.Bytes, "a soundtrack is not a windows soundtrack: extras are untouched");
    }

    // A file GOG refuses to serve is not a gap the user can close, so it is in no other stat either. A price
    // that counted it would be the one number in the app that cannot be reached.
    [Test]
    void UnavailableFilesAreExcluded()
    {
        var m = Library(Game(100), Game(500, state: FileState.Unavailable),
                        Extra(30), Extra(7, "artbook", FileState.Unavailable));

        var t = ScopeTally.ByContent(m, Scope.Both);

        Assert.Equal(100L, t.Games.Bytes, "the unavailable installer is not priced");
        Assert.Equal(30L, t.Extras.Bytes, "nor the unavailable extra");
        Assert.Equal(2, t.Both.Files, "two files remain");
    }

    // Expected size is what a backup will cost; local size is the fallback for a file GOG has not sized.
    // Same rule as every other planning number (Rollups.TotalBytesOf), not a second definition.
    [Test]
    void SizesUseExpectedFirstThenLocal()
    {
        var m = Library(
            new GameFile { GameGogId = 1, FileKey = "a", Kind = FileKind.Installer, Name = "setup.exe",
                           ExpectedSizeBytes = 100, LocalSizeBytes = 99, State = FileState.Present },
            new GameFile { GameGogId = 1, FileKey = "b", Kind = FileKind.Installer, Name = "patch.exe",
                           LocalSizeBytes = 5, State = FileState.Present },
            new GameFile { GameGogId = 1, FileKey = "c", Kind = FileKind.Installer, Name = "unknown.bin",
                           State = FileState.NotBackedUp });

        var t = ScopeTally.ByContent(m, Scope.Both);

        Assert.Equal(105L, t.Games.Bytes, "expected wins where present, local fills in, unknown weighs nothing");
        Assert.Equal(3, t.Games.Files, "an unsized file is still a file");
    }

    // An empty library is a zero price, not a crash and not a blank: the flyout still lists three options.
    [Test]
    void EmptyLibraryPricesEverythingAtZero()
    {
        var t = ScopeTally.ByContent(new LibraryManifest(), Scope.Both);

        Assert.Equal(0, t.Both.Files, "no files");
        Assert.Equal(0L, t.Both.Bytes, "no bytes");
    }
}

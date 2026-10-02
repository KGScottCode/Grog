// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Linq;
using Grog.Core.Api;
using Grog.Core.Tests.Framework;

public class GameDetailsParserTests
{
    // A fixture shaped exactly like a real /account/gameDetails response: a multi-part
    // Windows installer, a second language, a mac build, two extras, and one nested DLC.
    const string Fixture = """
    {
      "title": "Sample Game: Definitive Edition",
      "cdKey": "ABCD-1234-EFGH",
      "downloads": [
        [ "English", {
            "windows": [
              { "manualUrl": "/downlink/sample_game/en1installer0", "name": "Setup (Part 1 of 2)", "version": "2.1.0.5", "size": "1.5 GB" },
              { "manualUrl": "/downlink/sample_game/en1installer1", "name": "Setup (Part 2 of 2)", "version": "2.1.0.5", "size": "3.0 GB" }
            ],
            "mac": [
              { "manualUrl": "/downlink/sample_game/en1mac0", "name": "Mac Installer", "version": "2.1.0.5", "size": "4.2 GB" }
            ]
        } ],
        [ "Deutsch", {
            "windows": [
              { "manualUrl": "/downlink/sample_game/de1installer0", "name": "Setup (German)", "version": "2.1.0.5", "size": "1.5 GB" }
            ]
        } ]
      ],
      "extras": [
        { "manualUrl": "/downlink/file/sample_game/9001", "name": "manual (48 pages)", "type": "manuals", "size": "5 MB" },
        { "manualUrl": "/downlink/file/sample_game/9002", "name": "soundtrack (FLAC)", "type": "audio", "size": "512 MB" }
      ],
      "dlcs": [
        {
          "title": "Sample Game: Expansion Pack",
          "downloads": [
            [ "English", { "windows": [ { "manualUrl": "/downlink/sample_dlc/en1installer0", "name": "DLC Setup", "version": "1.0", "size": "800 MB" } ] } ]
          ],
          "extras": []
        }
      ]
    }
    """;

    [Test]
    void Parse_ReadsTitleAndSerial()
    {
        var d = GameDetailsParser.Parse(Fixture);
        Assert.Equal("Sample Game: Definitive Edition", d.Title, "title");
        Assert.Equal("ABCD-1234-EFGH", d.CdKey!, "cd key");
    }

    [Test]
    void Parse_FlattensAllInstallersAcrossLanguageAndOs()
    {
        var d = GameDetailsParser.Parse(Fixture);
        // en: 2 windows + 1 mac; de: 1 windows = 4 installer files total
        Assert.Equal(4, d.Installers.Count, "installer count");

        var enWin = d.Installers.Where(i => i.Language == "English" && i.Os == "windows").ToList();
        Assert.Equal(2, enWin.Count, "english windows parts");
        Assert.Contains("Part 1 of 2", enWin[0].Name, "first part name");

        var mac = Assert.Single(d.Installers.Where(i => i.Os == "mac"), "one mac installer");
        Assert.Equal("English", mac.Language, "mac language");

        var de = Assert.Single(d.Installers.Where(i => i.Language == "Deutsch"), "one german installer");
        Assert.Equal("windows", de.Os, "german os");
    }

    [Test]
    void Parse_PreservesVersionAndSizeText()
    {
        var d = GameDetailsParser.Parse(Fixture);
        var first = d.Installers.First();
        Assert.Equal("2.1.0.5", first.Version!, "version");
        Assert.Equal("1.5 GB", first.SizeText, "size text");
    }

    [Test]
    void Parse_ReadsExtrasWithType()
    {
        var d = GameDetailsParser.Parse(Fixture);
        Assert.Equal(2, d.Extras.Count, "extras count");
        var soundtrack = Assert.Single(d.Extras.Where(e => e.Type == "audio"), "one audio extra");
        Assert.Contains("soundtrack", soundtrack.Name, "soundtrack name");
    }

    [Test]
    void Parse_RecursesIntoNestedDlc()
    {
        var d = GameDetailsParser.Parse(Fixture);
        var dlc = Assert.Single(d.Dlcs, "one dlc");
        Assert.Equal("Sample Game: Expansion Pack", dlc.Title, "dlc title");
        var dlcInstaller = Assert.Single(dlc.Installers, "dlc has one installer");
        Assert.Equal("DLC Setup", dlcInstaller.Name, "dlc installer name");
    }

    [Test]
    void Parse_ToleratesMissingSectionsAndBlankSerial()
    {
        var d = GameDetailsParser.Parse("""{ "title": "Bare Game", "cdKey": "" }""");
        Assert.Equal("Bare Game", d.Title, "title");
        Assert.Null(d.CdKey, "blank serial -> null");
        Assert.Empty(d.Installers, "no installers");
        Assert.Empty(d.Extras, "no extras");
        Assert.Empty(d.Dlcs, "no dlcs");
    }
}

public class GameDetailsParserShapeTests
{
    [Test]
    void Parse_FlatDownloadsArray_MovieStyle_YieldsInstallers()
    {
        // Movies use a flat array of file objects instead of the [lang,{os}] nesting.
        const string movie = """
        {
          "title": "A Documentary Film",
          "downloads": [
            { "manualUrl": "/downlink/film/en1video1", "name": "Film (1080p)", "size": "1.1 GB" },
            { "manualUrl": "/downlink/film/en1video2", "name": "Film (720p)",  "size": "382 MB" }
          ],
          "extras": []
        }
        """;
        var d = GameDetailsParser.Parse(movie);
        Assert.Equal("A Documentary Film", d.Title, "title");
        Assert.Equal(2, d.Installers.Count, "flat downloads become installers");
        Assert.Contains("1080p", d.Installers[0].Name, "first file name");
    }

    [Test]
    void Parse_MixedGarbageEntries_SkippedNotCrashed()
    {
        // An entry that's neither a [lang,{os}] tuple nor a file object must be skipped.
        const string weird = """
        {
          "title": "Odd One",
          "downloads": [
            "just a string",
            [ "English", [ "not", "an", "object" ] ],
            [ "German", { "windows": [ { "manualUrl": "/dl/de0", "name": "Setup", "size": "1 GB" } ] } ]
          ]
        }
        """;
        var d = GameDetailsParser.Parse(weird);
        // Only the valid German windows installer should survive.
        var f = Assert.Single(d.Installers, "one valid installer survives");
        Assert.Equal("German", f.Language, "german survived");
    }
}

public class GameDetailsEmptyResponseTests
{
    [Test]
    void Parse_EmptyArray_ThrowsNoGameDetails()
    {
        // GOG returns "[]" for products with no account-level details.
        Assert.Throws<NoGameDetailsException>(() => GameDetailsParser.Parse("[]"), "bare [] -> NoGameDetails");
    }

    [Test]
    void Parse_EmptyArrayWithWhitespace_ThrowsNoGameDetails()
    {
        Assert.Throws<NoGameDetailsException>(() => GameDetailsParser.Parse("  [ ]  "), "whitespace [] -> NoGameDetails");
    }

    [Test]
    void CleanSerial_PlainKey_Unchanged()
    {
        Assert.Equal("4BCF-K2KR-TGEF-HPTM", GameDetailsParser.CleanSerial("4BCF-K2KR-TGEF-HPTM"), "plain key untouched");
        Assert.True(GameDetailsParser.CleanSerial("") is null, "empty → null");
        Assert.True(GameDetailsParser.CleanSerial(null) is null, "null → null");
    }

    [Test]
    void CleanSerial_ColonLabeledHtml_BecomesLines()
    {
        var raw = "<span>DOOM 3:</span><span>WBW2DCSHCTHRLLGR E4</span>" +
                  "<span>Resurrection of Evil DLC:</span><span>ABH77RC7TCT7HGA3 2E</span>";
        var cleaned = GameDetailsParser.CleanSerial(raw)!;
        Assert.True(!cleaned.Contains("<span>"), "no html tags remain");
        Assert.Equal("DOOM 3: WBW2DCSHCTHRLLGR E4\nResurrection of Evil DLC: ABH77RC7TCT7HGA3 2E", cleaned, "labeled lines");
    }

    [Test]
    void CleanSerial_AlternatingNameKeyHtml_PairsUp()
    {
        var raw = "<span>Neverwinter Nights 2 </span><span>9KNLX-CJ4DF-3MH7L-WL7NW-RE6QG-GWUNR-EJK3D</span>" +
                  "<span>Mask of the Betrayer </span><span>QYKWD-KYHGM-6CJFC-JCUC6-VK9CK-DLNAG-L4FJF</span>";
        var cleaned = GameDetailsParser.CleanSerial(raw)!;
        var lines = cleaned.Split('\n');
        Assert.Equal(2, lines.Length, "two pairs");
        Assert.Equal("Neverwinter Nights 2: 9KNLX-CJ4DF-3MH7L-WL7NW-RE6QG-GWUNR-EJK3D", lines[0], "first pair");
        Assert.Equal("Mask of the Betrayer: QYKWD-KYHGM-6CJFC-JCUC6-VK9CK-DLNAG-L4FJF", lines[1], "second pair");
    }
}

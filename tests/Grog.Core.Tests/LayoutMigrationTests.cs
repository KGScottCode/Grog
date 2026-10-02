// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Grog.Core.Models;
using Grog.Core.Volumes;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

public class LayoutMigrationTests
{
    [Test]
    void Games_AlwaysUnderGamesFolder()
    {
        Assert.Equal("Games/witcher-3/setup.exe",
            LayoutMigrationService.NewRelativeFor("witcher-3/setup.exe", "witcher-3", FileKind.Installer, ExtrasPlacement.SeparateByGame), "flat -> Games (per-game)");
        Assert.Equal("Games/witcher-3/setup.exe",
            LayoutMigrationService.NewRelativeFor("witcher-3/setup.exe", "witcher-3", FileKind.Installer, ExtrasPlacement.SeparateByType), "flat -> Games (sibling)");
        Assert.Null(LayoutMigrationService.NewRelativeFor("Games/witcher-3/setup.exe", "witcher-3", FileKind.Installer, ExtrasPlacement.SeparateByGame), "already correct");
    }

    // A soundtrack file so the migration classifies a real bucket ("Soundtracks"), matching the engine.
    static GameFile Ost() => new() { Kind = FileKind.Extra, Name = "ost.zip", ExtraType = "audio" };

    [Test]
    void Extras_GameFirst_UnderExtrasBySlugThenBucket()
    {
        // game-first: Extras/<slug>/<bucket>/<file>
        Assert.Equal("Extras/witcher-3/Soundtracks/ost.zip",
            LayoutMigrationService.NewRelativeFor("witcher-3/extras/ost.zip", "witcher-3", FileKind.Extra, ExtrasPlacement.SeparateByGame, Ost()), "flat -> game-first");
        // switching type-first -> game-first
        Assert.Equal("Extras/witcher-3/Soundtracks/ost.zip",
            LayoutMigrationService.NewRelativeFor("Extras/Soundtracks/witcher-3/ost.zip", "witcher-3", FileKind.Extra, ExtrasPlacement.SeparateByGame, Ost()), "type-first -> game-first");
        Assert.Null(LayoutMigrationService.NewRelativeFor("Extras/witcher-3/Soundtracks/ost.zip", "witcher-3", FileKind.Extra, ExtrasPlacement.SeparateByGame, Ost()), "already game-first");
    }

    [Test]
    void Extras_TypeFirst_UnderExtrasByBucketThenSlug()
    {
        // type-first: Extras/<bucket>/<slug>/<file>
        Assert.Equal("Extras/Soundtracks/witcher-3/ost.zip",
            LayoutMigrationService.NewRelativeFor("witcher-3/extras/ost.zip", "witcher-3", FileKind.Extra, ExtrasPlacement.SeparateByType, Ost()), "flat -> type-first");
        // switching game-first -> type-first
        Assert.Equal("Extras/Soundtracks/witcher-3/ost.zip",
            LayoutMigrationService.NewRelativeFor("Extras/witcher-3/Soundtracks/ost.zip", "witcher-3", FileKind.Extra, ExtrasPlacement.SeparateByType, Ost()), "game-first -> type-first");
        Assert.Null(LayoutMigrationService.NewRelativeFor("Extras/Soundtracks/witcher-3/ost.zip", "witcher-3", FileKind.Extra, ExtrasPlacement.SeparateByType, Ost()), "already type-first");
    }

    [Test]
    void Extras_WithGame_FlatInsideTheGameFolder()
    {
        // with-game: Games/<slug>/Extras/<file> -- flat, no buckets.
        Assert.Equal("Games/witcher-3/Extras/ost.zip",
            LayoutMigrationService.NewRelativeFor("witcher-3/extras/ost.zip", "witcher-3", FileKind.Extra, ExtrasPlacement.WithGame, Ost()), "flat -> with-game");
        Assert.Equal("Games/witcher-3/Extras/ost.zip",
            LayoutMigrationService.NewRelativeFor("Extras/witcher-3/Soundtracks/ost.zip", "witcher-3", FileKind.Extra, ExtrasPlacement.WithGame, Ost()), "game-first -> with-game");
        Assert.Equal("Games/witcher-3/Extras/ost.zip",
            LayoutMigrationService.NewRelativeFor("Extras/Soundtracks/witcher-3/ost.zip", "witcher-3", FileKind.Extra, ExtrasPlacement.WithGame, Ost()), "type-first -> with-game");
        Assert.Null(LayoutMigrationService.NewRelativeFor("Games/witcher-3/Extras/ost.zip", "witcher-3", FileKind.Extra, ExtrasPlacement.WithGame, Ost()), "already with-game");
        // Installers are untouched by the extras choice.
        Assert.Null(LayoutMigrationService.NewRelativeFor("Games/witcher-3/setup.exe", "witcher-3", FileKind.Installer, ExtrasPlacement.WithGame), "installer already correct");
    }

    [Test]
    void BackslashPaths_Normalized()
    {
        Assert.Equal("Games/witcher-3/setup.exe",
            LayoutMigrationService.NewRelativeFor("witcher-3\\setup.exe", "witcher-3", FileKind.Installer, ExtrasPlacement.SeparateByGame), "windows separators normalized");
    }
}

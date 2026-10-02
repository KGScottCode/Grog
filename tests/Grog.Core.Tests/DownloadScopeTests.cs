// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Linq;
using Grog.Core.Download;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;

public class DownloadScopeTests
{
    static LibraryItem Game(long id, string title, ProductType type = ProductType.Game) =>
        new() { GogId = id, Title = title, Type = type };

    static GameFile F(FileKind kind, string name = "", string os = "", string lang = "", string? extraType = null) =>
        new() { Kind = kind, Name = name, Os = os, Language = lang, ExtraType = extraType };

    static LibraryItem Sample()
    {
        var g = Game(1, "Baldur's Gate");
        g.Files.Add(F(FileKind.Installer, "setup_win", "windows"));
        g.Files.Add(F(FileKind.Installer, "setup_linux", "linux"));
        g.Files.Add(F(FileKind.Installer, "setup_mac", "mac"));
        g.Files.Add(F(FileKind.Patch, "patch_win", "windows"));
        g.Files.Add(F(FileKind.Extra, "soundtrack", extraType: "soundtrack"));
        g.Files.Add(F(FileKind.Extra, "manual", extraType: "manual"));
        g.Files.Add(F(FileKind.Extra, "wallpaper", extraType: "wallpapers"));
        return g;
    }

    [Test]
    void InstallersOnly_ExcludesExtrasAndPatches()
    {
        var scope = new DownloadScope { IncludeExtras = false, IncludePatches = false };
        var picked = DownloadFileSelector.Select(new[] { Sample() }, scope).Select(x => x.file.Kind).ToList();
        Assert.True(picked.All(k => k == FileKind.Installer), "only installers");
        Assert.Equal(3, picked.Count, "3 installers");
    }

    [Test]
    void ExtrasOnly_ByType_Soundtrack()
    {
        var scope = new DownloadScope { IncludeInstallers = false, IncludePatches = false, IncludeDlcInstallers = false };
        scope.ExtraTypes.Add("soundtrack");
        var picked = DownloadFileSelector.Select(new[] { Sample() }, scope).Select(x => x.file.Name).ToList();
        Assert.Equal(1, picked.Count, "one extra");
        Assert.Equal("soundtrack", picked[0], "the soundtrack");
    }

    [Test]
    void OsPriority_WithFallback_TakesHighestAvailable()
    {
        // Prefer linux; game has linux → take only linux installer.
        var scope = new DownloadScope { IncludeExtras = false, IncludePatches = false, OsFallback = true };
        scope.OsPriority.Add("linux");
        scope.OsPriority.Add("windows");
        var picked = DownloadFileSelector.Select(new[] { Sample() }, scope)
            .Where(x => x.file.Kind == FileKind.Installer).Select(x => x.file.Os).ToList();
        Assert.Equal(1, picked.Count, "one OS chosen");
        Assert.Equal("linux", picked[0], "linux preferred");
    }

    [Test]
    void OsPriority_Fallback_SkipsToWindowsWhenNoLinux()
    {
        var g = Game(2, "WinOnly");
        g.Files.Add(F(FileKind.Installer, "w", "windows"));
        g.Files.Add(F(FileKind.Installer, "m", "mac"));
        var scope = new DownloadScope { OsFallback = true };
        scope.OsPriority.Add("linux");
        scope.OsPriority.Add("windows");
        var picked = DownloadFileSelector.Select(new[] { g }, scope).Select(x => x.file.Os).ToList();
        Assert.Equal(1, picked.Count, "one installer");
        Assert.Equal("windows", picked[0], "fell back to windows");
    }

    [Test]
    void Blacklist_SkipsMatchingFiles()
    {
        var scope = new DownloadScope();
        scope.Blacklist.Add("wallpaper");
        var picked = DownloadFileSelector.Select(new[] { Sample() }, scope).Select(x => x.file.Name).ToList();
        Assert.True(!picked.Contains("wallpaper"), "wallpaper excluded");
        Assert.True(picked.Contains("manual"), "manual kept");
    }

    [Test]
    void TypeFilter_OnlyMods()
    {
        var game = Sample();
        var mod = Game(9, "KeeperFX", ProductType.Mod);
        mod.Files.Add(F(FileKind.Installer, "keeper", "windows"));
        var scope = new DownloadScope();
        scope.Types.Add(ProductType.Mod);
        var picked = DownloadFileSelector.Select(new[] { game, mod }, scope).Select(x => x.game.Title).Distinct().ToList();
        Assert.Equal(1, picked.Count, "only mod game");
        Assert.Equal("KeeperFX", picked[0], "the mod");
    }

    [Test]
    void TitleRegex_Filters()
    {
        var a = Sample();                       // Baldur's Gate
        var b = Game(3, "Cyberpunk 2077");
        b.Files.Add(F(FileKind.Installer, "cp", "windows"));
        var scope = new DownloadScope { TitleRegex = "^Baldur" };
        var picked = DownloadFileSelector.Select(new[] { a, b }, scope).Select(x => x.game.Title).Distinct().ToList();
        Assert.Equal(1, picked.Count, "one game");
        Assert.Equal("Baldur's Gate", picked[0], "regex matched");
    }
}

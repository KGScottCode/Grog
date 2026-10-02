// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.IO;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

/// <summary>Extras always live under their own Extras/ area; the HOW choice (game-first vs type-first) is
/// authoritative on EVERY drive. So the folder shape is the same whether extras sit beside their game, on a
/// separate drive (the "separate folder" WHERE), or on an overflow spill -- only the ROOT changes.</summary>
public class CoLocationLayoutTests
{
    static (JsonManifestStore store, string root) New()
    {
        var baseDir = Directory.CreateTempSubdirectory("grog-coloc-").FullName;
        var root = Path.Combine(baseDir, "primary");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(baseDir, "_cfg"));
        var store = new JsonManifestStore(GrogPaths.Resolve(root));
        store.LoadAsync().GetAwaiter().GetResult();
        return (store, root);
    }

    static GameFile Extra(string name, string type) =>
        new() { Kind = FileKind.Extra, Name = name, ExtraType = type };

    [Test] void PrimaryBindsToTheManifestsPath_NotTheCallersStaleOne()
    {
        // One fact: the primary root's PathHint. A host whose own setting lags (App after a promotion, or after
        // a CLI --primary) must FOLLOW the manifest, not drag the root onto its stale folder (owner-hit 09-04).
        var (store, root) = New();
        try
        {
            var moved = Path.Combine(Path.GetDirectoryName(root)!, "moved-primary");
            Directory.CreateDirectory(moved);
            _ = new BackupLayout(store.Current, root);   // first run: creates the primary, seeds PathHint
            var primary = store.Current.Roots.First(r => r.Id == store.Current.PrimaryRootId);
            primary.PathHint = moved;   // Change Folder / --primary / promotion wrote the new fact

            var layout = new BackupLayout(store.Current, root);   // caller still says the OLD folder
            Assert.Equal(Path.GetFullPath(moved), layout.RootPath(primary.Id)!, "bound to the manifest's path");
            Assert.Equal(Path.GetFullPath(moved), Path.GetFullPath(primary.PathHint), "and PathHint was not rewritten to the stale one");

            primary.PathHint = "";
            var seeded = new BackupLayout(store.Current, root);
            Assert.Equal(Path.GetFullPath(root), seeded.RootPath(primary.Id)!, "no PathHint yet: the caller's path seeds it");

            // The portable story: the stick came back on another letter, so the hint is DEAD and the caller's
            // path (re-anchored through the {portable} token) is live. The live one wins and heals the hint.
            primary.PathHint = Path.Combine(Path.GetDirectoryName(root)!, "gone-letter");
            var healed = new BackupLayout(store.Current, root);
            Assert.Equal(Path.GetFullPath(root), healed.RootPath(primary.Id)!, "a dead hint never displaces a live folder");
            Assert.Equal(Path.GetFullPath(root), primary.PathHint, "and the hint is healed");
        }
        finally { Directory.Delete(Path.GetFullPath(Path.Combine(root, "..")), true); }
    }

    [Test] void WithGame_IsTheFreshLibraryDefault_FlatExtrasInsideGameFolder()
    {
        var (store, root) = New();
        try
        {
            Assert.Equal(ExtrasPlacement.WithGame, store.Current.ExtrasLayout, "fresh library defaults to with-game");
            var layout = new BackupLayout(store.Current, root);
            var st = layout.ResolveTargetDir(1, "witcher", FileKind.Extra, out _, Extra("soundtrack (FLAC)", "audio"));
            Assert.True(st!.EndsWith(Path.Combine("Games", "witcher", "Extras")), "with-game → Games/<slug>/Extras, flat (no buckets)");
        }
        finally { Directory.Delete(Path.GetFullPath(Path.Combine(root, "..")), true); }
    }

    [Test] void WithGame_ExtrasFollowTheGamesRoot_ExtrasRoleRootIgnored()
    {
        var (store, root) = New();
        try
        {
            var secDir = Path.Combine(Path.GetDirectoryName(root)!, "secondary");
            Directory.CreateDirectory(secDir);
            var sec = new BackupRoot { Label = "Secondary", PathHint = secDir, State = RootState.Online };
            store.Current.Roots.Add(sec);

            var layout = new BackupLayout(store.Current, root);
            // The (stale, disabled-in-UI) Extras role root points at the secondary; WithGame must ignore it.
            store.Current.Routing.SetRole(ContentRole.Games, store.Current.PrimaryRootId!);
            store.Current.Routing.SetRole(ContentRole.Extras, sec.Id);
            var st = layout.ResolveTargetDir(1, "witcher", FileKind.Extra, out var rid, Extra("soundtrack (FLAC)", "audio"));

            Assert.Equal(store.Current.PrimaryRootId, rid, "with-game: extras route WITH the game, never by the Extras role");
            Assert.True(st!.EndsWith(Path.Combine("Games", "witcher", "Extras")), "flat with-game shape");
        }
        finally { Directory.Delete(Path.GetFullPath(Path.Combine(root, "..")), true); }
    }

    [Test] void GameFirst_SameDrive_GroupsByGameUnderExtras()
    {
        var (store, root) = New();
        try
        {
            store.Current.SetExtrasLayout(ExtrasPlacement.SeparateByGame);   // game-first
            var layout = new BackupLayout(store.Current, root);
            var st = layout.ResolveTargetDir(1, "witcher", FileKind.Extra, out _, Extra("soundtrack (FLAC)", "audio"));
            Assert.True(st!.EndsWith(Path.Combine("Extras", "witcher", "Soundtracks")), "game-first → Extras/<slug>/<bucket>");
        }
        finally { Directory.Delete(Path.GetFullPath(Path.Combine(root, "..")), true); }
    }

    [Test] void GameFirst_OnSeparateDrive_KeepsTheSameShape()
    {
        var (store, root) = New();
        try
        {
            var secDir = Path.Combine(Path.GetDirectoryName(root)!, "secondary");
            Directory.CreateDirectory(secDir);
            var sec = new BackupRoot { Label = "Secondary", PathHint = secDir, State = RootState.Online };
            store.Current.Roots.Add(sec);
            store.Current.SetExtrasLayout(ExtrasPlacement.SeparateByGame);   // game-first

            // Construct first (binds the primary + sets its id), THEN route extras to the separate folder.
            var layout = new BackupLayout(store.Current, root);
            store.Current.Routing.SetRole(ContentRole.Games, store.Current.PrimaryRootId!);
            store.Current.Routing.SetRole(ContentRole.Extras, sec.Id);
            var st = layout.ResolveTargetDir(1, "witcher", FileKind.Extra, out var rid, Extra("soundtrack (FLAC)", "audio"));

            Assert.Equal(sec.Id, rid, "extra routes to the separate extras folder");
            Assert.True(st!.EndsWith(Path.Combine("Extras", "witcher", "Soundtracks")),
                "HOW is authoritative on every drive → same game-first shape, just a different root");
        }
        finally { Directory.Delete(Path.GetFullPath(Path.Combine(root, "..")), true); }
    }

    [Test] void TypeFirst_GroupsByTypeUnderExtras()
    {
        var (store, root) = New();
        try
        {
            store.Current.SetExtrasLayout(ExtrasPlacement.SeparateByType);   // type-first
            var layout = new BackupLayout(store.Current, root);
            var st = layout.ResolveTargetDir(1, "witcher", FileKind.Extra, out _, Extra("soundtrack (FLAC)", "audio"));
            Assert.True(st!.EndsWith(Path.Combine("Extras", "Soundtracks", "witcher")), "type-first → Extras/<bucket>/<slug>");
        }
        finally { Directory.Delete(Path.GetFullPath(Path.Combine(root, "..")), true); }
    }

}

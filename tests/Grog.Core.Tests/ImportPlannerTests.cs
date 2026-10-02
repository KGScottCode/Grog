// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Linq;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;
using Grog.Core.Verify;

public class ImportPlannerTests
{
    static (JsonManifestStore store, string root) NewManifest()
    {
        var root = Directory.CreateTempSubdirectory("grog-import-").FullName;
        return (new JsonManifestStore(root), root);
    }

    static void Touch(string path, long size = 8)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    static LibraryItem Item(long id, string title, string slug, params (string key, string name, long size)[] files)
    {
        var it = new LibraryItem { GogId = id, Title = title, Slug = slug };
        foreach (var (key, name, size) in files)
            it.Files.Add(new GameFile { GameGogId = id, FileKey = key, Name = name, ExpectedSizeBytes = size, Kind = FileKind.Installer });
        return it;
    }

    // ---- pure helpers ----

    [Test]
    void FuzzyMatch_PrefersLongestMostSpecificTitle()
    {
        var (store, root) = NewManifest();
        // Two library items whose titles both appear inside the folder name.
        store.Current.Items.Add(Item(1, "DOOM 3", "doom-3", ("k1", "setup.exe", 100)));
        store.Current.Items.Add(Item(2, "DOOM 3: Phobos", "doom-3-phobos", ("k2", "setup.exe", 200)));
        // Folder that contains both "doom3" and the more specific "doom3phobos".
        var dir = Path.Combine(root, "Doom 3 Phobos (Mod) (2018)");
        Touch(Path.Combine(dir, "setup_file.exe"), 200);

        var plan = new ImportPlanner(store, root).Plan(root);
        Assert.Equal(0, plan.Ambiguous.Count, "no longer ambiguous -- longest match wins");
        Assert.Equal(1, plan.Matched.Count, "matched to one item");
        Assert.Equal(2L, plan.Matched[0].GogId, "matched the more specific DOOM 3: Phobos");
    }

    /// <summary>(09-17) A name the record carries beats GOG's rounded size: a forgotten drive re-added dropped
    /// 5 of 44 files because a 2.7 GB part is reported as 2.68 GB and tiny extras as "1 MB". The last-held path's
    /// leaf and the CDN name are authoritative, whatever the size says.</summary>
    [Test]
    void Match_by_recorded_name_ignores_the_size_tolerance()
    {
        var (store, root) = NewManifest();
        var it = Item(7, "Vambrace: Cold Soul", "vambrace_cold_soul", ("k1", "Vambrace (Part 2 of 2)", 2_684_354_560L), ("k2", "avatars", 1_048_576L));
        it.Files[0].ResolvedFileName = "setup_vambrace_cold_soul-1.bin";
        it.Files[1].LocalRelativePath = "Games/vambrace_cold_soul/Extras/vambrace_avatars.zip";
        store.Current.Items.Add(it);
        var dir = Path.Combine(root, "vambrace_cold_soul");
        Touch(Path.Combine(dir, "setup_vambrace_cold_soul-1.bin"), 50_000_000);   // 2.6 GB off the rounded figure
        Touch(Path.Combine(dir, "Extras", "vambrace_avatars.zip"), 134_678);       // well under GOG's "1 MB"
        var plan = new ImportPlanner(store, root).Plan(root);
        Assert.Equal(1, plan.Matched.Count, "folder matched by slug");
        Assert.Equal("k1,k2", string.Join(",", plan.Matched[0].Files.Select(f => f.FileKey).OrderBy(k => k)), "both adopted by name");
    }

    /// <summary>(09-17) A game whose only files are extras has no installer at its top level; its folder must
    /// still count as a game folder or the walk's depth cap skips it.</summary>
    [Test]
    void Extras_only_game_folder_is_scanned()
    {
        var (store, root) = NewManifest();
        var it = Item(9, "Planescape: Torment", "planescape_torment", ("k1", "avatars", 1_048_576L));
        it.Files[0].Kind = FileKind.Extra;
        it.Files[0].LocalRelativePath = "Games/planescape_torment/Extras/pst_avatars.zip";
        store.Current.Items.Add(it);
        Touch(Path.Combine(root, "Games", "planescape_torment", "Extras", "pst_avatars.zip"), 472_386);
        var plan = new ImportPlanner(store, root).Plan(root);
        Assert.Equal(1, plan.Matched.Count, "the extras-only folder is a game folder");
        Assert.Equal("k1", plan.Matched[0].Files.Single().FileKey);
    }

    /// <summary>(09-19, walk pass 3) A backup root keeps an empty top-level Extras folder after a separate extras
    /// tree was ever applied. The root then read as ONE game folder ("no library match") and nothing under Games
    /// was scanned: a forgotten drive re-added adopted 0 files.</summary>
    [Test]
    void A_backup_root_with_a_top_level_Extras_folder_is_not_a_game_folder()
    {
        var (store, root) = NewManifest();
        store.Current.Items.Add(Item(3, "Leap of Love", "leap_of_love", ("k1", "setup", 8)));
        Touch(Path.Combine(root, "Games", "leap_of_love", "setup_leap_of_love_2.5.8.exe"), 8);
        Directory.CreateDirectory(Path.Combine(root, "Extras"));   // the leftover

        var plan = new ImportPlanner(store, root).Plan(root);

        Assert.Equal(0, plan.UnmatchedGameFolders.Count, "the root is not offered as an unmatched game");
        Assert.Equal(1, plan.Matched.Count, "the game under Games is found");
    }

    // Sweep 2 #11: import walked into Old Versions/ and .grog-tmp/.
    [Test]
    void Archived_builds_and_partials_are_never_adopted()
    {
        var (store, root) = NewManifest();
        store.Current.Items.Add(Item(11, "Orc Quest", "orc_quest", ("k1", "setup", 1000)));
        // The archive beside Games/: an old build within 1 MB of the current size.
        Touch(Path.Combine(root, "Old Versions", "orc_quest", "setup_orc_quest_1.0.exe"), 990);
        // A partial in the temp folder, and debris inside a game folder.
        Touch(Path.Combine(root, ".grog-tmp", "setup_orc_quest_1.1.exe.part"), 500);
        var plan = new ImportPlanner(store, root).Plan(root);
        Assert.Equal(0, plan.Matched.Count, "neither the archive nor the temp folder is a game folder");

        Touch(Path.Combine(root, "Games", "orc_quest", "setup_orc_quest_1.1.exe"), 1000);
        Touch(Path.Combine(root, "Games", "orc_quest", "Old Versions", "setup_orc_quest_1.0.exe"), 995);
        Touch(Path.Combine(root, "Games", "orc_quest", ".grog-tmp", "x.part"), 1000);
        plan = new ImportPlanner(store, root).Plan(root);
        var m = Assert.Single(plan.Matched, "the real game folder");
        Assert.Equal(1, m.Files.Count, "only the current build");
        Assert.True(m.Files[0].DiskPath.EndsWith("setup_orc_quest_1.1.exe"), m.Files[0].DiskPath);
        Assert.Equal(0, m.UnmatchedFileCount, "internal files are not counted as unmatched either");
    }

    [Test]
    void SlugFromInstallerName_KeepsSequelNumber()
    {
        // The reported bug: sequel numbers were dropped as if they were versions.
        Assert.Equal("syberia_2", ImportPlanner.SlugFromInstallerName("setup_syberia_2_2.0.0.13"), "keeps sequel 2, drops dotted version");
        Assert.Equal("heroes_of_loot_2", ImportPlanner.SlugFromInstallerName("setup_heroes_of_loot_2_1.0.5"), "keeps sequel 2");
        Assert.Equal("baldurs_gate", ImportPlanner.SlugFromInstallerName("setup_baldurs_gate_2.5.0.10"), "no sequel, drops version");
    }

    [Test]
    void NormAlnum_ConvertsRomanNumerals()
    {
        // GOG titles use roman numerals; user folders use arabic. They must normalize to the same key.
        Assert.Equal(ImportPlanner.NormAlnum("Civilization 3"), ImportPlanner.NormAlnum("Civilization III"), "III == 3");
        Assert.Equal(ImportPlanner.NormAlnum("Warcraft 2"), ImportPlanner.NormAlnum("Warcraft II"), "II == 2");
        Assert.Equal(ImportPlanner.NormAlnum("Final Fantasy 15"), ImportPlanner.NormAlnum("Final Fantasy XV"), "XV == 15");
        // Non-canonical / risky forms are left alone.
        Assert.NotEqual(ImportPlanner.NormAlnum("I Am Setsuna"), ImportPlanner.NormAlnum("1 Am Setsuna"), "standalone 'I' not converted");
    }

    [Test]
    void SlugFromInstallerName_StripsSetupAndVersion()
    {
        Assert.Equal("baldurs_gate", ImportPlanner.SlugFromInstallerName("setup_baldurs_gate_2.5.0.10_(64bit)_(43005)"), "setup slug");
        Assert.Equal("tyrian", ImportPlanner.SlugFromInstallerName("gog_tyrian_2.0.0.1"), "gog.sh slug");
        Assert.Null(ImportPlanner.SlugFromInstallerName("readme"), "non-installer -> null");
    }

    [Test]
    void MatchFolderToItem_ManualPick_BuildsMatchedFolder()
    {
        var (store, root) = NewManifest();
        store.Current.Items.Add(Item(1, "Baldur's Gate", "baldurs_gate", ("k1", "Installer", 5000)));
        var dir = Path.Combine(root, "Some Weird Folder Name");
        Touch(Path.Combine(dir, "setup_bg_2.0.exe"), 5000);

        var matched = new ImportPlanner(store, root).MatchFolderToItem(dir, 1);
        Assert.NotNull(matched, "manual pick builds a matched folder");
        Assert.Equal(1L, matched!.GogId, "matched to the chosen item");
        Assert.True(matched.Files.Count >= 1, "at least one file paired by size");
    }

    [Test]
    async System.Threading.Tasks.Task ApplyAsync_StampsRootId_OnAdoptedFiles()
    {
        var (store, root) = NewManifest();
        store.Current.Items.Add(Item(1, "Baldur's Gate", "baldurs_gate", ("k1", "Installer", 5000)));
        var dir = Path.Combine(root, "Baldur's Gate");
        Touch(Path.Combine(dir, "setup_baldurs_gate_2.0.exe"), 5000);

        var planner = new ImportPlanner(store, root, rootId: "R9");
        var plan = planner.Plan(root);
        await planner.ApplyAsync(plan, ImportMode.Adopt);

        var file = store.Current.Items[0].Files[0];
        Assert.Equal(FileState.Present, file.State, "adopted");
        Assert.Equal("R9", file.RootId!, "stamped with the registered root id");
    }

    [Test]
    async System.Threading.Tasks.Task ApplyAsync_AdoptsSharedFileKeysPerGame()
    {
        // GOG serves one extra under two products with the SAME download path (Ultima Underworld I and II both
        // list Ultima_Underworld_1_2_QRC.zip). A manifest-wide FileKey map adopted game 1's entry twice and left
        // game 2 "gone from disk" (owner-hit 09-04). The lookup is per game.
        var (store, root) = NewManifest();
        store.Current.Items.Add(Item(1, "Ultima Underworld I", "ultima_underworld_i", ("/downloads/uw1/setup", "Setup", 50000), ("/downloads/uw/qrc", "QRC", 9000)));
        store.Current.Items.Add(Item(2, "Ultima Underworld II", "ultima_underworld_ii", ("/downloads/uw2/setup", "Setup", 60000), ("/downloads/uw/qrc", "QRC", 9000)));
        Touch(Path.Combine(root, "Ultima Underworld I", "setup_ultima_underworld_i_1.0.exe"), 50000);
        Touch(Path.Combine(root, "Ultima Underworld I", "Ultima_Underworld_1_2_QRC.zip"), 9000);
        Touch(Path.Combine(root, "Ultima Underworld II", "setup_ultima_underworld_ii_1.0.exe"), 60000);
        Touch(Path.Combine(root, "Ultima Underworld II", "Ultima_Underworld_1_2_QRC.zip"), 9000);

        var planner = new ImportPlanner(store, root, rootId: "R1");
        var plan = planner.Plan(root);
        await planner.ApplyAsync(plan, ImportMode.Adopt);

        Assert.Equal(FileState.Present, store.Current.Items[0].Files[1].State, "game 1's shared extra adopted");
        Assert.Equal(FileState.Present, store.Current.Items[1].Files[1].State, "game 2's shared extra adopted too, not left Missing");
        Assert.True(store.Current.Items[1].Files[1].LocalRelativePath!.Contains("Underworld II"), "each points at its own copy");
    }

    // ---- depth-capped walk ----

    [Test]
    void FindCandidateFolders_StopsAtGameFolders_AndRespectsDepthCap()
    {
        var (store, root) = NewManifest();
        try
        {
            // our nested layout: game folder at depth 2, with its own extras subfolder
            Touch(Path.Combine(root, "Games", "mygame", "setup_mygame_1.0.exe"));
            Touch(Path.Combine(root, "Games", "mygame", "extras", "manual.pdf"));   // must NOT be scanned as its own folder
            // a loose game folder at depth 1
            Touch(Path.Combine(root, "Loose Title (1998)", "setup_loose_1.0.exe"));
            // an installer buried too deep (depth 3) -> must be missed by a depth-2 cap
            Touch(Path.Combine(root, "toodeep", "a", "b", "setup_deep_1.0.exe"));

            var planner = new ImportPlanner(store, root);
            var found = planner.FindCandidateFolders(root, maxDepth: 2).Select(Path.GetFileName).ToList();

            Assert.Contains("mygame", string.Join("|", found), "nested game folder found at depth 2");
            Assert.Contains("Loose Title (1998)", string.Join("|", found), "loose game folder found at depth 1");
            Assert.True(!found.Contains("extras"), "did not descend into a game folder's own extras");
            Assert.True(!found.Contains("b"), "did not find installer buried below the depth cap");
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- matching ----

    [Test]
    async Task Plan_MatchesByInstallerSlug()
    {
        var (store, root) = NewManifest();
        try
        {
            await store.LoadAsync();
            store.Current.Items.Add(Item(10, "Baldur's Gate", "baldurs_gate", ("/dl/0", "Setup", 8)));
            await store.SaveAsync();

            var src = Directory.CreateTempSubdirectory("grog-src-").FullName;
            Touch(Path.Combine(src, "Some Weird Folder Name", "setup_baldurs_gate_2.5.exe"));

            var plan = new ImportPlanner(store, root).Plan(src);
            Assert.Equal(1, plan.Matched.Count, "one matched folder");
            Assert.Equal(10, plan.Matched[0].GogId, "matched to the right item by installer slug");
            Assert.Equal("installer slug", plan.Matched[0].Reason, "reason recorded");
            Directory.Delete(src, true);
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    async Task Plan_MatchesByFolderName_WithYearStripped()
    {
        var (store, root) = NewManifest();
        try
        {
            await store.LoadAsync();
            store.Current.Items.Add(Item(20, "Alone in the Dark", "alone_in_the_dark", ("/dl/0", "Setup", 8)));
            await store.SaveAsync();

            var src = Directory.CreateTempSubdirectory("grog-src-").FullName;
            // installer name gives no usable slug, so it must fall back to the folder name
            Touch(Path.Combine(src, "Alone in the Dark (1992)", "installer.exe"));

            var plan = new ImportPlanner(store, root).Plan(src);
            Assert.Equal(1, plan.Matched.Count, "matched by year-stripped folder name");
            Assert.Equal(20, plan.Matched[0].GogId, "right item");
            Directory.Delete(src, true);
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    async Task Plan_AmbiguousWhenTwoItemsShareName()
    {
        var (store, root) = NewManifest();
        try
        {
            await store.LoadAsync();
            store.Current.Items.Add(Item(30, "Twins", "twins"));
            store.Current.Items.Add(Item(31, "Twins", "twins"));
            await store.SaveAsync();

            var src = Directory.CreateTempSubdirectory("grog-src-").FullName;
            Touch(Path.Combine(src, "Twins", "setup_twins_1.0.exe"));

            var plan = new ImportPlanner(store, root).Plan(src);
            Assert.Equal(0, plan.Matched.Count, "not auto-matched");
            Assert.Equal(1, plan.Ambiguous.Count, "reported ambiguous");
            Assert.Equal(2, plan.Ambiguous[0].Candidates.Count, "both candidates listed");
            Directory.Delete(src, true);
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    async Task Plan_UnmatchedGameFolder_WhenNoLibraryMatch()
    {
        var (store, root) = NewManifest();
        try
        {
            await store.LoadAsync();
            store.Current.Items.Add(Item(40, "Owned Game", "owned_game"));
            await store.SaveAsync();

            var src = Directory.CreateTempSubdirectory("grog-src-").FullName;
            Touch(Path.Combine(src, "Not In My Library", "setup_stranger_1.0.exe"));

            var plan = new ImportPlanner(store, root).Plan(src);
            Assert.Equal(0, plan.Matched.Count, "nothing matched");
            Assert.Equal(1, plan.UnmatchedGameFolders.Count, "game-like but unmatched reported");
            Directory.Delete(src, true);
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- file adoption + apply ----

    [Test]
    async Task Apply_Preview_TouchesNothing()
    {
        var (store, root) = NewManifest();
        try
        {
            await store.LoadAsync();
            var game = Item(50, "Preview Game", "preview_game", ("/dl/0", "Setup", 100));
            store.Current.Items.Add(game);
            await store.SaveAsync();

            var src = Directory.CreateTempSubdirectory("grog-src-").FullName;
            Touch(Path.Combine(src, "Preview Game", "setup_preview_game_1.0.exe"), 100);

            var planner = new ImportPlanner(store, root);
            var plan = planner.Plan(src);
            await planner.ApplyAsync(plan, ImportMode.Preview);

            Assert.Equal(FileState.NotBackedUp, game.Files[0].State, "preview left state untouched");
            Assert.Equal(0, plan.AdoptedFiles, "nothing adopted in preview");
            Directory.Delete(src, true);
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    async Task Apply_Adopt_MatchesFileBySize_AndMarksDownloaded()
    {
        var (store, root) = NewManifest();
        try
        {
            await store.LoadAsync();
            var game = Item(60, "Adopt Game", "adopt_game", ("/dl/0", "Setup", 4096));
            store.Current.Items.Add(game);
            await store.SaveAsync();

            // put the game folder UNDER the backup root so the adopted relative path is clean
            var srcDir = Path.Combine(root, "Adopt Game");
            Touch(Path.Combine(srcDir, "setup_adopt_game_1.0.exe"), 4096);

            var planner = new ImportPlanner(store, root);
            var plan = planner.Plan(root);
            Assert.Equal(1, plan.MatchedFileCount, "file matched by size");

            await planner.ApplyAsync(plan, ImportMode.Adopt);
            Assert.Equal(1, plan.AdoptedFiles, "one file adopted");
            Assert.Equal(FileState.Present, game.Files[0].State, "adopted file marked downloaded");
            Assert.True(game.Files[0].LocalRelativePath!.Contains("Adopt Game"), "relative path recorded");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    async Task Plan_FileWithWrongSize_CountsAsUnmatchedWithinMatchedFolder()
    {
        var (store, root) = NewManifest();
        try
        {
            await store.LoadAsync();
            var game = Item(70, "Size Game", "size_game", ("/dl/0", "Setup", 10_000_000));
            store.Current.Items.Add(game);
            await store.SaveAsync();

            var src = Directory.CreateTempSubdirectory("grog-src-").FullName;
            var dir = Path.Combine(src, "Size Game");
            Touch(Path.Combine(dir, "setup_size_game_1.0.exe"), 10_000_000);  // matches
            Touch(Path.Combine(dir, "bonus_readme.txt"), 50);                 // no entry of this size

            var plan = new ImportPlanner(store, root).Plan(src);
            Assert.Equal(1, plan.Matched.Count, "folder matched");
            Assert.Equal(1, plan.Matched[0].Files.Count, "one file adopted by size");
            Assert.Equal(1, plan.Matched[0].UnmatchedFileCount, "the extra file reported unmatched");
            Directory.Delete(src, true);
        }
        finally { Directory.Delete(root, true); }
    }
}

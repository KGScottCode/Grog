// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.IO;
using System.Linq;
using Grog.Core.Auth;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

public class UpdateSurfacingTests
{
    static JsonManifestStore NewStore()
    {
        var root = Directory.CreateTempSubdirectory("grog-upd-").FullName;
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(root, "_cfg"));
        var store = new JsonManifestStore(GrogPaths.Resolve(root));
        store.LoadAsync().GetAwaiter().GetResult();
        return store;
    }

    // --- ChangelogService.CleanHtml ---

    [Test]
    void Changelog_StripsTagsAndDecodesEntities()
    {
        var html = "<p>Fixed a crash &amp; added <b>Polish</b> subtitles.</p>";
        var text = ChangelogService.CleanHtml(html);
        Assert.True(text.Contains("Fixed a crash & added Polish subtitles"), "entities decoded, tags stripped");
        Assert.True(!text.Contains("<"), "no tags remain");
    }

    [Test]
    void Changelog_ListItemsBecomeBullets()
    {
        var html = "<ul><li>One</li><li>Two</li></ul>";
        var text = ChangelogService.CleanHtml(html);
        Assert.True(text.Contains("• One"), "first bullet");
        Assert.True(text.Contains("• Two"), "second bullet");
    }

    [Test]
    void Changelog_EmptyInputYieldsEmpty()
    {
        Assert.True(ChangelogService.CleanHtml(null) == "", "null → empty");
        Assert.True(ChangelogService.CleanHtml("   ") == "", "whitespace → empty");
    }

    // --- ChangesService ---

    [Test]
    void Changes_SeparatesUpdatesFromIncomplete()
    {
        var store = NewStore();
        var updated = new LibraryItem { GogId = 1, Title = "Outdated", Status = BackupStatus.Outdated };
        var partial = new LibraryItem { GogId = 2, Title = "Half", Status = BackupStatus.Partial };
        var complete = new LibraryItem { GogId = 3, Title = "Done", Status = BackupStatus.Complete };
        var missing = new LibraryItem { GogId = 4, Title = "None", Status = BackupStatus.NotBackedUp };
        store.Current.Items.AddRange(new[] { updated, partial, complete, missing });

        var changes = new ChangesService(store);
        var ups = changes.UpdatesAvailable();
        var inc = changes.MissingOrIncomplete();

        Assert.True(ups.Count == 1 && ups[0].GogId == 1, "only the outdated game is an update");
        Assert.True(inc.Count == 2, "partial + missing are incomplete");
        Assert.True(!inc.Any(r => r.GogId == 3), "complete game is not incomplete");
    }

    [Test]
    void Changes_FileLevelUpdateFlagsGame()
    {
        var store = NewStore();
        var g = new LibraryItem { GogId = 5, Title = "FileUpd", Status = BackupStatus.Complete };
        g.Files.Add(new GameFile { State = FileState.Outdated });
        store.Current.Items.Add(g);

        var ups = new ChangesService(store).UpdatesAvailable();
        Assert.True(ups.Any(r => r.GogId == 5), "a file-level UpdateAvailable surfaces the game even if rollup says Complete");
    }

    [Test]
    void Changes_OneLineSummaryCountsAll()
    {
        var store = NewStore();
        store.Current.Items.Add(new LibraryItem { GogId = 1, Title = "U", Status = BackupStatus.Outdated });
        store.Current.Items.Add(new LibraryItem { GogId = 2, Title = "M", Status = BackupStatus.NotBackedUp });
        store.Current.Items.Add(new LibraryItem { GogId = 3, Title = "D", Status = BackupStatus.Complete, IsDelisted = true });

        var summary = new ChangesService(store).OneLineSummary();
        Assert.True(summary.Contains("1 with updates"), "update count");
        Assert.True(summary.Contains("1 incomplete"), "incomplete count");
        Assert.True(summary.Contains("1 delisted"), "delisted count");
    }

    // --- AccountService ---

    [Test]
    void Account_AddTagsAndTokenPathIsPerAccount()
    {
        var root = Directory.CreateTempSubdirectory("grog-acct-").FullName;
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(root, "_cfg"));
        var paths = GrogPaths.Resolve(root);
        var store = new JsonManifestStore(paths);
        store.LoadAsync().GetAwaiter().GetResult();
        var svc = new AccountService(store, paths);

        // default account → legacy tokens.json
        Assert.Contains("tokens-kevin.json", svc.TokenPathFor("kevin"), "every account gets its own token file");
        try { svc.TokenPathFor(""); Assert.True(false, "empty id must throw"); }
        catch (System.ArgumentException) { /* the unnamed slot no longer exists */ }
        // named account → tokens-<id>.json
        var named = svc.TokenPathFor("kids");
        Assert.True(named.EndsWith("tokens-kids.json"), "named account gets its own token file");

        var acct = svc.AddOrUpdateAsync("main", "MainUser", "main@example.com").GetAwaiter().GetResult();
        Assert.True(acct.Id == "main" && svc.Accounts.Count == 1, "account recorded");
    }

    [Test]
    void Account_RemovePreservesItems()
    {
        var root = Directory.CreateTempSubdirectory("grog-acct2-").FullName;
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(root, "_cfg"));
        var paths = GrogPaths.Resolve(root);
        var store = new JsonManifestStore(paths);
        store.LoadAsync().GetAwaiter().GetResult();
        var svc = new AccountService(store, paths);

        svc.AddOrUpdateAsync("main", "U", "u@e.com").GetAwaiter().GetResult();
        store.Current.Items.Add(new LibraryItem { GogId = 1, Title = "Owned", AccountId = "main" });

        var removed = svc.RemoveAsync("main").GetAwaiter().GetResult();
        Assert.True(removed, "account removed");
        Assert.True(svc.Accounts.Count == 0, "no accounts left");
        Assert.True(store.Current.Items.Count == 1, "already-backed-up item is preserved, not deleted");
    }

    [Test]
    void Account_ItemCountsByAccount()
    {
        var root = Directory.CreateTempSubdirectory("grog-acct3-").FullName;
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(root, "_cfg"));
        var paths = GrogPaths.Resolve(root);
        var store = new JsonManifestStore(paths);
        store.LoadAsync().GetAwaiter().GetResult();
        var svc = new AccountService(store, paths);

        store.Current.Items.Add(new LibraryItem { GogId = 1, Title = "A", AccountId = "main" });
        store.Current.Items.Add(new LibraryItem { GogId = 2, Title = "B", AccountId = "main" });
        store.Current.Items.Add(new LibraryItem { GogId = 3, Title = "C", AccountId = "" });

        var counts = svc.ItemCountsByAccount();
        Assert.True(counts["main"] == 2, "two items for main");
        Assert.True(counts[""] == 1, "one default item");
    }
}

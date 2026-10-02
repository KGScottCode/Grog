// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using Grog.Core.Auth;
using Grog.Core.Manifest;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;

/// <summary>
/// Account registration. The bug these exist for: Grog has TWO paths that register the signed-in user, and
/// they keyed the same person differently -- an id-less registration derives its id from the username
/// tokens.json), while Add-account stores Id = the GOG username. Doing both put the same account on the
/// Accounts page twice.
/// </summary>
[NewBatch]
[Trait("accounts")]
public class AccountServiceTests
{
    private string _root = "";

    private (AccountService Svc, JsonManifestStore Store) NewSetup()
    {
        _root = Directory.CreateTempSubdirectory("grog-acct-").FullName;
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(_root, "_cfg"));
        var paths = GrogPaths.Resolve(_root);
        var store = new JsonManifestStore(paths);
        store.LoadAsync().GetAwaiter().GetResult();
        return (new AccountService(store, paths), store);
    }

    [Teardown]
    public void Teardown() { try { Directory.Delete(_root, true); } catch { } }

    [Test]
    async Task Migration_upgrades_the_unnamed_slot_to_an_ordinary_account()
    {
        // The pre-multi-account layout: one account with no id, owning a bare tokens.json.
        var (svc, store) = NewSetup();
        var paths = GrogPaths.Resolve(_root);
        store.Current.Accounts.Add(new Grog.Core.Models.GrogAccount { Id = "", Username = "GogUser", Login = "goguser" });
        store.Current.Items.Add(new Grog.Core.Models.LibraryItem { GogId = 1, Title = "A", AccountId = "" });
        store.Current.Items[0].Files.Add(new Grog.Core.Models.GameFile { GameGogId = 1, FileKey = "f", OwnerIds = { "" } });
        Directory.CreateDirectory(Path.GetDirectoryName(paths.TokensPath)!);
        File.WriteAllText(paths.TokensPath, "{\"who\":\"legacy\"}");

        Assert.True(await svc.MigrateLegacyPrimaryAsync(), "a legacy slot was migrated");

        var acct = store.Current.Accounts[0];
        Assert.Equal("goguser", acct.Id, "id derives from the username, lowercased");
        Assert.True(File.Exists(svc.TokenPathFor("goguser")), "token renamed to the ordinary scheme");
        Assert.Equal("{\"who\":\"legacy\"}", File.ReadAllText(svc.TokenPathFor("goguser")), "token content intact");
        Assert.False(File.Exists(paths.TokensPath), "the bare tokens.json is gone");
        Assert.Equal("goguser", store.Current.Items[0].AccountId, "item attribution rewritten");
        Assert.Equal("goguser", store.Current.Items[0].Files[0].OwnerIds[0], "file ownership rewritten");
    }

    [Test]
    async Task Healing_reaches_DLC_children_not_just_top_level_items()
    {
        // Seen live: seven "" attributions survived the sweep because every one was a DLC --
        // Dlcs holds nested items with the same fields, and the first sweep only walked the top level.
        var (svc, store) = NewSetup();
        await svc.AddOrUpdateAsync("kevin", "kevin", "kevin");
        var game = new Grog.Core.Models.LibraryItem { GogId = 1, Title = "Base", AccountId = "kevin" };
        var dlc = new Grog.Core.Models.LibraryItem { GogId = 2, Title = "DLC", AccountId = "" };
        dlc.Files.Add(new Grog.Core.Models.GameFile { GameGogId = 2, FileKey = "d", OwnerIds = { "" } });
        game.Dlcs.Add(dlc);
        store.Current.Items.Add(game);

        Assert.False(await svc.MigrateLegacyPrimaryAsync(), "already migrated");
        Assert.Equal("kevin", dlc.AccountId, "the DLC's attribution heals too");
        Assert.Equal("kevin", dlc.Files[0].OwnerIds[0], "and its file ownership");
    }

    [Test]
    async Task Migration_is_a_no_op_on_an_already_migrated_manifest()
    {
        var (svc, store) = NewSetup();
        await svc.AddOrUpdateAsync("kevin", "kevin", "kevin");
        Assert.False(await svc.MigrateLegacyPrimaryAsync(), "nothing to migrate");
        Assert.Equal("kevin", store.Current.Accounts[0].Id, "and nothing changed");
    }

    [Test]
    async Task Migration_finishes_a_previously_interrupted_run()
    {
        // Crash window: the token was copied and the manifest committed, but the delete never ran.
        var (svc, store) = NewSetup();
        var paths = GrogPaths.Resolve(_root);
        await svc.AddOrUpdateAsync("kevin", "kevin", "kevin");
        Directory.CreateDirectory(Path.GetDirectoryName(paths.TokensPath)!);
        File.WriteAllText(svc.TokenPathFor("kevin"), "{}");
        File.WriteAllText(paths.TokensPath, "{}");   // the stale original

        Assert.False(await svc.MigrateLegacyPrimaryAsync(), "no unnamed slot left to migrate");
        Assert.False(File.Exists(paths.TokensPath), "but the stale credential copy is cleaned up");
        Assert.True(File.Exists(svc.TokenPathFor("kevin")), "and the live token is untouched");
    }

    [Test]
    async Task Concurrent_primary_registration_still_yields_ONE_account()
    {
        // THE ACTUAL BUG this pins. Three call sites register the primary, each guarding on
        // "no accounts yet", and every one of them awaits between the check and the add. Two overlapping
        // logins both read zero and both append. Reproduced by racing the registrations directly.
        var (svc, store) = NewSetup();

        await Task.WhenAll(
            svc.AddOrUpdateAsync("", "goguser", "goguser"),
            svc.AddOrUpdateAsync("", "goguser", "goguser"),
            svc.AddOrUpdateAsync("", "goguser", "goguser"));

        Assert.Equal(1, store.Current.Accounts.Count, "a race must not produce a second row");
    }

    [Test]
    async Task Dedupe_heals_a_manifest_that_already_has_duplicates()
    {
        // Preventing new duplicates does nothing for the manifests already carrying them.
        var (svc, store) = NewSetup();
        store.Current.Accounts.Add(new Grog.Core.Models.GrogAccount { Id = "goguser", Username = "goguser", Login = "goguser" });
        store.Current.Accounts.Add(new Grog.Core.Models.GrogAccount { Id = "goguser1", Username = "GogUser", Login = "goguser" });
        store.Current.Accounts.Add(new Grog.Core.Models.GrogAccount { Id = "kids", Username = "kidsaccount", Login = "kidsaccount" });
        // Only the second duplicate has a token file, so it is the login the app can still use.
        var live = svc.TokenPathFor("goguser1");
        Directory.CreateDirectory(Path.GetDirectoryName(live)!);
        File.WriteAllText(live, "{}");

        var removed = await svc.DedupeAsync();

        Assert.Equal(1, removed, "one duplicate collapsed");
        Assert.Equal(2, store.Current.Accounts.Count, "the unrelated second account is untouched");
        // The survivor must be the row whose token file exists, or the app signs in as a file it deleted.
        var kept = store.Current.Accounts.First(a => string.Equals(a.Username, "goguser", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("goguser1", kept.Id, "the row with the live token survives");
    }

    [Test]
    async Task Dedupe_is_a_no_op_on_a_clean_manifest()
    {
        var (svc, store) = NewSetup();
        await svc.AddOrUpdateAsync("", "goguser", "goguser");
        await svc.AddOrUpdateAsync("kids", "kidsaccount", "kidsaccount");

        Assert.Equal(0, await svc.DedupeAsync(), "nothing to collapse");
        Assert.Equal(2, store.Current.Accounts.Count, "and nothing removed");
    }

    [Test]
    async Task Same_user_through_both_paths_is_ONE_account()
    {
        var (svc, store) = NewSetup();

        // 1. First sign-in arrives with no id; the id derives from the username.
        await svc.AddOrUpdateAsync("", "goguser", "goguser");
        // 2. "Add account" as the same person registers with id = username explicitly.
        await svc.AddOrUpdateAsync("goguser", "goguser", "goguser");

        Assert.Equal(1, store.Current.Accounts.Count, "one human, one row");
        Assert.Equal("goguser", store.Current.Accounts[0].Username, "and it keeps the username");
    }

    [Test]
    async Task Matching_is_case_insensitive()
    {
        var (svc, store) = NewSetup();
        await svc.AddOrUpdateAsync("", "GogUser", "GogUser");
        await svc.AddOrUpdateAsync("goguser", "goguser", "goguser");
        Assert.Equal(1, store.Current.Accounts.Count, "GOG casing must not split one account in two");
    }

    [Test]
    async Task Different_users_stay_separate()
    {
        // The whole point of multi-account: two real people must NOT collapse into one row.
        var (svc, store) = NewSetup();
        await svc.AddOrUpdateAsync("", "goguser", "goguser");
        await svc.AddOrUpdateAsync("kids", "kidsaccount", "kidsaccount");
        Assert.Equal(2, store.Current.Accounts.Count, "two people, two rows");
    }

    [Test]
    async Task LogOut_forgets_the_token_but_keeps_the_registration()
    {
        // THE SPLIT. "Log Out" on a secondary used to route into Remove, so signing out silently
        // unregistered the account. Log Out may touch ONLY the token file.
        var (svc, store) = NewSetup();
        await svc.AddOrUpdateAsync("kids", "kidsaccount", "kidsaccount");
        var tokenPath = svc.TokenPathFor("kids");
        Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);
        File.WriteAllText(tokenPath, "{}");

        Assert.True(svc.LogOut("kids"), "a stored token was forgotten");
        Assert.False(File.Exists(tokenPath), "the token file is gone");
        Assert.Equal(1, store.Current.Accounts.Count, "the registration stays");
    }

    [Test]
    async Task LogOut_with_no_token_is_a_harmless_no_op()
    {
        var (svc, store) = NewSetup();
        await svc.AddOrUpdateAsync("kids", "kidsaccount", "kidsaccount");
        Assert.False(svc.LogOut("kids"), "nothing to forget");
        Assert.Equal(1, store.Current.Accounts.Count, "and nothing removed");
    }

    [Test]
    async Task Remove_unregisters_and_wipes_the_token_but_items_stay()
    {
        // The never-delete rule: Remove wipes CREDENTIALS and the registration; the synced items
        // (and the bytes they point at) are owned backups and must survive.
        var (svc, store) = NewSetup();
        await svc.AddOrUpdateAsync("kids", "kidsaccount", "kidsaccount");
        var tokenPath = svc.TokenPathFor("kids");
        Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);
        File.WriteAllText(tokenPath, "{}");
        store.Current.Items.Add(new Grog.Core.Models.LibraryItem { GogId = 1, Title = "A Game", AccountId = "kids" });

        Assert.True(await svc.RemoveAsync("kids"), "the account was removed");
        Assert.Equal(0, store.Current.Accounts.Count, "registration gone");
        Assert.False(File.Exists(tokenPath), "credentials wiped");
        Assert.Equal(1, store.Current.Items.Count, "its items stay");
    }

    [Test]
    async Task Re_registering_updates_in_place()
    {
        var (svc, store) = NewSetup();
        await svc.AddOrUpdateAsync("main", "oldname", "old@example.com");
        await svc.AddOrUpdateAsync("main", "newname", "new@example.com");
        Assert.Equal(1, store.Current.Accounts.Count, "same id -> same row");
        Assert.Equal("newname", store.Current.Accounts[0].Username, "display fields refresh");
    }

    [Test]
    async Task Removing_the_first_account_promotes_nobody()
    {
        // No account is special: the per-account session engine runs every account on its own token
        // file, so removing the FIRST-registered account must not rebase the survivor or move files.
        var (svc, store) = NewSetup();
        await svc.AddOrUpdateAsync("", "goguser", "goguser");
        await svc.AddOrUpdateAsync("kids", "kidsaccount", "kidsaccount");
        var primaryPath = svc.TokenPathFor("goguser");
        var kidsPath = svc.TokenPathFor("kids");
        Directory.CreateDirectory(Path.GetDirectoryName(primaryPath)!);
        File.WriteAllText(primaryPath, "{\"who\":\"primary\"}");
        File.WriteAllText(kidsPath, "{\"who\":\"kids\"}");

        Assert.True(await svc.RemoveAsync("goguser"), "the first account was removed");
        Assert.Equal(1, store.Current.Accounts.Count, "one account remains");
        Assert.Equal("kids", store.Current.Accounts[0].Id, "the survivor keeps its own id");
        Assert.False(File.Exists(primaryPath), "its token wiped, not handed to anyone");
        Assert.True(File.Exists(kidsPath), "the survivor's token file stays where it was");
        Assert.Equal("{\"who\":\"kids\"}", File.ReadAllText(kidsPath), "untouched");
    }

    [Test]
    async Task Removing_the_only_account_leaves_a_clean_empty_state()
    {
        var (svc, store) = NewSetup();
        await svc.AddOrUpdateAsync("", "goguser", "goguser");
        var primaryPath = svc.TokenPathFor("goguser");
        Directory.CreateDirectory(Path.GetDirectoryName(primaryPath)!);
        File.WriteAllText(primaryPath, "{}");

        Assert.True(await svc.RemoveAsync("goguser"), "removed");
        Assert.Equal(0, store.Current.Accounts.Count, "no accounts");
        Assert.False(File.Exists(primaryPath), "no stray token file");
    }

    [Test]
    async Task Ids_are_lowercased_so_token_filenames_agree_across_filesystems()
    {
        // tokens-the owner.json and tokens-kevin.json are ONE file on Windows and TWO on Linux, and the
        // config travels between them. One identity must be one file everywhere.
        var (svc, store) = NewSetup();
        await svc.AddOrUpdateAsync("Kevin", "SomeUser", "SomeUser");
        Assert.Equal("kevin", store.Current.Accounts[0].Id, "the id is stored lowercase");
        Assert.Equal(svc.TokenPathFor("Kevin"), svc.TokenPathFor("kevin"),
            "any casing of the id resolves to the same token file");
    }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Grog.Core.Auth;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Download;
using Grog.Core.Storage;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

/// <summary>
/// The per-account session engine (multi-account step 4). Locked down here: the download picker
/// chooses the FIRST authenticated owner and skips signed-out or auth-failed accounts; the sync
/// runner skips signed-out accounts with a reason instead of popping a login or dying.
/// </summary>
[NewBatch]
[Trait("accounts")]
public sealed class AccountSessionsTests
{
    private string _root = "";

    private (AccountSessions Sessions, JsonManifestStore Store, GrogPaths Paths) Setup()
    {
        _root = Directory.CreateTempSubdirectory("grog-sess-").FullName;
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(_root, "_cfg"));
        var paths = GrogPaths.Resolve(_root);
        var store = new JsonManifestStore(paths);
        store.LoadAsync().GetAwaiter().GetResult();
        store.Current.Accounts.Add(new GrogAccount { Id = "kevin", Username = "kevin" });
        store.Current.Accounts.Add(new GrogAccount { Id = "kids", Username = "kidsaccount" });
        return (new AccountSessions(store, paths, new HttpClient()), store, paths);
    }

    [Teardown]
    public void Teardown() { try { Directory.Delete(_root, true); } catch { } }

    private static GameFile File(params string[] owners)
        => new() { FileKey = "setup.exe", OwnerIds = owners.ToList() };

    private void SignIn(AccountSessions s, string id)
    {
        var p = s.TokenPathFor(id);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, "{}");
    }

    [Test]
    public void Picker_chooses_the_first_owner_in_registration_order()
    {
        var (s, _, _) = Setup();
        SignIn(s, "kevin"); SignIn(s, "kids");
        Assert.Equal("kevin", s.FirstAuthenticatedOwner(File("kevin", "kids")), "first-registered owner serves when both can");
    }

    [Test]
    public void Picker_falls_through_to_the_second_owner_when_the_first_is_signed_out()
    {
        // The resilience the owners list exists for: kevin signed out, kids still feeds the backup.
        var (s, _, _) = Setup();
        SignIn(s, "kids");
        Assert.Equal("kids", s.FirstAuthenticatedOwner(File("kevin", "kids")), "the signed-in co-owner serves");
    }

    [Test]
    public void Picker_returns_null_when_every_owner_is_signed_out_and_names_them()
    {
        var (s, _, _) = Setup();
        Assert.True(s.FirstAuthenticatedOwner(File("kevin", "kids")) is null, "no key to the door today");
        Assert.Equal("kevin, kidsaccount", s.OwnerNames(File("kevin", "kids")), "the log line can name them");
    }

    [Test]
    public void Picker_skips_an_account_whose_auth_failed_this_run_until_marked_ok()
    {
        var (s, _, _) = Setup();
        SignIn(s, "kevin"); SignIn(s, "kids");
        s.MarkAuthFailed("kevin");
        Assert.Equal("kids", s.FirstAuthenticatedOwner(File("kevin", "kids")), "a lapsed session stops serving");
        s.MarkAuthOk("kevin");
        Assert.Equal("kevin", s.FirstAuthenticatedOwner(File("kevin", "kids")), "and serves again after re-login");
    }

    [Test]
    public void Picker_never_picks_a_non_owner_however_connected()
    {
        var (s, _, _) = Setup();
        SignIn(s, "kevin"); SignIn(s, "kids");
        Assert.Equal("kids", s.FirstAuthenticatedOwner(File("kids")), "kids-only file needs kids");
    }

    [Test]
    public void Adopt_hands_back_the_given_pair_for_that_account_only()
    {
        // The App's interactive session is bound to the first account's token file; reusing it here means one
        // GogAuthService per token file, so two instances never race on a rotated refresh token.
        var (s, _, _) = Setup();
        var auth = new GogAuthService(s.Http, new FileTokenStore(s.TokenPathFor("kevin")), new NonInteractiveLoginProvider());
        var api = new Grog.Core.Api.GogApiClient(s.Http, auth);
        var before = s.ApiFor("kevin");
        s.Adopt("kevin", auth, api);
        Assert.True(ReferenceEquals(auth, s.AuthFor("kevin")), "the adopted auth replaces the built chain");
        Assert.True(ReferenceEquals(api, s.ApiFor("kevin")), "and the adopted api");
        Assert.False(ReferenceEquals(before, s.ApiFor("kevin")), "the previously built chain is dropped");
        Assert.False(ReferenceEquals(auth, s.AuthFor("kids")), "other accounts keep their own chain");
        s.Invalidate("kevin");
        Assert.False(ReferenceEquals(auth, s.AuthFor("kevin")), "Invalidate rebuilds from disk as before");
    }

    [Test]
    public async Task Runner_skips_signed_out_accounts_with_a_reason_and_touches_no_network()
    {
        var (s, store, _) = Setup();   // no token files at all
        var log = new System.Collections.Generic.List<string>();
        var runner = new MultiAccountSync(s, store) { Log = log.Add };

        var passes = await runner.RunAsync();

        Assert.Equal(2, passes.Count, "every account gets a pass record");
        Assert.True(passes.All(p => p.Skipped && p.SkipReason == "signed out"), "both skipped, reason recorded");
        Assert.True(log.Any(l => l.Contains("kidsaccount")), "the skip line names the account");
    }

    [Test]
    public async Task Engine_skips_a_file_whose_owners_are_all_signed_out_without_a_strike_or_network()
    {
        // "No key to the door today": no owner can serve, so the engine must not attempt, not fail,
        // not count a retry strike -- Skipped with a reason naming the accounts, file left untouched.
        var (s, _, _) = Setup();   // no token files
        var file = File("kevin", "kids");
        var engine = new Grog.Core.Download.DownloadEngine(null!, null!) { Sessions = s, MaxConcurrent = 1 };
        var task = new DownloadTask { File = file, GameTitle = "Doom" };
        engine.Enqueue(task);

        await engine.RunToCompletionAsync();

        Assert.Equal(DownloadTaskState.Skipped, task.State, "skipped, not failed");
        Assert.True(task.SkipReason!.Contains("kevin") && task.SkipReason.Contains("kidsaccount"),
            "the reason names the signed-out owners");
        Assert.True(task.Error is null && task.UnavailableReason is null, "no error, no refusal recorded");
        Assert.Equal(0, file.FailedAttempts, "a skip is not a strike");
    }

    [Test]
    public async Task Engine_skip_leaves_other_queued_files_running()
    {
        // One unservable file must not poison the run: the queue drains and each task settles alone.
        var (s, _, _) = Setup();
        var engine = new Grog.Core.Download.DownloadEngine(null!, null!) { Sessions = s, MaxConcurrent = 1 };
        var a = new DownloadTask { File = File("kevin", "kids"), GameTitle = "A" };
        var bFile = File("kids"); bFile.GameGogId = 2;   // a different file: the engine keeps one live task per (game, key)
        var b = new DownloadTask { File = bFile, GameTitle = "B" };
        engine.EnqueueRange(new[] { a, b });

        await engine.RunToCompletionAsync();

        Assert.Equal(DownloadTaskState.Skipped, a.State, "first file skipped");
        Assert.Equal(DownloadTaskState.Skipped, b.State, "second file still processed (skipped on its own merits)");
    }
}

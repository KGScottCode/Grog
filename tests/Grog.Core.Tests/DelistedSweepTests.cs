// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Grog.Core.Auth;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

/// <summary>
/// The delisted sweep after the multi-account change. The red-grid bug: the add-account merge swept
/// with ONE account's seen ids and flagged the rest of the shared catalog "delisted". Locked down:
/// the sweep is one shared pass over the UNION of every scanned account's ids, single-account merges
/// suppress it entirely, and a run that skipped any account sweeps nothing (partial knowledge).
/// </summary>
[NewBatch]
[Trait("accounts")]
public sealed class DelistedSweepTests
{
    private static LibraryManifest Manifest(params LibraryItem[] items)
    {
        var m = new LibraryManifest();
        m.Items.AddRange(items);
        return m;
    }

    [Test]
    public void Sweep_flags_only_what_no_account_saw_and_clears_what_reappeared()
    {
        var kept = new LibraryItem { GogId = 1, Title = "Primary's game" };
        var reappeared = new LibraryItem { GogId = 2, Title = "Was flagged", IsDelisted = true };
        var gone = new LibraryItem { GogId = 3, Title = "Truly delisted" };

        int n = LibrarySyncService.ApplyDelistedSweep(Manifest(kept, reappeared, gone), new long[] { 1, 2 });

        Assert.Equal(1, n, "one newly flagged");
        Assert.True(!kept.IsDelisted, "seen game untouched");
        Assert.True(!reappeared.IsDelisted, "a seen game loses its stale flag");
        Assert.True(gone.IsDelisted, "unseen by every account = flagged, never deleted");
    }

    [Test]
    public void Sweep_never_flags_games_owned_only_by_a_removed_account_and_heals_stale_flags()
    {
        // Owner-hit 2026-08-30: removing an account painted its 15 games "delisted by GOG" - false.
        // A game no registered account can ask about is UNANSWERABLE: never flagged, stale flag withdrawn.
        var m = new LibraryManifest();
        m.Accounts.Add(new GrogAccount { Id = "goguser", Username = "GogUser" });

        var orphan = new LibraryItem { GogId = 1, Title = "Removed account's game" };
        orphan.Files.Add(new GameFile { FileKey = "a", OwnerIds = { "oldacct" } });
        var stale = new LibraryItem { GogId = 2, Title = "Falsely flagged earlier", IsDelisted = true };
        stale.Files.Add(new GameFile { FileKey = "b", OwnerIds = { "oldacct" } });
        var mine = new LibraryItem { GogId = 3, Title = "My delisted game" };
        mine.Files.Add(new GameFile { FileKey = "c", OwnerIds = { "goguser" } });
        m.Items.AddRange(new[] { orphan, stale, mine });

        int n = LibrarySyncService.ApplyDelistedSweep(m, new long[] { });

        Assert.Equal(1, n, "only the answerable unseen game is flagged");
        Assert.True(!orphan.IsDelisted, "removed-account game never flagged");
        Assert.True(!stale.IsDelisted, "stale false flag withdrawn");
        Assert.True(mine.IsDelisted, "a registered account's unseen game still flags");
    }

    [Test]
    public void Sweep_leaves_corrupt_items_alone()
    {
        var corrupt = new LibraryItem { GogId = 4, Title = "Corrupt", Status = BackupStatus.Corrupt };
        LibrarySyncService.ApplyDelistedSweep(Manifest(corrupt), new long[] { });
        Assert.True(!corrupt.IsDelisted, "corrupt red already means something else");
    }

    [Test]
    public void Narrowing_removes_a_scanned_account_from_games_its_scan_never_saw()
    {
        // The stale-attribution repair: the add-merge bug tagged the primary onto games it never owned,
        // and per-file seen-but-not-offered can never fix a game the account's scans do not mention.
        var wrong = new LibraryItem { GogId = 1, Title = "KG's game" };
        wrong.Files.Add(new GameFile { FileKey = "a", OwnerIds = { "", "kg" } });
        var kept = new LibraryItem { GogId = 2, Title = "Primary's game" };
        kept.Files.Add(new GameFile { FileKey = "b", OwnerIds = { "" } });

        int n = MultiAccountSync.NarrowOwnersForScannedAccount(Manifest(wrong, kept), "", new long[] { 2 });

        Assert.Equal(1, n, "one file lost the stale owner");
        Assert.Equal("kg", wrong.Files[0].OwnerIds.Single(), "the real owner stays");
        Assert.Equal("", kept.Files[0].OwnerIds.Single(), "a seen game keeps its owner");
    }

    [Test]
    public async Task Runner_sweeps_nothing_when_any_account_was_skipped()
    {
        // A run that could not scan every account has NOT seen the whole library; flagging on that
        // partial knowledge is exactly the red-grid bug. Both accounts signed out here: no sweep at all.
        var root = Directory.CreateTempSubdirectory("grog-sweep-").FullName;
        try
        {
            System.Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(root, "_cfg"));
            var paths = GrogPaths.Resolve(root);
            var store = new JsonManifestStore(paths);
            await store.LoadAsync();
            store.Current.Accounts.Add(new GrogAccount { Id = "", Username = "kevin" });
            store.Current.Accounts.Add(new GrogAccount { Id = "kids", Username = "kidsaccount" });
            store.Current.Items.Add(new LibraryItem { GogId = 1, Title = "Unseen but unswept" });
            store.Current.Items.Add(new LibraryItem { GogId = 2, Title = "Stays flagged", IsDelisted = true });

            var sessions = new AccountSessions(store, paths, new HttpClient());
            var passes = await new MultiAccountSync(sessions, store).RunAsync();

            Assert.True(passes.All(p => p.Skipped), "both accounts signed out");
            var (byAccount, complete) = await new MultiAccountSync(sessions, store).ListCloudContainersByAccountAsync();
            Assert.True(!complete && byAccount.Count == 0, "cloud check reports incomplete with no network touched");
            Assert.True(!store.Current.Items[0].IsDelisted, "no flag from a run that scanned nothing");
            Assert.True(store.Current.Items[1].IsDelisted, "and no unflag either -- the sweep simply did not run");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

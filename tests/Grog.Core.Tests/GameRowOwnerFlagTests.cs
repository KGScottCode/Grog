// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.App.ViewModels;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;

/// <summary>
/// The Library's ORANGE owner-disconnected flag (owner rules 08-31): flag a game only when it is owned
/// solely by accounts no longer registered AND it still has a gap (some in-scope file with no local copy).
/// A fully-backed-up game stays plain -- nothing needs re-fetching, so there is no risk to point out.
/// Removing the LAST account still flags (an empty registered list is knowledge, not ignorance);
/// a null list is ignorance (harness/tests) and never flags.
/// </summary>
[NewBatch]
[Trait("ownerflag")]
public class GameRowOwnerFlagTests
{
    private static GameFile File(FileState state, string key, string owner)
        => new() { Kind = FileKind.Installer, Name = key, FileKey = key, State = state,
                   ExpectedSizeBytes = 100, OwnerIds = { owner } };

    private static LibraryItem Game(params GameFile[] files)
    {
        var item = new LibraryItem { GogId = 1, Title = "Test Game", Slug = "test-game" };
        item.Files.AddRange(files);
        return item;
    }

    // Sweep 2 #8: Status was set once in FromItem and never re-derived while a run changed the files.
    [Test] void StatusIsRederivedWhenAFileLandsMidRun()
    {
        var a = File(FileState.Present, "a", "me"); var b = File(FileState.NotBackedUp, "b", "me");
        var row = GameRowViewModel.FromItem(Game(a, b), registeredAccounts: null);
        Assert.Equal(BackupStatus.Partial, row.Status, "one of two");
        b.State = FileState.Verified;   // the engine settles the last file
        row.RaiseCompletion();          // the tick every settle raises
        Assert.Equal(BackupStatus.Complete, row.Status, "the row follows the files, not its build-time snapshot");
        Assert.Equal("Backed Up", row.StatusText);
    }

    // Sweep 2 #13: the fraction counted a file GOG refuses to serve; Status did not.
    [Test] void UnavailableFilesAreInNeitherTheFractionNorTheStatus()
    {
        var row = GameRowViewModel.FromItem(
            Game(File(FileState.Verified, "a", "me"), File(FileState.Unavailable, "b", "me")), registeredAccounts: null);
        Assert.Equal(BackupStatus.Complete, row.Status, "nothing the user can fetch is missing");
        Assert.Equal(1, row.FilesInScope, "the refused file is not in the denominator");
        Assert.Equal(1, row.FilesPresent);
        Assert.Equal(1, row.FileCount, "the Files column agrees");
        Assert.Equal(100.0, row.CompletionPercent, "and so does the bar");
    }

    // Sweep 2 #16: HasUpdate looked at every file, in scope or not.
    [Test] void AnOutdatedFileOutOfScopeIsNotAnUpdate()
    {
        var extra = File(FileState.Outdated, "x", "me"); extra.Kind = FileKind.Extra;
        var game = Game(File(FileState.Verified, "a", "me"), extra);
        var gamesOnly = Grog.Core.Sync.Scope.FromKey("games");
        Assert.False(GameRowViewModel.FromItem(game, scope: gamesOnly, registeredAccounts: null).HasUpdate, "extras are out of scope");
        Assert.True(GameRowViewModel.FromItem(game, registeredAccounts: null).HasUpdate, "in scope, it is an update");
    }

    [Test] void GapPlusUnregisteredOwnerFlags()
    {
        var row = GameRowViewModel.FromItem(Game(File(FileState.NotBackedUp, "a", "gone")),
            registeredAccounts: new[] { "kept" });
        Assert.True(row.OwnerDisconnected, "gap + unregistered owner is the flagged state");
        Assert.Equal("(account removed)", row.OwnerCaption, "caption names why the row is orange");
        Assert.Equal("gone", row.DisconnectedOwners, "tooltip names the account");
    }

    [Test] void FullyBackedUpStaysPlain()
    {
        var row = GameRowViewModel.FromItem(
            Game(File(FileState.Present, "a", "gone"), File(FileState.Verified, "b", "gone")),
            registeredAccounts: new[] { "kept" });
        Assert.False(row.OwnerDisconnected, "no gap means nothing needs re-fetching -- no flag");
        Assert.Equal("", row.OwnerCaption, "no caption on a plain row");
    }

    [Test] void LastAccountRemovedStillFlags()
    {
        var row = GameRowViewModel.FromItem(Game(File(FileState.NotBackedUp, "a", "gone")),
            registeredAccounts: System.Array.Empty<string>());
        Assert.True(row.OwnerDisconnected, "an EMPTY registered list is knowledge, and still flags");
    }

    [Test] void NullAccountListNeverFlags()
    {
        var row = GameRowViewModel.FromItem(Game(File(FileState.NotBackedUp, "a", "gone")),
            registeredAccounts: null);
        Assert.False(row.OwnerDisconnected, "null = caller has no account knowledge (harness) -- never flag");
    }

    [Test] void EmptyOwnerStampWithAccountsPresentStaysPlain()
    {
        // "" = a pre-account-era stamp the account service rewrites; with accounts present it
        // cannot be proven orphaned, so it never flags.
        var row = GameRowViewModel.FromItem(Game(File(FileState.NotBackedUp, "a", "")),
            registeredAccounts: new[] { "kept" });
        Assert.False(row.OwnerDisconnected, "an unstamped file with accounts present is not provably orphaned");
    }

    [Test] void ZeroAccountsFlagsEvenUnstampedFiles()
    {
        // With NO accounts registered nothing can be fetched, whatever the stamps say.
        var row = GameRowViewModel.FromItem(Game(File(FileState.NotBackedUp, "a", "")),
            registeredAccounts: System.Array.Empty<string>());
        Assert.True(row.OwnerDisconnected, "zero accounts = every gap flags, stamped or not");
        Assert.Equal("", row.DisconnectedOwners, "no name to show for an unstamped file");
    }

    [Test] void RegisteredOwnerNeverFlags()
    {
        var row = GameRowViewModel.FromItem(Game(File(FileState.NotBackedUp, "a", "kept")),
            registeredAccounts: new[] { "kept" });
        Assert.False(row.OwnerDisconnected, "a registered owner means the file can still be fetched");
    }

    [Test] void RegisteredButSignedOutFlagsLoggedOut()
    {
        var row = GameRowViewModel.FromItem(Game(File(FileState.NotBackedUp, "a", "kept")),
            registeredAccounts: new[] { "kept" }, connectedAccounts: System.Array.Empty<string>());
        Assert.False(row.OwnerDisconnected, "still registered - not removed");
        Assert.True(row.OwnerLoggedOut, "registered with no readable token = logged out");
        Assert.Equal("(account logged out)", row.OwnerCaption, "the quieter caption names the quieter fix");
    }

    [Test] void ConnectedOwnerStaysPlain()
    {
        var row = GameRowViewModel.FromItem(Game(File(FileState.NotBackedUp, "a", "kept")),
            registeredAccounts: new[] { "kept" }, connectedAccounts: new[] { "kept" });
        Assert.False(row.OwnerLoggedOut, "a connected owner can download - nothing to flag");
        Assert.Equal("", row.OwnerCaption);
    }

    [Test] void LoggedOutNeedsAGapToo()
    {
        var row = GameRowViewModel.FromItem(Game(File(FileState.Present, "a", "kept")),
            registeredAccounts: new[] { "kept" }, connectedAccounts: System.Array.Empty<string>());
        Assert.False(row.OwnerLoggedOut, "fully backed up - no risk to point out, signed out or not");
    }

    [Test] void RemovedOutranksLoggedOut()
    {
        var row = GameRowViewModel.FromItem(Game(File(FileState.NotBackedUp, "a", "gone")),
            registeredAccounts: new[] { "kept" }, connectedAccounts: System.Array.Empty<string>());
        Assert.True(row.OwnerDisconnected, "owner unregistered = removed, whatever the token state");
        Assert.Equal("(account removed)", row.OwnerCaption, "removed is the stronger claim and wins");
    }

    [Test] void OutdatedCopyCountsAsHeld()
    {
        // Outdated = bytes on disk (HasLocalCopy). The flag marks what cannot be RE-FETCHED at all;
        // a superseded copy you hold is not a hole in the backup, so it alone does not flag.
        var row = GameRowViewModel.FromItem(Game(File(FileState.Outdated, "a", "gone")),
            registeredAccounts: new[] { "kept" });
        Assert.False(row.OwnerDisconnected, "an outdated local copy is still a held copy");
    }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Grog.App.ViewModels;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Runs;
using Grog.Core.Tests.Framework;

/// <summary>
/// The staged "Where extras are stored" card (09-11). <see cref="ExtrasPlacementTests"/> pins the PLANNER;
/// this pins the CARD - what is staged, when it is dirty, and what the leave guard does - because that is
/// the half that decides whether the planner is ever asked.
///
/// Reachable at all only because the editor takes <see cref="IExtrasPlacementHost"/> rather than the root
/// view model (09-11); the fake below is the whole shell it needs. Same pattern as ScheduleViewModelTests.
/// </summary>
[Trait("settings")]
public sealed class ExtrasPlacementEditorTests
{
    private sealed class FakeHost : IExtrasPlacementHost
    {
        public JsonManifestStore? Store { get; set; }
        public string BackupRoot { get; set; } = "";
        public CancellationToken SessionToken => default;
        public List<(string? RootId, string? Label)> Roots { get; } = new();
        public IReadOnlyList<(string? RootId, string? Label)> Locations => Roots;
        public bool MigratePromptOpen { get; set; }
        public bool ExtrasInScope { get; set; } = true;

        public int Previews, Commits;
        public LayoutMigrationPlan? Offered;
        public List<string?> Navigated { get; } = new();
        public List<string> Logged { get; } = new();

        public void RaiseFolderPreview() => Previews++;
        public void AfterCommit() => Commits++;
        public void OfferPlan(LayoutMigrationPlan plan) => Offered = plan;
        public void LeaveTo(string? view) => Navigated.Add(view);
        public void Log(string message, bool isError = false) => Logged.Add(message);
    }

    private string _dir = "";
    private FakeHost _host = null!;

    [Setup]
    void Setup()
    {
        _dir = Directory.CreateTempSubdirectory("grog-extras-vm-").FullName;
        Directory.CreateDirectory(Path.Combine(_dir, "r1"));
        Directory.CreateDirectory(Path.Combine(_dir, "r2"));
        _host = new FakeHost { BackupRoot = Path.Combine(_dir, "r1") };
    }

    [Teardown]
    void Teardown() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>A store holding the COMMITTED state, plus the two locations the card asks about.</summary>
    private ExtrasPlacementEditor Card(ExtrasPlacement committed, string committedExtrasRoot = "r1",
                                       bool secondDrive = true)
    {
        var store = new JsonManifestStore(_dir);
        var m = store.Current;
        m.PrimaryRootId = "r1";
        m.Roots.Add(new BackupRoot { Id = "r1", Label = "Primary", PathHint = Path.Combine(_dir, "r1") });
        m.Roots.Add(new BackupRoot { Id = "r2", Label = "The Big One", PathHint = Path.Combine(_dir, "r2") });
        m.SetExtrasLayout(committed);
        m.Routing.SetRole(ContentRole.Extras, committedExtrasRoot);
        _host.Store = store;
        _host.Roots.Add(("r1", "Primary"));
        if (secondDrive) _host.Roots.Add(("r2", "The Big One"));

        var card = new ExtrasPlacementEditor(_host);
        card.ResetFromManifest();
        return card;
    }

    // ---- staging ------------------------------------------------------------------------------------

    // Nothing is dirty at rest, or the footer offers to apply a change nobody made.
    [Test]
    void FreshCard_IsNotDirty()
    {
        var card = Card(ExtrasPlacement.WithGame);
        Assert.False(card.IsDirty, "opening the page is not a change");
        Assert.Equal("", card.PendingText, "and the footer says nothing");
    }

    // THE RULE THE BUG TURNED ON. WithGame routes an extra with its game, so a stored Extras role root is
    // inert; treating it as a difference would make the card permanently dirty on a library that once used
    // a separate tree, and Apply would plan moves for a setting that does not apply.
    [Test]
    void WithGame_IgnoresAStaleExtrasRoot()
    {
        // The manifest holds an Extras root from a previous separate-tree layout. Opening the card stages
        // that same root, so nothing differs YET -- asserting here would pass whether the rule exists or
        // not (it did, until a mutation test caught it). Drive the path where the rule actually decides:
        // going out to a separate tree and back re-stages the PRIMARY, so staged and committed now differ
        // on a value WithGame ignores.
        var card = Card(ExtrasPlacement.WithGame, committedExtrasRoot: "r2");
        card.SelectShapeCommand.Execute("separate");
        card.SelectShapeCommand.Execute("withgame");

        Assert.False(card.IsDirty,
            "back at the committed shape: routing is inert under WithGame, so there is nothing to apply");
        Assert.False(card.OnSecondaryActive, "and the storage pills do not claim the second drive");
        Assert.Equal("", card.PendingText, "so the footer offers nothing");
    }

    [Test]
    void ChangingTheShape_MakesItDirty()
    {
        var card = Card(ExtrasPlacement.WithGame);
        card.SelectShapeCommand.Execute("separate");

        Assert.True(card.IsDirty, "shape differs from the manifest");
        Assert.True(card.SeparateActive, "the pills show the staged shape, not the committed one");
        Assert.Equal("Not applied", card.PendingText, "the count is honest about not knowing yet");
    }

    // The storage half alone is a change. This is the case that silently did nothing before 09-11.
    [Test]
    void ChangingOnlyTheStorage_MakesItDirty()
    {
        var card = Card(ExtrasPlacement.SeparateByGame);
        Assert.False(card.IsDirty, "precondition: committed and staged agree");

        card.SelectStorageCommand.Execute("secondary");

        Assert.True(card.IsDirty, "the device changed even though the shape did not");
        Assert.True(card.OnSecondaryActive, "and the pill shows it");
    }

    // Coming back to a separate tree restores the DEFAULT ordering, not whatever was last committed: the
    // user is choosing "separate", not re-choosing by-type.
    [Test]
    void ReturningToSeparate_RestoresByGame()
    {
        var card = Card(ExtrasPlacement.SeparateByType);
        card.SelectShapeCommand.Execute("withgame");
        card.SelectShapeCommand.Execute("separate");

        Assert.True(card.ByGameActive, "by-game is the shape a fresh 'separate' means");
        Assert.False(card.ByTypeActive, "the old ordering is not silently reinstated");
    }

    // Choosing WithGame must also drop a staged second drive, or Apply would commit a routing that the
    // placement ignores and the next reader has to know to ignore too.
    [Test]
    void ChoosingWithGame_DropsTheStagedSecondDrive()
    {
        var card = Card(ExtrasPlacement.SeparateByGame);
        card.SelectStorageCommand.Execute("secondary");
        Assert.True(card.OnSecondaryActive, "precondition");

        card.SelectShapeCommand.Execute("withgame");

        Assert.False(card.OnSecondaryActive, "the storage half is inert and must not stay staged");
    }

    // ---- which rows exist ---------------------------------------------------------------------------

    // ShowStorageRow asks ONLY "is there a second place". Testing the shape here too made the card change
    // height when the shape toggled; the view reserves the space instead.
    [Test]
    void StorageRow_FollowsTheDriveCountAndNotTheShape()
    {
        var withGame = Card(ExtrasPlacement.WithGame);
        Assert.True(withGame.ShowStorageRow, "two drives: the row exists even under WithGame");

        Teardown(); Setup();
        var oneDrive = Card(ExtrasPlacement.SeparateByGame, secondDrive: false);
        Assert.False(oneDrive.ShowStorageRow, "one drive: there is nowhere else to put anything");
    }

    [Test]
    void SecondaryLabel_UsesTheDrivesOwnName()
    {
        var card = Card(ExtrasPlacement.SeparateByGame);
        Assert.Equal("The Big One", card.SecondaryLabel, "the pill names the drive the user named");

        Teardown(); Setup();
        _host.Roots.Add(("r1", "Primary"));
        _host.Roots.Add(("r2", null));
        var store = new JsonManifestStore(_dir);
        store.Current.PrimaryRootId = "r1";
        _host.Store = store;
        var unnamed = new ExtrasPlacementEditor(_host);
        unnamed.ResetFromManifest();
        Assert.Equal("Secondary", unnamed.SecondaryLabel, "an unnamed drive falls back, never to blank");
    }

    // ---- discard ------------------------------------------------------------------------------------

    [Test]
    void Discard_ReturnsToTheCommittedState()
    {
        var card = Card(ExtrasPlacement.WithGame);
        card.SelectShapeCommand.Execute("separate");
        card.SelectStorageCommand.Execute("secondary");
        Assert.True(card.IsDirty, "precondition");

        card.DiscardCommand.Execute(null);

        Assert.False(card.IsDirty, "nothing is pending");
        Assert.True(card.WithGameActive, "the pills are back on the committed shape");
    }

    // ---- the leave guard ----------------------------------------------------------------------------

    // Staged-but-unapplied is the one state a page change would silently discard, which is the whole
    // reason the guard exists.
    [Test]
    void HasPending_TracksDirty()
    {
        var card = Card(ExtrasPlacement.WithGame);
        Assert.False(card.HasPending, "nothing staged, nothing to ask about");

        card.SelectShapeCommand.Execute("separate");
        Assert.True(card.HasPending, "the shell must ask before leaving");
    }

    [Test]
    void StayHere_ClosesThePromptAndDoesNotNavigate()
    {
        var card = Card(ExtrasPlacement.WithGame);
        card.SelectShapeCommand.Execute("separate");
        card.AskBeforeLeaving("Library");

        Assert.True(card.ShowLeavePrompt, "the question is on screen");
        card.StayHereCommand.Execute(null);

        Assert.False(card.ShowLeavePrompt, "and it closes");
        Assert.Empty(_host.Navigated, "staying means staying");
        Assert.True(card.IsDirty, "and the staged choices survive");
    }

    // Discard-and-leave must do BOTH, and land on the page the guard intercepted - not the one the user
    // was on.
    [Test]
    void DiscardAndLeave_DropsTheStagingAndGoesWhereAsked()
    {
        var card = Card(ExtrasPlacement.WithGame);
        card.SelectShapeCommand.Execute("separate");
        card.AskBeforeLeaving("Storage");

        card.DiscardAndLeaveCommand.Execute(null);

        Assert.False(card.ShowLeavePrompt, "the prompt closes");
        Assert.False(card.IsDirty, "the staging is dropped");
        Assert.Equal("Storage", Assert.Single(_host.Navigated, "exactly one navigation"),
                     "to the destination the guard held, not back to Settings");
    }

    // ---- apply --------------------------------------------------------------------------------------

    // Apply commits BOTH halves in one write. Committing one without the other is the 09-11 bug.
    [Test]
    async Task Apply_CommitsShapeAndStorageTogether()
    {
        var card = Card(ExtrasPlacement.WithGame);
        card.SelectShapeCommand.Execute("separate");
        card.SelectStorageCommand.Execute("secondary");

        await card.ApplyCommand.ExecuteAsync(null);

        var m = _host.Store!.Current;
        Assert.Equal(ExtrasPlacement.SeparateByGame, m.ExtrasLayout, "the shape landed");
        Assert.Equal("r2", m.Routing.RoleRoots[ContentRole.Extras], "and so did the device, in the same apply");
        Assert.False(card.IsDirty, "the card is clean afterwards");
        Assert.True(_host.Commits > 0, "the host re-derived its state");
    }

    // A preference with no files to move is not a failed migration: the setting still sticks and no move
    // prompt is raised. Pressing Apply twice to make a choice take is the failure this pins against.
    [Test]
    async Task Apply_WithNothingToMove_StillCommitsAndOffersNoPlan()
    {
        var card = Card(ExtrasPlacement.WithGame);
        card.SelectShapeCommand.Execute("separate");

        await card.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(ExtrasPlacement.SeparateByGame, _host.Store!.Current.ExtrasLayout, "committed");
        Assert.Null(_host.Offered, "an empty library has nothing to move, so there is no prompt");
    }

    // Apply-and-leave navigates AFTER applying, and says where the move question went if one was raised -
    // leaving silently would hide a question the user has not answered.
    [Test]
    async Task ApplyAndLeave_AppliesThenNavigates_AndNamesTheMovePrompt()
    {
        var card = Card(ExtrasPlacement.WithGame);
        card.SelectShapeCommand.Execute("separate");
        card.AskBeforeLeaving("Library");
        _host.MigratePromptOpen = true;   // as if Apply raised it

        await card.ApplyAndLeaveCommand.ExecuteAsync(null);

        Assert.False(card.IsDirty, "applied");
        Assert.Equal("Library", Assert.Single(_host.Navigated, "one navigation"), "to the held destination");
        Assert.True(_host.Logged.Exists(l => l.Contains("Storage page")),
                    "the user is told where the unanswered question is");
    }

    // Sweep 2 #22: the same Apply, reached from the window's X. Closing would drop the move question.
    [Test]
    async Task ApplyAndLeave_FromTheCloseGuard_GoesToTheMovePromptInsteadOfClosing()
    {
        var card = Card(ExtrasPlacement.WithGame);
        card.SelectShapeCommand.Execute("separate");
        card.AskBeforeLeaving(MainWindowViewModel.CloseTarget);
        _host.MigratePromptOpen = true;

        await card.ApplyAndLeaveCommand.ExecuteAsync(null);

        Assert.Equal(ExtrasPlacementEditor.StoragePage, Assert.Single(_host.Navigated, "one navigation"), "to the question, not out of the app");
    }

    // Sweep 2 #19: the card read the drives once. Removing the secondary left it dirty on a missing drive.
    [Test]
    void RemovingTheSecondDrive_UnderAnUntouchedCard_LeavesItClean()
    {
        var card = Card(ExtrasPlacement.SeparateByGame, committedExtrasRoot: "r2");
        Assert.False(card.IsDirty, "at rest");
        // Storage removes r2: routing falls back to the primary, the location list shrinks.
        _host.Store!.Current.Routing.SetRole(ContentRole.Extras, "r1");
        _host.Roots.RemoveAll(l => l.RootId == "r2");

        card.OnLocationsChanged();

        Assert.False(card.IsDirty, "nothing the user staged: the card follows the manifest");
        Assert.False(card.OnSecondaryActive, "and no longer names the missing drive");
    }

    [Test]
    void RemovingTheSecondDrive_DropsAStagedChoiceThatNamedIt_AndKeepsOneThatDidNot()
    {
        var card = Card(ExtrasPlacement.SeparateByGame);
        card.SelectStorageCommand.Execute("secondary");
        Assert.True(card.IsDirty, "staged on r2");
        _host.Roots.RemoveAll(l => l.RootId == "r2");
        card.OnLocationsChanged();
        Assert.False(card.IsDirty, "the drive is gone: the choice is dropped, not applied to a missing root");

        card.SelectOrganizeCommand.Execute("bytype");
        card.OnLocationsChanged();
        Assert.True(card.IsDirty, "a shape choice survives a storage refresh");
        Assert.True(card.ByTypeActive);
    }

    // Sweep 2 #29: a scope change hides the card; its staging must not outlive it.
    [Test]
    void TakingExtrasOutOfScope_DropsTheStaging()
    {
        var card = Card(ExtrasPlacement.WithGame);
        card.SelectShapeCommand.Execute("separate");
        Assert.True(card.HasPending, "staged");

        _host.ExtrasInScope = false;
        Assert.False(card.HasPending, "a hidden card never stops a page change");
        card.OnLocationsChanged();   // the scope change raises the policy state, which lands here
        Assert.False(card.IsDirty, "and the staging is gone");

        _host.ExtrasInScope = true;
        card.OnLocationsChanged();
        Assert.True(card.WithGameActive, "back in scope it shows the committed state");
    }
}

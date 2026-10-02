// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Collections.Generic;
using System.Linq;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

// Scope decides what is BACKED UP; HiddenPolicy decides what is SHOWN. These lock the second half -- and
// in particular that hiding NEVER changes what gets backed up, only what is visible.
public class HiddenPolicyTests
{
    static LibraryItem Game(long id, string title, params (string board, int age)[] ratings)
    {
        var it = new LibraryItem { GogId = id, Title = title, Slug = title.ToLowerInvariant() };
        var d = new Dictionary<string, int>();
        foreach (var (b, a) in ratings) d[b] = a;
        it.AgeRatings = d;
        return it;
    }

    [Test]
    void ShowEverything_HidesNothing()
    {
        Assert.True(HiddenPolicy.ShowEverything.IsVisible(Game(1, "Adult", ("pegi", 18))), "no policy -> visible");
        Assert.True(HiddenPolicy.ShowEverything.IsVisible(Game(2, "Kids", ("pegi", 3))), "no policy -> visible");
    }

    [Test]
    void MatureCutoff_HidesAtOrAboveTheThresholdOnly()
    {
        var p = new HiddenPolicy(true, 18, null, null);
        Assert.True(p.IsHidden(Game(1, "Adult", ("pegi", 18))), "18 is hidden");
        Assert.True(p.IsHidden(Game(2, "Older", ("esrb", 21))), "above the cutoff is hidden");
        Assert.True(p.IsVisible(Game(3, "Teen", ("pegi", 16))), "below the cutoff stays");
    }

    // (09-26, owner) The App reads GOG's rating only: an adult title (GOG 18) hides, a violent classic GOG never
    // rated (Deus Ex: ESRB 17, USK 18) stays visible.
    [Test]
    void GogBoardOnly_HidesGogAdultRatings_NotOtherBoards()
    {
        var gog = new HiddenPolicy(true, 18, new[] { "gog" }, null);
        Assert.True(gog.IsHidden(Game(1, "Being a DIK", ("gog", 18))), "GOG 18 hides");
        Assert.True(gog.IsVisible(Game(2, "Deus Ex", ("esrb", 17), ("pegi", 16), ("usk", 18))), "no GOG rating -> visible");
    }

    // (09-26, owner) A game kept in the mature review stays visible under the rating filter, but a per-game Hide
    // still hides it; IsMatureRated ignores Keep (it is what the review lists).
    [Test]
    void KeptGames_AreExemptFromTheRatingFilterOnly()
    {
        var kept = new HiddenPolicy(true, 18, null, null, KeptIds: new long[] { 1 });
        var g = Game(1, "Deus Ex", ("esrb", 17), ("usk", 18));
        Assert.True(kept.IsVisible(g), "kept -> visible");
        Assert.True(kept.IsMatureRated(g), "still listed as rated 18+");
        Assert.True(kept.IsHidden(Game(2, "Other", ("gog", 18))), "not kept -> hidden");
        var alsoHidden = new HiddenPolicy(true, 18, null, new long[] { 1 }, KeptIds: new long[] { 1 });
        Assert.True(alsoHidden.IsHidden(g), "a per-game Hide wins over Keep");
        Assert.True(new HiddenPolicy(false, 18, null, null, KeptIds: new long[] { 1 }).IsVisible(Game(2, "Other", ("gog", 18))), "filter off -> visible");
    }

    // Guessing would hide ordinary games, so an absent rating must never imply "adult".
    [Test]
    void UnratedIsNeverHidden()
    {
        var p = new HiddenPolicy(true, 18, null, null);
        Assert.True(p.IsVisible(Game(1, "Unrated")), "no ratings at all -> visible");
        Assert.Equal(-1, p.EffectiveAge(new Dictionary<string, int>()), "no ratings -> -1, not 0");
    }

    [Test]
    void BoardFilter_OnlyCountsTheChosenBoards()
    {
        var pegiOnly = new HiddenPolicy(true, 18, new[] { "pegi" }, null);
        var g = Game(1, "Split", ("pegi", 12), ("esrb", 18));
        Assert.True(pegiOnly.IsVisible(g), "rated 12 by PEGI, so a PEGI-only parent sees it");
        var allBoards = new HiddenPolicy(true, 18, null, null);
        Assert.True(allBoards.IsHidden(g), "counting every board, the ESRB 18 hides it");
    }

    [Test]
    void PerGameHide_IsIndependentOfTheMatureToggle()
    {
        var p = new HiddenPolicy(false, 18, null, new long[] { 42 });
        Assert.True(p.IsHidden(Game(42, "Picked")), "explicitly hidden even with the mature filter off");
        Assert.True(p.IsVisible(Game(43, "Other")), "others unaffected");
    }

    // "Show hidden items" is a temporary reveal for the per-game picks; it is never persisted.
    [Test]
    void RevealHidden_UnhidesThePicksButNotTheMatureCutoff()
    {
        var reveal = new HiddenPolicy(true, 18, null, new long[] { 42 }, RevealHidden: true);
        Assert.True(reveal.IsVisible(Game(42, "Picked")), "the reveal shows a per-game pick");
        Assert.True(reveal.IsHidden(Game(43, "Adult", ("pegi", 18))), "but the parental cutoff still holds");
    }

    // THE boundary: hiding conceals names, never bytes. A hidden game is still backed up, so scope -- which
    // is what every size/health number is built on -- must be completely untouched by the policy.
    [Test]
    void HidingNeverChangesWhatGetsBackedUp()
    {
        var it = Game(1, "Adult", ("pegi", 18));
        it.Files.Add(new GameFile { GameGogId = 1, FileKey = "f", Kind = FileKind.Installer,
            Name = "setup.exe", Os = "windows", Language = "English",
            ExpectedSizeBytes = 100, State = FileState.Verified });
        var p = new HiddenPolicy(true, 18, null, null);
        Assert.True(p.IsHidden(it), "hidden from view");
        Assert.Equal(1, BackupScope.Scoped(it, Scope.Both).Count(), "still fully in backup scope");
        Assert.Equal(BackupStatus.Complete, BackupScope.Status(it, Scope.Both), "and still reported as backed up");
    }

    // The id picks are sets now (O(1) membership on every row). A HashSet and a list of the same ids decide alike,
    // and an empty list normalises to null like the "no picks" shape the App already passes.
    [Test]
    void IdSets_DecideTheSameAsLists()
    {
        var hidden = new HashSet<long> { 42 };
        var kept = new HashSet<long> { 7 };
        var viaSet = new HiddenPolicy(true, 18, null, hidden, KeptIds: kept);
        var viaList = new HiddenPolicy(true, 18, null, new List<long> { 42 }, KeptIds: new long[] { 7 });
        foreach (var p in new[] { viaSet, viaList })
        {
            Assert.True(p.IsHidden(Game(42, "Picked")), "per-game hide");
            Assert.True(p.IsVisible(Game(7, "Kept", ("gog", 18))), "Keep beats the mature test");
            Assert.True(p.IsHidden(Game(8, "Adult", ("gog", 18))), "mature test still applies");
            Assert.True(p.IsVisible(Game(9, "Kids", ("gog", 3))), "unaffected game visible");
        }
        Assert.True(ReferenceEquals(hidden, viaSet.HiddenIds), "a set is used as given, not copied");
        Assert.Null(new HiddenPolicy(false, 18, null, new List<long>()).HiddenIds, "an empty list is no picks");
        Assert.Null(HiddenPolicy.ToSet(null), "null stays null");
    }
}

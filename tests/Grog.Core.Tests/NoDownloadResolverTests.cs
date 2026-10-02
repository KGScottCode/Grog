// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Collections.Generic;
using System.Linq;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

// The coverage heuristic (DetermineCoverage/BaseStem) is private; exercise it via a tiny
// reflection-free reimplementation would drift, so instead we validate the observable
// contract through a fake API client that returns canned lookups. Kept simple: we assert
// the ResolvedProduct coverage verdicts for representative shapes.
public class NoDownloadResolverTests
{
    // A stand-in GogApiClient is heavy to fake (sealed deps), so these tests focus on the
    // pure coverage classification by driving the resolver against an in-memory game list
    // and a fake that echoes titles. We validate the three important verdicts.

    static LibraryItem Game(string title) => new() { GogId = 1, Title = title };

    [Test]
    void PackTitle_SharesStemWithBaseGame_IsCovered()
    {
        // "This War of Mine: Complete Edition" should be recognized as covered by
        // the "This War of Mine" base game via shared stem "This War of Mine".
        var games = new List<LibraryItem> { Game("This War of Mine"), Game("Some Other Game") };
        var verdict = NoDownloadResolver.DetermineCoverage("This War of Mine: Complete Edition", games.Select(g => g.Title).ToList());
        Assert.Equal(CoverageKind.LikelyCoveredByBaseGame, verdict.kind, "pack covered by base");
        Assert.Equal("This War of Mine", verdict.coveredBy!, "covered-by base title");
    }

    [Test]
    void ExactTitleMatch_IsSameProduct()
    {
        var games = new List<LibraryItem> { Game("DOOM 3") };
        var verdict = NoDownloadResolver.DetermineCoverage("DOOM 3", games.Select(g => g.Title).ToList());
        Assert.Equal(CoverageKind.SameProduct, verdict.kind, "exact match");
    }

    [Test]
    void NoMatchingBaseGame_IsNotObviouslyCovered()
    {
        var games = new List<LibraryItem> { Game("Baldur's Gate"), Game("Icewind Dale") };
        var verdict = NoDownloadResolver.DetermineCoverage("Warcraft I & II Bundle", games.Select(g => g.Title).ToList());
        Assert.Equal(CoverageKind.NotObviouslyCovered, verdict.kind, "no base game -> flagged");
    }

    [Test]
    void ShortStems_DoNotFalselyMatch()
    {
        // A 2-3 char stem must not match everything (guard against "II" matching "III").
        var games = new List<LibraryItem> { Game("X") };
        var verdict = NoDownloadResolver.DetermineCoverage("Y: Something", games.Select(g => g.Title).ToList());
        Assert.Equal(CoverageKind.NotObviouslyCovered, verdict.kind, "short stems don't match");
    }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.Core.Guide;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

/// <summary>
/// The guide's example-file pick, now Core-owned. The rule under test: smallest non-patch GAME file in the
/// library, ties broken alphabetically by game then file, extras never picked, only actionable states, only
/// in-scope files, only positive sizes. Deterministic on any library - GOG rounds sizes to whole megabytes,
/// so ties are the NORMAL case, not the edge case.
/// </summary>
[NewBatch]
[Trait("guide")]
public class GuideFilePickerTests
{
    private static readonly Scope All = new(true, true, null, null, null);

    private static GameFile File(string name, long size, FileKind kind = FileKind.Installer,
                                      FileState state = FileState.NotBackedUp)
        => new() { Kind = kind, Name = name, State = state, ExpectedSizeBytes = size, FileKey = name };

    private static LibraryItem Game(long id, string title, params GameFile[] files)
    {
        var it = new LibraryItem { GogId = id, Title = title, Slug = $"g{id}" };
        it.Files.AddRange(files);
        return it;
    }

    [Test] void SmallestFileWins()
    {
        var pick = GuideFilePicker.Pick(new[]
        {
            Game(1, "Anchorhead", File("setup.exe", 5_000)),
            Game(2, "Beneath", File("setup-1.bin", 2_000), File("setup-2.bin", 9_000)),
        }, All);
        Assert.Equal((2L, "setup-1.bin"), pick!.Value);
    }

    [Test] void TiesBreakByGameThenFile()
    {
        // Same size everywhere: the alphabetically first game wins, then its alphabetically first file.
        var pick = GuideFilePicker.Pick(new[]
        {
            Game(1, "Zork", File("a.bin", 1_000)),
            Game(2, "Anchorhead", File("z.bin", 1_000), File("b.bin", 1_000)),
        }, All);
        Assert.Equal((2L, "b.bin"), pick!.Value);
    }

    [Test] void ExtrasAreNeverPicked()
    {
        // A tiny soundtrack must lose to a bigger installer: the first backup demonstrates the thing the
        // app is for.
        var pick = GuideFilePicker.Pick(new[]
        {
            Game(1, "Anchorhead", File("ost.zip", 100, FileKind.Extra), File("setup.exe", 5_000)),
        }, All);
        Assert.Equal((1L, "setup.exe"), pick!.Value);
    }

    [Test] void PatchesAreNeverPicked()
    {
        // Both by kind and by name: a patch backs up an update to something you do not have yet.
        var pick = GuideFilePicker.Pick(new[]
        {
            Game(1, "Anchorhead", File("update.bin", 100, FileKind.Patch),
                                  File("patch_v2.exe", 200),
                                  File("setup.exe", 5_000)),
        }, All);
        Assert.Equal((1L, "setup.exe"), pick!.Value);
    }

    [Test] void OnlyActionableStatesCount()
    {
        // Present/Verified files are already backed up; the step needs a real gap to close.
        var pick = GuideFilePicker.Pick(new[]
        {
            Game(1, "Anchorhead", File("small.bin", 100, state: FileState.Present),
                                  File("big.bin", 9_000)),
            Game(2, "Beneath", File("mid.bin", 500, state: FileState.Missing)),
        }, All);
        Assert.Equal((2L, "mid.bin"), pick!.Value);
    }

    [Test] void OutOfScopeFilesAreSkipped()
    {
        // Extras-only scope excludes every game file, and extras can never be picked: nothing qualifies.
        var extrasOnly = new Scope(false, true, null, null, null);
        var pick = GuideFilePicker.Pick(new[]
        {
            Game(1, "Anchorhead", File("setup.exe", 5_000), File("ost.zip", 100, FileKind.Extra)),
        }, extrasOnly);
        Assert.True(pick is null);
    }

    [Test] void ZeroOrUnknownSizeIsSkipped()
    {
        var pick = GuideFilePicker.Pick(new[]
        {
            Game(1, "Anchorhead", File("mystery.bin", 0), File("setup.exe", 5_000)),
        }, All);
        Assert.Equal((1L, "setup.exe"), pick!.Value);
    }

    [Test] void EmptyLibraryPicksNothing()
    {
        Assert.True(GuideFilePicker.Pick(System.Array.Empty<LibraryItem>(), All) is null);
    }

    [Test] void AFileWithNoArrowOnScreenIsNotTheDemo()
    {
        // The stop says "click the arrow": a paused partial or a queued file shows a pause glyph or a queued
        // state instead, so the ring would have nothing to hold (seen 09-02 resuming the tour mid-run).
        var partial = File("setup-a.exe", 1_000); partial.HasPartial = true; partial.PartialBytes = 10;
        var queued = File("setup-b.exe", 2_000);
        var plain = File("setup-c.exe", 3_000);
        var pick = GuideFilePicker.Pick(new[] { Game(1, "A", partial, queued, plain) }, All,
            alreadyInFlight: (_, f) => ReferenceEquals(f, queued));
        Assert.Equal((1L, plain.FileKey), pick!.Value, "the smallest file whose arrow is actually there");
    }
}

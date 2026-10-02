// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.App.ViewModels;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

/// <summary>
/// The shared status vocabulary. The point of these tests is not that a condition maps to a particular
/// English word - it is that there is exactly ONE mapping, so the Library column and the Folders grid can
/// never describe the same file differently. They used to hold separate switch statements.
/// </summary>
[NewBatch]
[Trait("vocabulary")]
public class ConditionVocabularyTests
{
    [Test] void EveryConditionHasAWord()
    {
        // A condition with no case would silently fall through to "Not Downloaded" and read as a file the
        // user never fetched - the most misleading possible default for, say, a corrupt one.
        foreach (FileCondition c in Enum.GetValues<FileCondition>())
            Assert.True(!string.IsNullOrWhiteSpace(ConditionVocabulary.Word(c)));
    }

    [Test] void EveryWordHasAGlyph()
    {
        foreach (FileCondition c in Enum.GetValues<FileCondition>())
            Assert.True(!string.IsNullOrWhiteSpace(ConditionVocabulary.Glyph(ConditionVocabulary.Word(c))));
    }

    [Test] void PresentAndVerifiedReadTheSame()
    {
        // Verified is Present plus a check we no longer advertise (the verified stamp was removed),
        // so the two must not produce two different words in the column.
        Assert.Equal(ConditionVocabulary.Word(FileCondition.Present), ConditionVocabulary.Word(FileCondition.Verified));
        Assert.Equal("Backed Up", ConditionVocabulary.Word(FileCondition.Present));
    }

    [Test] void DetachedIsNotAnAlarm()
    {
        // A removable drive that is unplugged is SAFE: the bytes are fine, they are just not here. It must not
        // share a word with Missing, which means the bytes are gone.
        Assert.Equal("Disconnected", ConditionVocabulary.Word(FileCondition.Detached));
        Assert.Equal("Missing", ConditionVocabulary.Word(FileCondition.Missing));
    }

    [Test] void UnknownWordsFallBackRatherThanThrow()
    {
        // Row states that are not conditions at all ("Moving", "Verifying") are passed through the same glyph
        // and brush lookups, so an unmapped string must degrade, never crash a row template.
        Assert.Equal(ConditionVocabulary.Glyph("Not Downloaded"), ConditionVocabulary.Glyph("something else"));
    }
}

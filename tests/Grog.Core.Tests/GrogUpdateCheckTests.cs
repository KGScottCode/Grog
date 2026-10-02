// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Grog.Core.Update;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

/// <summary>
/// The update check's whole contract: newer means told, everything else means silence. Silence on failure
/// matters as much as the happy path -- a backup tool is often offline, and an update checker that ever
/// throws or nags becomes a startup hazard.
/// </summary>
[Trait("update")]
public sealed class GrogUpdateCheckTests
{
    private static string Release(string tag, bool draft = false, bool prerelease = false)
        => $"{{\"tag_name\":\"{tag}\",\"html_url\":\"https://github.com/KGScottCode/Grog/releases/tag/{tag}\","
         + $"\"draft\":{(draft ? "true" : "false")},\"prerelease\":{(prerelease ? "true" : "false")}}}";

    [Test]
    void A_newer_release_is_reported_with_its_page()
    {
        var hit = GrogUpdateCheck.Evaluate(Release("v0.2.0"), "0.1.0");
        Assert.True(hit is not null, "0.2.0 is newer than 0.1.0");
        Assert.Equal("0.2.0", hit!.Value.Version, "the bare version, v stripped");
        Assert.True(hit.Value.Url.EndsWith("v0.2.0"), "and the human release page rides along");
    }

    [Test]
    void Same_or_older_is_silence()
    {
        Assert.True(GrogUpdateCheck.Evaluate(Release("v0.1.0"), "0.1.0") is null, "same version: nothing to say");
        Assert.True(GrogUpdateCheck.Evaluate(Release("v0.0.9"), "0.1.0") is null, "older: never suggest a downgrade");
    }

    [Test]
    void Release_day_shapes_pinned()
    {
        // The v0.1.0 tag sets -p:Version=0.1.0 (3 parts) in the shipped build; dev builds run 4 parts.
        // System.Version treats a missing 4th part as -1, so these are the exact comparisons that matter:
        Assert.True(GrogUpdateCheck.Evaluate(Release("v0.1.0"), "0.1.0.1072") is null,
            "a 4-part dev build newer than the tag never nags");
        Assert.True(GrogUpdateCheck.Evaluate(Release("v0.1.0"), "0.1.0.0") is null,
            "a 4-part zero-revision build of the same release is silent");
        Assert.True(GrogUpdateCheck.Evaluate(Release("v0.1.1"), "0.1.0.0") is not null,
            "the next patch is reported to a 4-part current");
        Assert.True(GrogUpdateCheck.Evaluate(Release("v0.1.1"), "0.1.0") is not null,
            "the next patch is reported to the shipped 3-part current");
    }

    [Test]
    void Tag_spellings_all_compare()
    {
        // The workflow tags v0.1.0; AppVersionText trims v/V; a hand-made tag might skip the prefix or a part.
        Assert.True(GrogUpdateCheck.Evaluate(Release("V0.2.0"), "0.1.0") is not null, "capital V");
        Assert.True(GrogUpdateCheck.Evaluate(Release("0.2.0"), "0.1.0") is not null, "no prefix");
        Assert.True(GrogUpdateCheck.Evaluate(Release("v0.2"), "0.1.0") is not null, "two-part tag");
        Assert.True(GrogUpdateCheck.Evaluate(Release("v1"), "0.1.0") is not null, "bare major normalizes");
    }

    [Test]
    void Garbage_drafts_and_prereleases_are_silence()
    {
        Assert.True(GrogUpdateCheck.Evaluate("not json at all", "0.1.0") is null, "malformed payload");
        Assert.True(GrogUpdateCheck.Evaluate("{}", "0.1.0") is null, "empty object");
        Assert.True(GrogUpdateCheck.Evaluate(Release("banana"), "0.1.0") is null, "unparseable tag");
        Assert.True(GrogUpdateCheck.Evaluate(Release("v9.9.9", draft: true), "0.1.0") is null, "a draft is not a release");
        Assert.True(GrogUpdateCheck.Evaluate(Release("v9.9.9", prerelease: true), "0.1.0") is null, "nor is a prerelease");
        Assert.True(GrogUpdateCheck.Evaluate(Release("v9.9.9"), "garbage-current") is null,
            "an unparseable RUNNING version also stays silent rather than always-updating");
    }
}

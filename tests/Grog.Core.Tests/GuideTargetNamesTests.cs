// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

[Trait("guide")]
public sealed class GuideTargetNamesTests
{
    private static string ReadAllViewXaml()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "Grog.slnx")))
                {
                    var views = Path.Combine(d.FullName, "src", "Grog.App", "Views");
                    return string.Concat(Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories)
                                                  .Where(f => !f.Contains("_to_delete"))
                                                  .Select(File.ReadAllText));
                }
        throw new InvalidOperationException(
            $"Could not locate the repo root (Grog.slnx) from {AppContext.BaseDirectory} or {Directory.GetCurrentDirectory()}");
    }

    [Test]
    void Every_guide_target_and_scope_name_exists_in_the_view()
    {
        var xaml = ReadAllViewXaml();
        // Both attribute spellings: x:Name is the norm here, Name is legal and would still resolve.
        var declared = Regex.Matches(xaml, @"(?:x:)?Name=""(Guide[A-Za-z0-9_]*)""")
                            .Select(m => m.Groups[1].Value)
                            .ToHashSet(StringComparer.Ordinal);

        // Collected, not fail-fast: when a sweep unhooks a dozen names at once, the repair list matters more
        // than the first casualty.
        var broken = new System.Collections.Generic.List<string>();
        foreach (var step in Grog.Core.Guide.FirstRunTour.Nodes)
        {
            // GuideFileDownload is the one target resolved by DATA, not by name: every file row carries an
            // identically-named button, so ResolveTarget matches the row the guide picked instead. It is
            // correct for it to have no x:Name.
            void Check(string kind, string name)
            {
                if (name == "GuideFileDownload") return;
                if (!string.IsNullOrEmpty(name) && !declared.Contains(name))
                    broken.Add($"{step.Key} {kind} '{name}'");
            }
            Check("spotlights", step.TargetName);
            Check("scopes to", step.ScopeName);
            Check("anchors to", step.AnchorName);
        }

        Assert.True(broken.Count == 0,
            $"{broken.Count} guide name(s) name no control in the views: {string.Join("; ", broken)}");
    }

    [Test]
    void The_guide_actually_spotlights_something_on_most_steps()
    {
        // A blunt floor, not a style rule: if the guide stops pointing at real controls it stops being a
        // guide, and the failure is invisible (the caption just drifts to a corner). Rail navigation steps
        // are the only ones that legitimately target a menu item rather than page content.
        var withTarget = Grog.Core.Guide.FirstRunTour.Nodes.Count(s => !string.IsNullOrEmpty(s.TargetName));
        Assert.True(withTarget >= Grog.Core.Guide.FirstRunTour.Nodes.Count - 1,
            "essentially every step names a spotlight target; a step with none renders a floating caption");
    }
}

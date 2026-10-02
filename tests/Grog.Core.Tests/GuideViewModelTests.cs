// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Grog.App;
using Grog.App.ViewModels;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

/// <summary>
/// The guide behind IGuideHost (split 2026-09-02). Every test drives GuideViewModel against a fake window:
/// the facts the steps wait on are plain settable properties, the four actions are counters. Nothing here
/// reads GuideDetail (it probes the OS keyring). AppSettings.Save is safe: Program.cs points GROG_CONFIG_DIR
/// at a throwaway temp dir before any test runs, and Save swallows IO failures.
/// </summary>
[NewBatch]
[Trait("guide")]
public sealed class GuideViewModelTests
{
    private sealed class FakeHost : IGuideHost
    {
        public bool IsConnected { get; set; }
        public int TotalCount { get; set; }
        public bool HasBackedUpAnything { get; set; }
        public HashSet<(long, string)> BackedUp { get; } = new();
        public bool IsFileBackedUp(long gogId, string fileKey) => BackedUp.Contains((gogId, fileKey));
        public HashSet<(long, string)> Queued { get; } = new();
        public IReadOnlySet<(long GogId, string FileKey)> QueuedFiles() => Queued;
        public string CurrentViewName { get; set; } = "Overview";
        public bool IsScanning { get; set; }
        public bool RunActive { get; set; }
        public bool ShowingItemCount { get; set; }
        public bool HasCloudSaves { get; set; }
        public List<GameRowViewModel> Rows { get; } = new();
        public IEnumerable<GameRowViewModel> Games => Rows;
        public Scope EffectiveScope { get; set; } = Scope.Both;
        public GameRowViewModel? SelectedRow { get; set; }
        public AppSettings Settings { get; } = new();
        public List<string> Navigated { get; } = new();
        public int Scaffolds, Reopens;
        public List<string> Logged { get; } = new();
        public Action<string>? OnNavigate;
        public void ClearGridSelection() => SelectedRow = null;
    public void Navigate(string view) { Navigated.Add(view); OnNavigate?.Invoke(view); }
        public void ScaffoldChosenFolder() => Scaffolds++;
        public void Log(string message) => Logged.Add(message);
        public void ReopenWelcome() => Reopens++;
    }

    private static GameFile File(string name, long size)
        => new() { Kind = FileKind.Installer, Name = name, FileKey = name, Os = "windows", State = FileState.NotBackedUp, ExpectedSizeBytes = size };

    private static GameRowViewModel Row(long id, string title, params GameFile[] files)
    {
        var it = new LibraryItem { GogId = id, Title = title, Slug = $"g{id}" };
        it.Files.AddRange(files);
        return GameRowViewModel.FromItem(it);
    }

    [Test] void StartLandsOnTheFirstStopAndTellsTheHost()
    {
        var host = new FakeHost();
        var vm = new GuideViewModel(host);
        int changed = 0; vm.Changed += () => changed++;

        vm.StartGuideCommand.Execute(null);

        Assert.True(vm.GuideActive, "Start turns the guide on");
        Assert.Equal("nav-accounts", vm.CurrentStepKey, "a fresh run begins at stop one");
        // Once for GuideActive false->true, once from the step evaluation: both host raises the old partial
        // did (OnGuideActiveChanged + RaiseGuide) now arrive through this one event.
        Assert.Equal(2, changed, "Changed fires for the active flip AND the step evaluation");
        Assert.False(host.Settings.FirstRunGuideDone, "a re-run clears the done flag");
        Assert.Equal(0, host.Settings.GuidePosition, "position persisted at the start");
    }

    [Test] void NextOnAGatedStopIsRefusedUntilTheFactIsTrue()
    {
        var host = new FakeHost();
        var vm = new GuideViewModel(host);
        vm.StartGuideCommand.Execute(null);

        Assert.False(vm.ShowGuideNext, "gate stop hides Next while unmet");
        Assert.False(vm.CanGuideNext, "and cannot advance");
        vm.GuideNextCommand.Execute(null);
        Assert.Equal("nav-accounts", vm.CurrentStepKey, "Next on an unmet gate does not move the cursor");
        Assert.Empty(host.Navigated, "and does not navigate");

        host.IsConnected = true;
        vm.Refresh();
        // HasAccount completes nav-accounts AND add-account; nav-folders waits on the Storage page.
        Assert.Equal("nav-folders", vm.CurrentStepKey, "state carries the tour past both account stops");
        Assert.Equal(2, host.Settings.GuidePosition, "the advance was persisted");
    }

    [Test] void BackOffTheFrontReopensWelcomeAndPauses()
    {
        var host = new FakeHost();
        var vm = new GuideViewModel(host);
        vm.StartGuideCommand.Execute(null);
        int changed = 0; vm.Changed += () => changed++;

        Assert.True(vm.CanGuideBack, "Back is always offered while active");
        vm.GuideBackCommand.Execute(null);

        Assert.False(vm.GuideActive, "the guide pauses: welcome scrim and guide never overlap");
        Assert.True(vm.WelcomeReopenedFromGuide, "flag set so Show-me-around RESUMES rather than resets");
        Assert.Equal(1, host.Reopens, "host asked to show the Welcome card exactly once");
        Assert.Equal(1, changed, "the active flip reached the host");
        Assert.Empty(host.Navigated, "no page change on a welcome reopen");
    }

    [Test] void ExitMarksDoneScaffoldsAndLogs()
    {
        var host = new FakeHost();
        var vm = new GuideViewModel(host);
        vm.StartGuideCommand.Execute(null);
        vm.DetailKeep = Row(1, "Kept", File("a.bin", 10));

        vm.ExitGuideCommand.Execute(null);

        Assert.False(vm.GuideActive, "off");
        Assert.True(host.Settings.FirstRunGuideDone, "Exit settles the tour as done");
        Assert.Equal(1, host.Scaffolds, "leaving the tour settles the folder question by default");
        Assert.Null(vm.DetailKeep, "the detail pane goes back to its normal fallback");
        Assert.Equal("Setup guide closed. Reopen it any time from Settings.", Assert.Single(host.Logged), "logged once");
    }

    [Test] void BackUpStepPicksTheSmallestFileAndReleasesItOnLeaving()
    {
        var host = new FakeHost { IsConnected = true, TotalCount = 2, CurrentViewName = "Library" };
        host.Rows.Add(Row(1, "Anchorhead", File("setup.exe", 5_000)));
        host.Rows.Add(Row(2, "Beneath", File("setup-1.bin", 2_000), File("setup-2.bin", 9_000)));
        var vm = new GuideViewModel(host) { GuideStepOverride = "back-up" };   // pin: nav-folders would otherwise hold the cursor

        vm.StartGuideCommand.Execute(null);

        Assert.Equal("back-up", vm.CurrentStepKey, "pinned");
        Assert.Equal((2L, "setup-1.bin"), vm.GuideFilePick!.Value, "smallest in-scope game file via host.Games");
        Assert.NotNull(host.SelectedRow, "a row is selected");
        Assert.Equal(2L, host.SelectedRow!.GogId, "the pick's game is selected on the host");

        // Optional stop: Next acks it and lands on back-up-all (same page, so no Navigate).
        vm.GuideNextCommand.Execute(null);
        Assert.Equal("back-up-all", vm.CurrentStepKey, "advanced");
        Assert.Null(vm.GuideFilePick, "the pick is released once the stop is behind us");
        Assert.Null(host.SelectedRow, "and the selection it made is released with it");
        Assert.Empty(host.Navigated, "already on Library: no navigation");
        Assert.True(vm.IsAcked("back-up"), "acked");
    }

    [Test] void CaptionCentersWhenTheStopDeclaresNoTarget()
    {
        var host = new FakeHost();
        var vm = new GuideViewModel(host) { GuideStepOverride = "farewell" };
        vm.StartGuideCommand.Execute(null);
        vm.GuideViewportW = 1000; vm.GuideViewportH = 800;
        vm.GuideCaptionW = 400;   vm.GuideCaptionH = 200;

        Assert.False(vm.GuideHasTarget, "no target declared");
        Assert.Equal(new Thickness(300, 300, 0, 0), vm.GuideCaptionMargin, "sign-off centers like the Welcome card");

        // Contrast: a stop that HAS a target but failed to resolve this frame holds its last position.
        var held = new GuideViewModel(new FakeHost()) { GuideStepOverride = "nav-accounts" };
        held.StartGuideCommand.Execute(null);
        held.GuideViewportW = 1000; held.GuideViewportH = 800;
        Assert.Equal(new Thickness(12, 12, 0, 0), held.GuideCaptionMargin, "unresolved target keeps the seed position, no leap to center");
    }

    [Test] void ResumeOpensThePageTheStopLivesOn()
    {
        // A launch mid-tour starts on the Overview; the persisted stop is the back-up step, which lives on
        // the Library page. Resume must take the window there, or the card floats with nothing to ring.
        var host = new FakeHost { IsConnected = true, TotalCount = 1, CurrentViewName = "Overview" };
        host.Rows.Add(Row(1, "Anchorhead", File("setup.exe", 5_000)));
        var first = new GuideViewModel(host) { GuideStepOverride = "back-up" };
        first.StartGuideCommand.Execute(null);   // pins and persists position + acks
        Assert.Equal(7, host.Settings.GuidePosition, "back-up is stop 8, persisted");
        host.Navigated.Clear();

        var resumed = new GuideViewModel(host);   // no pin: reads the persisted position, as a launch does
        resumed.Resume();
        Assert.True(resumed.GuideActive, "on");
        Assert.Equal("back-up", resumed.CurrentStepKey, "resumed at the persisted stop");
        Assert.Equal("Library", Assert.Single(host.Navigated), "and navigated to the stop's page");
    }

    [Test] void BackUpStepLandsOnlyWhenThePickedFileIsBackedUp()
    {
        // Other files already on disk (earlier runs) must not make the stop say "that file is backed up".
        var host = new FakeHost { IsConnected = true, TotalCount = 1, CurrentViewName = "Library", HasBackedUpAnything = true };
        host.Rows.Add(Row(2, "Beneath", File("setup-1.bin", 2_000)));
        var vm = new GuideViewModel(host) { GuideStepOverride = "back-up" };
        vm.StartGuideCommand.Execute(null);
        Assert.Equal("GuideFileDownload", vm.GuideTargetName, "asks for the arrow while the picked file is not on disk");
        Assert.True(vm.GuideCaption.StartsWith("Click the arrow"), vm.GuideCaption);

        host.BackedUp.Add((2L, "setup-1.bin"));
        vm.Refresh();
        Assert.Equal("GuideFileDone", vm.GuideTargetName, "the tick, once THAT file landed");
        Assert.True(vm.GuideCaption.StartsWith("That file is backed up"), vm.GuideCaption);
    }

    [Test] void StateAdvancingTheCursorAlsoOpensTheStopsPage()
    {
        // The fifth transition: not Back, Next, Start or Resume but plain state change. Cursor on the scan
        // stop, the user wanders to Overview, the scan completes and the summary is dismissed there: the
        // cursor lands on back-up (a Library stop) and the window must follow it to the Library.
        var host = new FakeHost { IsConnected = true, TotalCount = 0, CurrentViewName = "Overview" };
        host.OnNavigate = v => host.CurrentViewName = v;
        var vm = new GuideViewModel(host);
        vm.StartGuideCommand.Execute(null);
        host.CurrentViewName = "Drives"; vm.Refresh();          // rail click: Storage
        vm.GuideNextCommand.Execute(null);                       // accept the default folder -> Open [Library]
        host.CurrentViewName = "Library"; vm.Refresh();         // rail click: Library -> scan stop
        host.IsScanning = true; vm.Refresh();
        Assert.Equal("scan", vm.CurrentStepKey, "scanning: on the scan stop");
        host.CurrentViewName = "Overview";           // the user clicked away mid-scan
        host.Navigated.Clear();
        host.IsScanning = false; host.TotalCount = 3; host.ShowingItemCount = true; vm.Refresh();
        Assert.Equal("item-count", vm.CurrentStepKey, "summary up: holds on the summary stop");
        host.Rows.Add(Row(1, "Anchorhead", File("setup.exe", 5_000)));
        host.ShowingItemCount = false; vm.Refresh();
        Assert.Equal("back-up", vm.CurrentStepKey, "summary dismissed: state moved the cursor on");
        Assert.Equal("Library", host.Navigated.LastOrDefault(), "and the window followed the cursor to the Library");
        Assert.NotNull(vm.GuideFilePick, "with the demo file picked, so the arrow can be ringed");
    }

    [Test] void EveryTransitionKeepsTheInvariants()
    {
        // A scripted walk over every transition, checking the invariants the design promises after each one:
        // position within reach, a relevant stop under the cursor, the page matching the stop, persistence in
        // step, no pick without an active tour, Next refused when it cannot skip.
        var host = new FakeHost { CurrentViewName = "Overview" };
        var vm = new GuideViewModel(host);
        void Check(string when)
        {
            var s = vm.SnapshotForTests();
            int at = vm.SessionForTests.Position, reached = vm.SessionForTests.Reached;
            Assert.True(0 <= at && at <= reached && reached < Grog.Core.Guide.FirstRunTour.Nodes.Count, $"{when}: 0 <= {at} <= {reached}");
            var node = Grog.Core.Guide.FirstRunTour.Nodes[at];
            Assert.True(node.Relevant?.Invoke(s) != false, $"{when}: cursor on an irrelevant stop '{node.Key}'");
            if (vm.GuideActive)
            {
                var want = node.DestinationFor?.Invoke(s) ?? node.RequiresView;
                if (!string.IsNullOrEmpty(want)) Assert.Equal(want, host.CurrentViewName, $"{when}: stop '{node.Key}' lives on {want}, window on {host.CurrentViewName}");
                Assert.Equal(at, host.Settings.GuidePosition, $"{when}: persisted position mirrors the cursor");
            }
            else Assert.Null(vm.GuideFilePick, $"{when}: no pick without a tour");
            if (vm.GuideActive && !vm.CanGuideNext)
            {
                int before = at; vm.GuideNextCommand.Execute(null);
                Assert.Equal(before, vm.SessionForTests.Position, $"{when}: Next refused when it cannot skip");
            }
        }
        // The window follows the guide's navigations AND re-raises state, exactly as the real Navigate does
        // (Navigate -> RaiseState -> Guide.Refresh): this is what exercises the re-entrant reconcile.
        host.OnNavigate = v => { host.CurrentViewName = v; vm.Refresh(); };

        vm.StartGuideCommand.Execute(null); Check("start");
        host.IsConnected = true; vm.Refresh(); Check("connected");
        host.CurrentViewName = "Drives"; vm.Refresh(); Check("on storage");
        vm.GuideNextCommand.Execute(null); Check("accepted folder");
        host.CurrentViewName = "Library"; vm.Refresh(); Check("on library");
        host.IsScanning = true; vm.Refresh(); Check("scanning");
        host.IsScanning = false; host.TotalCount = 2; host.Rows.Add(Row(1, "A", File("a.exe", 10))); host.ShowingItemCount = true; vm.Refresh(); Check("summary");
        host.ShowingItemCount = false; vm.Refresh(); Check("back-up");
        for (int i = 0; i < 3; i++) { vm.GuideBackCommand.Execute(null); Check($"back {i}"); Assert.True(vm.GuideActive, $"back {i}: still on the tour"); }
        for (int i = 0; i < 6; i++) { if (vm.CanGuideNext) vm.GuideNextCommand.Execute(null); Check($"next {i}"); }
        vm.ExitGuideCommand.Execute(null); Check("exit");
        vm = new GuideViewModel(host); host.OnNavigate = v => { host.CurrentViewName = v; vm.Refresh(); };
        host.CurrentViewName = "Overview";
        vm.Resume(); Check("resume");
        // Check("resume") already proved page == stop page when active; a tour that completed does not resume.
        Assert.True(!vm.GuideActive == vm.SessionForTests.IsComplete, "resume is on unless the tour was completed");
    }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Guide;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

/// <summary>
/// The tour is an ordered list walked by a cursor, so these tests are about the LIST and the WALK, not
/// about deriving a position from app state. The old guide recomputed its step on every render, which is
/// why forward worked and backward did not: there was nothing to recompute backwards to. Position is held
/// now, and the tests that matter are the symmetry ones.
/// </summary>
[Trait("guide")]
public sealed class FirstRunTourTests
{
    // folder/sawFile are accepted and ignored: neither was ever read by a stop (removed from GuideState 09-02).
    private static GuideState S(string view = "Overview", bool acct = false, bool folder = false,
                               bool lib = false, bool backed = false, bool scanning = false,
                               bool runStarted = false, bool sawFile = false, bool itemCount = false,
                               bool scanRan = false, bool countSeen = false,
                               IReadOnlySet<string>? acked = null, bool firstFile = false, bool cloud = false,
                               bool demo = true)
        => new(acct, lib, backed, view, scanning, runStarted, itemCount,
               scanRan, countSeen, acked, firstFile, cloud, demo);

    private static GuideSession Fresh() => new();

    // ---- the list ----

    [Test]
    void The_rails_appear_in_order_each_preceded_by_its_click()
    {
        // Owner-stated shape: Account, Folders, Library, Overview, Cloud Saves, Settings, and every page is
        // reached by clicking its rail item first. The pair is what a user experiences as one step.
        var pages = new[]
        {
            ("nav-accounts", "add-account", "Accounts"),
            ("nav-folders",  "folder-default", "Drives"),
            ("nav-library",  "scan", "Library"),
            ("nav-overview", "setup-done", "Overview"),
            ("nav-cloud",    "cloud-look", "CloudSaves"),
            ("nav-settings", "settings-look", "Settings"),
        };

        int last = -1;
        foreach (var (nav, page, view) in pages)
        {
            int i = FirstRunTour.IndexOf(nav), j = FirstRunTour.IndexOf(page);
            Assert.True(i >= 0 && j >= 0, $"{nav} and {page} both exist");
            Assert.True(i > last, $"{nav} comes after the previous rail's stops");
            Assert.True(j > i, $"{page} comes after the click that opens it");
            Assert.Equal(view, FirstRunTour.Find(page)!.Value.RequiresView, $"{page} lives on {view}");
            // The rail item is reachable from anywhere, so its stop must not demand a page of its own.
            Assert.True(string.IsNullOrEmpty(FirstRunTour.Find(nav)!.Value.RequiresView),
                $"{nav} is a rail click, so it works on any page");
            last = j;
        }
    }

    [Test]
    void A_rail_click_and_the_page_it_opens_share_one_number()
    {
        // Otherwise the counter counts clicks, and "Step 11 of 15" describes plumbing rather than progress.
        Assert.Equal(6, FirstRunTour.Total, "six numbered milestones, one per rail");
        foreach (var (nav, page) in new[] { ("nav-accounts", "add-account"), ("nav-folders", "folder-default"),
                                            ("nav-library", "scan"), ("nav-overview", "setup-done"),
                                            ("nav-cloud", "cloud-look"), ("nav-settings", "settings-look") })
            Assert.Equal(FirstRunTour.NumberOf(nav), FirstRunTour.NumberOf(page),
                $"{nav} and {page} are two halves of one step");

        // Backing up belongs to the LIBRARY milestone, not to Overview: it happens on the Library page, and
        // the rail order is Account, Folders, Library, Overview, Cloud Saves, Settings.
        Assert.Equal(FirstRunTour.NumberOf("nav-library"), FirstRunTour.NumberOf("back-up"),
            "the first backup is part of the Library step");

        // Only the sign-off reports rather than asks, so it alone carries a title and no number.
        Assert.True(!FirstRunTour.IsCounted("farewell"), "the sign-off is not a numbered step");
    }

    // ---- the walk ----

    [Test]
    void Back_then_next_returns_to_the_same_stop()
    {
        // The symmetry the old model could not manage. Nothing about this should depend on app state.
        var g = Fresh();
        var s = S(view: "Accounts", acct: true);
        g.Sync(s);
        var start = g.Current(s).Key;

        g.Back(s);
        Assert.True(g.Current(s).Key != start, "Back actually moved");
        g.Next(s);
        Assert.Equal(start, g.Current(s).Key, "and Next came back to where we were");
    }

    [Test]
    void Back_passes_over_transition_stops()
    {
        // Rail clicks are pure transitions and the summary's target lives in a dismissed modal, so Back
        // lands only on stops where something real is on screen (owner decision 2026-08-26). The full
        // backward walk from the sign-off:
        var g = Fresh();
        var s = S(view: "Settings", acct: true, folder: true, lib: true, backed: true, sawFile: true,
                  scanRan: true, countSeen: true);
        g.Sync(s);
        g.GoTo("farewell");

        var expected = new[] { "settings-look", "cloud-look", "setup-done", "back-up-all",
                               "back-up", "scan", "folder-default", "add-account" };
        foreach (var want in expected)
        {
            g.Back(s);
            Assert.Equal(want, g.Current(s).Key, $"Back lands on {want}, skipping transitions");
        }
        Assert.True(!g.CanBack,
            "the sign-in card is the first real stop; its own rail click is not offered as Back");
    }

    [Test]
    void Revisiting_the_scan_stop_reads_as_a_revisit()
    {
        // Coming back, the scan is long done and the button no longer says Scan, so the original ask
        // would be stale next to it.
        var g = Fresh();
        g.GoTo("back-up");
        g.Back(S(view: "Library", acct: true, lib: true));
        var revisit = S(view: "Library", acct: true, lib: true);
        Assert.Equal("scan", g.Current(revisit).Key, "landed on the scan stop");
        Assert.True(g.Current(revisit).Caption.Contains("already been scanned"),
            "and it acknowledges the scan instead of asking for it again");
    }

    [Test]
    void Back_walks_one_stop_at_a_time_all_the_way_to_the_start()
    {
        var g = Fresh();
        var s = S(view: "Settings", acct: true, folder: true, lib: true, backed: true, sawFile: true);
        g.Sync(s);

        int guard = FirstRunTour.Nodes.Count + 5;
        while (g.CanBack && guard-- > 0) g.Back(s);
        // The first REAL stop, not literal index 0: rail clicks are passed over going backwards, and the
        // tour opens with one.
        Assert.Equal("add-account", g.Current(s).Key, "Back reaches the first non-transition stop");
        Assert.True(!g.CanBack, "and stops there rather than running off the front");
    }

    [Test]
    void State_advances_the_cursor_but_never_drags_it_back()
    {
        // A user who connects an account before being asked should skip that stop, not be pulled to it.
        var g = Fresh();
        var done = S(view: "Drives", acct: true);
        g.Sync(done);
        Assert.True(FirstRunTour.IndexOf(g.Current(done).Key) >= FirstRunTour.IndexOf("nav-folders"),
            "an account that already exists carries the cursor past the login stops");

        // Now a state that makes an EARLIER stop look undone must not rewind anything.
        var here = FirstRunTour.IndexOf(g.Current(done).Key);
        g.Sync(S(view: "Drives"));
        Assert.True(FirstRunTour.IndexOf(g.Current(done).Key) >= here, "state never moves the cursor backwards");
    }

    [Test]
    void Reading_an_earlier_stop_is_not_interrupted_by_progress_behind_you()
    {
        // The whole point of holding two positions: the tour may advance while the user re-reads something.
        var g = Fresh();
        var s = S(view: "Accounts", acct: true);
        g.Sync(s);
        g.Back(s);
        var reading = g.Current(s).Key;

        g.Sync(S(view: "Library", acct: true, folder: true, lib: true));
        Assert.Equal(reading, g.Current(s).Key, "the card under the user's eyes stayed put");
    }

    [Test]
    void Moving_the_cursor_hands_back_the_page_that_stop_lives_on()
    {
        // This is what keeps Back honest. The old code synced the page only when walking history forward, so
        // going back left a past caption sitting over the present page.
        var g = Fresh();
        var s = S(view: "Overview", acct: true);
        g.Sync(s);
        while (g.Current(s).Key != "folder-default" && g.CanNext) g.Next(s);

        var move = g.Back(s);
        var node = FirstRunTour.Find(g.Current(s).Key)!.Value;
        if (!string.IsNullOrEmpty(node.RequiresView))
            Assert.Equal(node.RequiresView, move.NavigateTo, "Back navigates to the stop's own page");
    }

    [Test]
    void A_required_stop_cannot_be_skipped_but_an_optional_one_can()
    {
        var g = Fresh();
        // Start ELSEWHERE: the rail stop completes on arrival, so opening on Accounts would legitimately
        // skip it and there would be nothing to test.
        var s = S(view: "Overview");                     // no account yet
        g.Sync(s);
        Assert.Equal("nav-accounts", g.Current(s).Key, "starts at the first rail click");
        g.Next(s);
        Assert.Equal("add-account", g.Current(s).Key, "reaching the page is the click's whole job");

        var onPage = S(view: "Accounts");                // arrived, still not signed in
        Assert.True(!g.CanSkip(onPage), "signing in is required, so Next cannot leave it");

        g.Sync(S(view: "Drives", acct: true));
        var s2 = S(view: "Drives", acct: true);
        Assert.True(g.CanSkip(s2), "the folder stop is optional: the default already works");
    }

    [Test]
    void The_scan_stop_narrates_while_it_runs_and_the_confirmation_changes_once_a_file_lands()
    {
        var g = Fresh();
        g.GoTo("scan");
        var busy = S(view: "Library", acct: true, scanning: true);
        Assert.True(g.Current(busy).Caption.Contains("Scanning"),
            "a spotlit button that now says Cancel must not be described as Scan");

        // The payoff is announced on the BACKUP stop, which is where the file actually lands. It used to sit
        // on the Overview stop, where it won every time (you always arrive there having just backed up) and
        // so buried that page's own description.
        g.GoTo("back-up");
        var waiting = S(view: "Library", acct: true, lib: true);
        // Landing is THE PICKED FILE arriving, not "anything is backed up": a library with files from earlier
        // runs must still ask for the arrow (owner 09-02).
        var otherFiles = S(view: "Library", acct: true, lib: true, backed: true);
        Assert.True(!g.Current(otherFiles).Caption.Contains("backed up"), "other files on disk do not land this stop");
        var landed = S(view: "Library", acct: true, lib: true, backed: true, firstFile: true);
        Assert.True(!g.Current(waiting).Caption.Contains("backed up"), "before it lands, the stop asks");
        Assert.True(g.Current(landed).Caption.Contains("backed up"), "after it lands, the stop confirms");

        // The target moves with the caption: finishing HIDES the download arrow, so a ring that only knew
        // the arrow had nothing to hold and the caption fell to a corner of the window.
        Assert.True(g.Current(waiting).TargetName != g.Current(landed).TargetName,
            "the ring moves from the arrow to the tick that replaces it");
    }

    [Test]
    void The_tour_is_complete_only_when_the_last_card_is_acknowledged()
    {
        // Completion used to be a state predicate, which is how a tour could end before showing its final
        // card. It is now exactly "the user pressed Done on the sign-off".
        var g = Fresh();
        var s = S(view: "Settings", acct: true, folder: true, lib: true, backed: true, sawFile: true);
        g.Sync(s);
        Assert.True(!g.IsComplete, "a fully set-up app has still not been shown the ending");

        int guard = FirstRunTour.Nodes.Count + 5;
        while (g.CanNext && guard-- > 0) g.Next(s);
        Assert.Equal("farewell", g.Current(s).Key, "the walk ends on the sign-off");
        g.Next(s);
        Assert.True(g.IsComplete, "and acknowledging it finishes the tour");
    }

    [Test]
    void The_tour_holds_on_the_scan_until_it_finishes_and_its_summary_is_dismissed()
    {
        // The exact sequence reported on 2026-08-25: the tour reached "Back Up Your First Game" while the
        // scan was still running, and the summary dialog then appeared behind an already-advanced tour.
        // Two causes, both here: items land progressively so the library is non-empty MID-scan, and "no
        // dialog on screen" is trivially true in the moment before the dialog appears.
        var g = Fresh();
        g.GoTo("scan");

        // Part way through: some items exist, scan still running.
        var midScan = S(view: "Library", acct: true, folder: true, lib: true, scanning: true, scanRan: true);
        g.Sync(midScan);
        Assert.Equal("scan", g.Current(midScan).Key, "a half-populated library is not a finished scan");

        // Finished, dialog has not appeared yet. This is the frame that used to leak.
        var gap = S(view: "Library", acct: true, folder: true, lib: true, scanRan: true);
        g.Sync(gap);
        Assert.Equal("item-count", g.Current(gap).Key, "the tour waits for the summary it knows is coming");

        // Dialog up.
        var showing = S(view: "Library", acct: true, folder: true, lib: true, itemCount: true,
                        scanRan: true, countSeen: true);
        g.Sync(showing);
        Assert.Equal("item-count", g.Current(showing).Key, "and holds while it is being read");

        // Dismissed.
        var dismissed = S(view: "Library", acct: true, folder: true, lib: true, scanRan: true, countSeen: true);
        g.Sync(dismissed);
        Assert.Equal("back-up", g.Current(dismissed).Key, "only then does the backup stop arrive");
    }

    [Test]
    void An_empty_account_walks_the_whole_tour_without_trapping_or_offering_backups()
    {
        // A GOG account with zero games: the scan finds nothing, so HasLibrary never becomes true. The scan
        // stop used to complete on HasLibrary and trapped the tour there forever with the scan already done;
        // it completes on the scan FINISHING now. The two backup stops are irrelevant with nothing to back
        // up and must be invisible in both directions.
        var g = Fresh();
        g.GoTo("scan");

        // Scan ran, finished, found nothing; summary shown and dismissed.
        var empty = S(view: "Library", acct: true, folder: true, scanRan: true, countSeen: true);
        g.Sync(empty);
        Assert.True(FirstRunTour.IndexOf(g.Current(empty).Key) > FirstRunTour.IndexOf("scan"),
            "a finished scan completes the stop even with zero items");
        Assert.True(g.Current(empty).Key != "back-up" && g.Current(empty).Key != "back-up-all",
            "no backup stops with nothing to back up");
        Assert.Equal("nav-overview", g.Current(empty).Key, "the tour moves on to Overview");
        Assert.True(g.Current(empty).Caption.Contains("activity and statistics"),
            "and the Overview ask drops 'confirm your backup', which does not exist");

        // Backwards from Overview: the backup stops stay invisible, landing on the scan revisit.
        g.Back(empty);
        Assert.Equal("scan", g.Current(empty).Key, "Back skips the irrelevant backup stops too");
    }

    [Test]
    void A_scan_that_stored_nothing_does_not_hold_the_tour_at_the_summary()
    {
        // Signed out, a GOG error, or an empty account: the scan ran but no summary ever opens (the app
        // toasts instead). ItemCountSeen stays false forever, and the tour used to wait at item-count.
        var g = Fresh();
        g.GoTo("scan");
        var nothing = S(view: "Library", acct: true, folder: true, scanRan: true);
        g.Sync(nothing);
        Assert.Equal("nav-overview", g.Current(nothing).Key, "no library, no summary: the tour moves on");

        // With a library it still waits: the summary WILL open for a scan that stored items.
        var g2 = Fresh();
        g2.GoTo("scan");
        var stored = S(view: "Library", acct: true, folder: true, lib: true, scanRan: true);
        g2.Sync(stored);
        Assert.Equal("item-count", g2.Current(stored).Key, "a stored scan holds for its summary");
    }

    [Test]
    void The_item_count_stop_disappears_when_its_dialog_never_shows()
    {
        // It exists to hold the tour while a modal is up. With no modal there is nothing to hold, so it must
        // not become a card the user has to dismiss.
        var g = Fresh();
        g.GoTo("scan");
        g.Sync(S(view: "Library", acct: true, folder: true, lib: true));
        Assert.True(g.Current(S(view: "Library")).Key != "item-count",
            "no dialog on screen means no summary stop");
    }

    [Test]
    void Retracing_forward_passes_over_an_already_satisfied_nav_stop()
    {
        var g = Fresh();
        g.GoTo("back-up");   // the live edge: everything before it is done
        var onLibrary = S(view: "Library", acct: true, folder: true, lib: true, scanRan: true);
        // Back twice: scan, then folder-default (nav-library is a pass-over stop going back).
        g.Back(onLibrary);
        var onFolders = S(view: "Drives", acct: true, folder: true, lib: true, scanRan: true);
        g.Back(onFolders);
        Assert.Equal("folder-default", g.Current(onFolders).Key, "reading the folder stop");

        // Forward again: "Open [Library]" is already satisfied (the library exists), so the cursor must not
        // park on it and wait for a click that changes nothing (owner 09-02); it lands on the scan stop.
        g.Next(onFolders);
        g.Sync(onLibrary);
        Assert.Equal("scan", g.Current(onLibrary).Key, "nav-library passed over when retracing forward");
        Assert.Contains("already been scanned", g.Current(onLibrary).Caption, "and the scan stop wears its revisit face");
    }

    [Test]
    void Clicking_the_page_a_retraced_nav_stop_asks_for_moves_the_cursor_on()
    {
        // The exact path the owner walked: Back to the folder stop, Next lands on "Open [Library]" because the
        // library is not known yet, then the rail click opens Library. Sync must step off the nav stop.
        var g = Fresh();
        g.GoTo("scan");
        var onFolders = S(view: "Drives", acct: true, folder: true, scanRan: true);
        g.Back(onFolders);
        Assert.Equal("folder-default", g.Current(onFolders).Key, "reading the folder stop");
        g.Next(onFolders);
        Assert.Equal("nav-library", g.Current(onFolders).Key, "nothing satisfied it yet, so it shows");

        var clickedLibrary = S(view: "Library", acct: true, folder: true, scanRan: true);
        g.Sync(clickedLibrary);
        Assert.Equal("scan", g.Current(clickedLibrary).Key, "opening the page it asked for moves the cursor, no Next needed");
    }

    [Test]
    void Every_move_lands_on_a_stop_whose_page_is_the_one_it_asked_to_open()
    {
        // The whole tour, back to the start and forward to the end, with the window following each Move exactly
        // as the app does. At every landing the stop's page and the current page must agree, or the card
        // draws over the wrong screen with nothing to ring (owner 09-02: "steps being mapped correctly").
        var g = Fresh();
        string view = "Settings";
        GuideState S2() => S(view: view, acct: true, folder: true, lib: true, backed: true, sawFile: true,
                             runStarted: true, scanRan: true, countSeen: true);
        g.GoTo("farewell");   // the acked optional stops behind it are the live edge's history
        Assert.Equal("farewell", g.Current(S2()).Key, "start at the sign-off");

        void Follow(GuideSession.Move m) { if (m.NavigateTo is { Length: > 0 } d) view = d; }
        void Check(string when)
        {
            var cur = g.Current(S2());
            if (!string.IsNullOrEmpty(cur.RequiresView))
                Assert.Equal(cur.RequiresView, view, $"{when}: stop '{cur.Key}' requires {cur.RequiresView}, window is on {view}");
        }
        int guard = 40;
        while (g.CanBack && guard-- > 0) { Follow(g.Back(S2())); Check("back"); }
        guard = 40;
        while (g.CanNext && guard-- > 0) { Follow(g.Next(S2())); Check("next"); }
        Assert.Equal("farewell", g.Current(S2()).Key, "and forward again reaches the sign-off");
    }

    // ---- the table: every stop's page, faces and targets in one place, so nobody reasons about them one lambda
    //      at a time. A new stop or a new face gets a row here, or this fails. ----

    [Test]
    void The_stop_table_is_what_the_code_does()
    {
        // (key, page, skipOnBack, target when asking, target when landed or null when it has no landed face)
        var table = new (string Key, string? Page, bool Skip, string Ask, string? Landed)[]
        {
            ("nav-accounts",  null,        true,  "GuideNavAccounts",   null),
            ("add-account",   "Accounts",  false, "GuideAddAccount",    null),
            ("nav-folders",   null,        true,  "GuideNavFolders",    null),
            ("folder-default","Drives",    false, "GuideChangeFolder",  null),
            ("nav-library",   null,        true,  "GuideNavLibrary",    null),
            ("scan",          "Library",   false, "GuidePrimaryAction", "GuideActionRow"),
            ("item-count",    null,        true,  "GuideItemCountOk",   null),
            ("back-up",       "Library",   false, "GuideFileDownload",  "GuideFileDone"),
            ("back-up-all",   "Library",   false, "GuidePrimaryAction", null),
            ("nav-overview",  null,        true,  "GuideNavOverview",   null),
            ("setup-done",    "Overview",  false, "GuideHealthCard",    null),
            ("nav-cloud",     null,        true,  "GuideNavCloud",      null),
            ("cloud-look",    "CloudSaves",false, "GuideCloudCheck",    null),
            ("nav-settings",  null,        true,  "GuideNavSettings",   null),
            ("settings-look", "Settings",  false, "GuideScheduleCard",  null),
            ("farewell",      null,        false, "",                   null),
        };
        Assert.Equal(FirstRunTour.Nodes.Count, table.Length, "one row per stop");
        for (int i = 0; i < table.Length; i++)
        {
            var (key, page, skip, ask, landed) = table[i];
            var n = FirstRunTour.Nodes[i];
            Assert.Equal(key, n.Key, $"row {i} order");
            Assert.Equal(page, string.IsNullOrEmpty(n.RequiresView) ? null : n.RequiresView, $"{key}: page");
            Assert.Equal(skip, n.SkipOnBack, $"{key}: pass-over stop");
            Assert.Equal(ask, n.TargetName, $"{key}: target when asking");
            Assert.Equal(landed, n.IsLanded is null ? null : (string.IsNullOrEmpty(n.DoneTarget) ? n.TargetName : n.DoneTarget), $"{key}: target when landed");
        }
    }

    [Test]
    void Faces_follow_the_fact_they_are_about()
    {
        var g = Fresh();
        // back-up: asks while the picked file is not on disk, even with other files backed up; lands on the pick.
        g.GoTo("back-up");
        var others = S(view: "Library", acct: true, lib: true, backed: true);
        Assert.Equal("GuideFileDownload", g.Current(others).TargetName, "other files on disk: still asks for the arrow");
        var pick = S(view: "Library", acct: true, lib: true, backed: true, firstFile: true);
        Assert.Equal("GuideFileDone", g.Current(pick).TargetName, "the pick on disk: the tick");
        // scan: busy while scanning, revisit face once the library exists, plain ask before either.
        g = Fresh(); g.GoTo("scan");
        Assert.Equal("GuidePrimaryAction", g.Current(S(view: "Library", acct: true)).TargetName, "asks for the scan");
        Assert.Contains("Scanning", g.Current(S(view: "Library", acct: true, scanning: true)).Caption, "busy face");
        Assert.Equal("GuideActionRow", g.Current(S(view: "Library", acct: true, lib: true)).TargetName, "revisit rings both buttons");
        // cloud-look: no saves known -> Check for New Saves; saves known -> Download all.
        g = Fresh(); g.GoTo("cloud-look");
        Assert.Equal("GuideCloudCheckNew", g.Current(S(view: "CloudSaves", acct: true, lib: true)).TargetName, "empty cloud list: ring the check");
        Assert.Equal("GuideCloudCheck", g.Current(S(view: "CloudSaves", acct: true, lib: true, cloud: true)).TargetName, "saves known: ring download all");
    }

    [Test]
    void A_satisfied_pass_over_stop_is_never_landed_on_from_either_direction()
    {
        // The property behind the 09-02 retrace bug, for EVERY pass-over stop, not the one that was reported.
        // A tour that reached the end has acked every stop it passed with Next (an Optional stop completes
        // only that way), so the state carries every key acked: what a real end-of-tour session holds.
        var acked = FirstRunTour.Nodes.Select(n => n.Key).ToHashSet();
        var all = S(view: "Settings", acct: true, lib: true, backed: true, runStarted: true, scanRan: true, countSeen: true, firstFile: true, acked: acked);
        for (int i = 0; i < FirstRunTour.Nodes.Count; i++)
        {
            if (!FirstRunTour.Nodes[i].SkipOnBack) continue;
            var g = Fresh(); g.GoTo("farewell");
            // Walk back to just before the pass-over stop, then forward one: it must be passed, never shown.
            int guard = 40;
            while (g.Position > 0 && g.Position >= i && guard-- > 0) g.Back(all);
            if (g.Position >= i) continue;   // nothing showable before it (index 0): Back cannot get there
            g.Next(all);
            Assert.NotEqual(i, g.Position, $"retrace forward landed on satisfied pass-over stop '{FirstRunTour.Nodes[i].Key}'");
            var h = Fresh(); h.GoTo("farewell"); guard = 40;
            while (h.CanBack && guard-- > 0) { h.Back(all); Assert.NotEqual(i, h.Position, $"Back landed on pass-over stop '{FirstRunTour.Nodes[i].Key}'"); }
        }
    }

    [Test]
    void With_nothing_to_demo_the_back_up_stop_is_skipped_not_shown_empty()
    {
        // Library fully backed up, or every remaining file already queued/partial: no arrow to ring.
        var g = Fresh();
        var noDemo = S(view: "Library", acct: true, lib: true, backed: true, scanRan: true, countSeen: true, demo: false);
        g.GoTo("item-count");   // the summary was just dismissed
        g.Sync(noDemo);
        Assert.NotEqual("back-up", g.Current(noDemo).Key, "skipped");
        Assert.Equal("back-up-all", g.Current(noDemo).Key, "lands on the whole-library choice instead");
    }
}

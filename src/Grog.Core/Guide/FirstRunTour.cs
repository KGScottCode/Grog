// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;

namespace Grog.Core.Guide;

/// <summary>
/// The tour: an ordered list of stops walked by a cursor (see <see cref="GuideSession"/>). Declaration
/// order IS the order the user sees, forwards and backwards.
///
/// <para>Rail order, and the shape of the list: Account, Folders, Library, Overview, Cloud Saves, Settings.
/// Each of those is a rail click followed by the page it opens, and the pair shares one number.</para>
/// </summary>
public static class FirstRunTour
{
    public static readonly IReadOnlyList<GuideNode> Nodes = new[]
    {
        // ---- 1. Account ----
        new GuideNode("nav-accounts", "Log In to GOG", 1,
            "Click [Account] to connect your GOG account.", "GuideNavAccounts",
            // Gate: nothing past the login means anything without an account, so Next is hidden rather
            // than shown disabled.
            Gate: true, SkipOnBack: true)
        // Also done when the account already EXISTS: a user who signed in before being asked must be carried
        // past this, not left staring at "click Account" with nothing to do.
        { IsDone = s => s.CurrentView == "Accounts" || s.HasAccount },

        new GuideNode("add-account", "Log In to GOG", 1,
            "Click to open a GOG login window.", "GuideAddAccount", RequiresView: "Accounts", Gate: true,
            DetailTitle: "Your sign-in is safe",
            // Both {protection} substitutions are adjectival phrases, so "that is" fits either one.
            Detail: "You log in on a GOG login page, so Grog never sees your password. GOG hands back a "
                  + "refresh token that is {protection}, and GOG replaces it every time it is used. Remove "
                  + "your account and it is deleted; your backups stay.{unlock}")
        { IsDone = s => s.HasAccount },

        // ---- 2. Folders ----
        new GuideNode("nav-folders", "Where Backups Go", 2,
            "Open [Storage] to see where your backups will go.", "GuideNavFolders", SkipOnBack: true)
        { IsDone = s => s.CurrentView == "Drives" || s.Ack("folder-default") },

        new GuideNode("folder-default", "Where Backups Go", 2,
            "This is where your backups will go. Change it here to use another drive, or click Next to keep "
          + "the default. (You can change this later.)",
            "GuideChangeFolder", RequiresView: "Drives", ScopeName: "GuidePrimaryFolder",
            // Optional: a default folder is already applied, so acknowledgement completes this, not existence.
            Optional: true,
            Detail: "Grog picked a default so it works out of the box. You can point it at any drive with "
                  + "room to spare.\n\n"
                  + "You can also add secondary storage later. That is useful for keeping a copy on an external "
                  + "drive, or for splitting a library that outgrows one disk."),

        // ---- 3. Library ----
        new GuideNode("nav-library", "Scan Your Library", 3,
            "Open [Library] to pull in your GOG catalog.", "GuideNavLibrary", SkipOnBack: true)
        { IsDone = s => s.CurrentView == "Library" || s.HasLibrary },

        new GuideNode("scan", "Scan Your Library", 3,
            "Nothing downloads yet; this just pulls the inventory of your GOG catalog.",
            "GuidePrimaryAction", RequiresView: "Library",
            // While the scan runs the spotlit button says "Cancel Scan", so narrate instead of pointing at a
            // control that now does the opposite.
            BusyCaption: "Scanning your GOG catalog. This can take a minute on a large library.",
            // The revisit face: coming BACK to this stop, the scan is long done and the button no longer says
            // Scan, so the ask would be stale. Never shows going forward -- the same condition advances the
            // cursor off this stop the moment it becomes true.
            DoneCaption: "Your library has already been scanned. Click Rescan GOG Library to scan it again, "
                       + "or Back up to start your full backup.",
            // The revisit names two buttons, so it rings the row that holds both and the caption parks clear.
            DoneTarget: "GuideActionRow")
        // Completes when the SCAN FINISHES, not when items exist: an empty account has zero items forever,
        // and gating on HasLibrary trapped the tour on this stop with the scan already done. HasLibrary
        // still completes it for a re-run where the library predates this tour. Not just !IsScanning
        // either: items land progressively, so a running scan must hold the stop.
        { IsDone = s => (s.HasLibrary || s.ScanRan) && !s.IsScanning, IsBusy = s => s.IsScanning,
          IsLanded = s => s.HasLibrary && !s.IsScanning },

        new GuideNode("item-count", "Scan Summary", 3,
            "This is what GOG says you own. Close it when you are ready.", "GuideItemCountOk",
            // Inside a modal that carries its own scrim, so the guide must not dim the dialog it is asking
            // the user to read, and the bubble hangs below rather than beside.
            NoScrim: true, Below: true, AnchorName: "GuideItemCountCard", SkipOnBack: true)
        // Holds until the summary has been SEEN and dismissed. "No dialog on screen" alone was true in the
        // moment before it appeared, so the cursor walked past and the dialog arrived behind a tour that had
        // already moved on. When no scan ran this tour there will never be a summary, so it self-completes.
        // A scan that stored nothing (signed out, GOG error, empty account) may never raise the summary
        // either: with no library there is nothing to count, so it must not hold the tour.
        { IsDone = s => !s.ShowingItemCount && (!s.ScanRan || s.ItemCountSeen || !s.HasLibrary) },

        // Number 3: this is still the Library milestone. Optional with NO IsDone, so clicking the arrow does
        // not move the tour. Completing on RunStarted made the card jump to the rail the instant the download
        // began, which on a small file reads as a flash. One card, caption updated in place, and the user
        // presses Next when they are ready.
        // "File", not "Game": the demo is one file, and for a multi-part game that is not the whole thing.
        new GuideNode("back-up", "Back Up Your First Game File", 3,
            "Click the arrow to back up this one file. It is the smallest thing you own, so it should not "
          + "take long.",
            "GuideFileDownload", RequiresView: "Library", Optional: true,
            DoneCaption: "That file is backed up. It may have happened very quickly, depending on the file size.",
            DoneTarget: "GuideFileDone")
        // Irrelevant on an empty library: there is no file to pick, and the ring would have nothing to hold.
        { IsLanded = s => s.FirstFileBackedUp, Relevant = s => s.HasLibrary && s.HasDemoFile },

        // The other half of the choice: one file as a demonstration, or the whole library queued before
        // moving on. Optional, and still the Library milestone -- it is an alternative to the stop above,
        // not a further chore.
        new GuideNode("back-up-all", "Back Up Your Library", 3,
            "That was one file. If you want, this button queues the rest of your library now; it keeps "
          + "running in the background while you finish this tour.",
            "GuidePrimaryAction", RequiresView: "Library", Optional: true,
            Detail: "No pressure either way. You can start the full backup any time from this page, and "
                  + "stopping or pausing it later loses nothing.")
        { Relevant = s => s.HasLibrary },

        // ---- 4. Overview ----
        // No "setup is done" here any more: the tour carries on through Cloud Saves and Settings, so calling
        // this the end was a leftover from when it was.
        new GuideNode("nav-overview", "Confirming Your Initial Backup", 4,
            "Open [Overview] to confirm your initial backup.",
            "GuideNavOverview", SkipOnBack: true,
            // The empty-library face: with nothing scanned in there is no backup to confirm, but Overview
            // is still worth introducing as where activity and statistics will live.
            AltCaption: "Open [Overview] - this is where activity and statistics live.")
        { IsDone = s => s.CurrentView == "Overview" || s.Ack("setup-done"), IsAlt = s => !s.HasLibrary },

        new GuideNode("setup-done", "Confirming Your Initial Backup", 4,
            // Present tense, and no [Overview] highlight: the brackets are the guide's "click this" markup, so
            // highlighting the page the user is already on read as an instruction with nothing to follow.
            "This page shows activity in progress, key statistics, and the status of any scheduled "
          + "backups.\n\n"
          + "Queue and download additional files from the [Library] tab.",
            // No DoneCaption: the user always arrives here having just backed a file up, so a "there it is"
            // face would win every time and this page description would never be read. The backup stop
            // already announces the file; this stop's job is to introduce Overview.
            "GuideHealthCard", RequiresView: "Overview", Optional: true),

        // ---- 5. Cloud Saves ----
        new GuideNode("nav-cloud", "Cloud Saves", 5,
            "In [Cloud Saves], you can download any game saves that were synced to GOG's Servers.",
            "GuideNavCloud", SkipOnBack: true)
        { IsDone = s => s.CurrentView == "CloudSaves" || s.Ack("cloud-look") },

        new GuideNode("cloud-look", "Cloud Saves", 5,
            "If you launch your games via GOG Galaxy or another supported launcher, your saved games are "
          + "synced to GOG's Servers. Here you can download local copies of those saves.",
            "GuideCloudCheck", RequiresView: "CloudSaves", Optional: true,
            // With no cloud saves known yet the Download-all button is not on the page (its header hides with
            // the empty list), so the stop rings Check for New Saves and says what it does instead.
            AltCaption: "If you launch your games via GOG Galaxy or another supported launcher, your saved games "
                      + "are synced to GOG's Servers. Click Check for New Saves to see what GOG holds for you; "
                      + "local copies can be downloaded from here.",
            AltTarget: "GuideCloudCheckNew",
            Detail: "Save them in the default location, or use Save As to put them in the game's own save "
                  + "folder.\n\n"
                  + "NOTE: If you save files directly to the game's saves location, be careful if you are "
                  + "prompted to overwrite existing save files. Grog can't detect or prevent that.")
        { IsAlt = s => !s.HasCloudSaves },

        // ---- 6. Settings ----
        // The rail card gets you through the door; the page card, which RINGS the schedule card, carries the
        // schedule sentence -- content sits beside the control it describes, not two clicks before it.
        new GuideNode("nav-settings", "Settings", 6,
            "Last stop. Open [Settings] to choose what gets backed up and how Grog behaves.",
            "GuideNavSettings", SkipOnBack: true)
        { IsDone = s => s.CurrentView == "Settings" || s.Ack("settings-look") },

        new GuideNode("settings-look", "Settings", 6,
            "Adjust how Grog looks and works, and what's included in your backups. Highlighted here: the "
          + "backup schedule - set one up and Grog runs it automatically while open or minimized to the tray.",
            "GuideScheduleCard", RequiresView: "Settings", Optional: true),

        // ---- The sign-off ----
        // NO target: the tour is over, so there is nothing left to point at. Every other stop rings a control
        // because it is asking for something; ringing the rail here would read as one more instruction.
        new GuideNode("farewell", "You're All Set", 0,
            "And that's it. Your library is cataloged, your first backup is on disk, and Grog will keep the "
          + "rest in step whenever you ask it to.\n\n"
          + "Good luck!",
            "", Optional: true,
            Detail: "Guides, the CLI reference and release notes live at getgrog.org. You can replay this "
                  + "tour any time from Settings.")
        // The card is centered with nothing highlighted, but the PAGE behind it is chosen for what comes
        // next: a full backup still running lands on Overview to watch it; otherwise Library, where the
        // next action most likely is (owner, 2026-08-26).
        { DestinationFor = s => s.RunStarted ? "Overview" : "Library" },
    };

    /// <summary>How many numbered milestones the tour has: "Step 3 of 6". Distinct numbers, because a rail
    /// click and the page it opens share one.</summary>
    public static int Total => Nodes.Where(n => n.Number > 0).Select(n => n.Number).Distinct().Count();

    public static bool IsCounted(string key) => Find(key) is { Number: > 0 };

    public static int NumberOf(string key) => Find(key)?.Number ?? 0;

    public static GuideNode? Find(string key)
    {
        foreach (var n in Nodes) if (n.Key == key) return n;
        return null;
    }

    public static int IndexOf(string key)
    {
        for (int i = 0; i < Nodes.Count; i++) if (Nodes[i].Key == key) return i;
        return -1;
    }
}

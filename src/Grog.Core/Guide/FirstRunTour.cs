// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;

namespace Grog.Core.Guide;

public static class FirstRunTour
{
    public static readonly IReadOnlyList<GuideNode> Nodes = new[]
    {
        // ---- 1. Account ----
        new GuideNode("nav-accounts", "Log In to GOG", 1,
            "Click [Account] to connect your GOG account.", "GuideNavAccounts",
            Gate: true, SkipOnBack: true)
        { IsDone = s => s.CurrentView == "Accounts" || s.HasAccount },

        new GuideNode("add-account", "Log In to GOG", 1,
            "Click to open a GOG login window.", "GuideAddAccount", RequiresView: "Accounts", Gate: true,
            DetailTitle: "Your sign-in is safe",
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
            BusyCaption: "Scanning your GOG catalog. This can take a minute on a large library.",
            DoneCaption: "Your library has already been scanned. Click Rescan GOG Library to scan it again, "
                       + "or Back up to start your full backup.",
            DoneTarget: "GuideActionRow")

        { IsDone = s => (s.HasLibrary || s.ScanRan) && !s.IsScanning, IsBusy = s => s.IsScanning,
          IsLanded = s => s.HasLibrary && !s.IsScanning },

        new GuideNode("item-count", "Scan Summary", 3,
            "This is what GOG says you own. Close it when you are ready.", "GuideItemCountOk",
            NoScrim: true, Below: true, AnchorName: "GuideItemCountCard", SkipOnBack: true)
        { IsDone = s => !s.ShowingItemCount && (!s.ScanRan || s.ItemCountSeen || !s.HasLibrary) },

        new GuideNode("back-up", "Back Up Your First Game File", 3,
            "Click the arrow to back up this one file. It is the smallest thing you own, so it should not "
          + "take long.",
            "GuideFileDownload", RequiresView: "Library", Optional: true,
            DoneCaption: "That file is backed up. It may have happened very quickly, depending on the file size.",
            DoneTarget: "GuideFileDone")
        { IsLanded = s => s.FirstFileBackedUp, Relevant = s => s.HasLibrary && s.HasDemoFile },

        new GuideNode("back-up-all", "Back Up Your Library", 3,
            "That was one file. If you want, this button queues the rest of your library now; it keeps "
          + "running in the background while you finish this tour.",
            "GuidePrimaryAction", RequiresView: "Library", Optional: true,
            Detail: "No pressure either way. You can start the full backup any time from this page, and "
                  + "stopping or pausing it later loses nothing.")
        { Relevant = s => s.HasLibrary },

        // ---- 4. Overview ----
        new GuideNode("nav-overview", "Confirming Your Initial Backup", 4,
            "Open [Overview] to confirm your initial backup.",
            "GuideNavOverview", SkipOnBack: true,
            AltCaption: "Open [Overview] - this is where activity and statistics live.")
        { IsDone = s => s.CurrentView == "Overview" || s.Ack("setup-done"), IsAlt = s => !s.HasLibrary },

        new GuideNode("setup-done", "Confirming Your Initial Backup", 4,
            "This page shows activity in progress, key statistics, and the status of any scheduled "
          + "backups.\n\n"
          + "Queue and download additional files from the [Library] tab.",
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
        new GuideNode("nav-settings", "Settings", 6,
            "Last stop. Open [Settings] to choose what gets backed up and how Grog behaves.",
            "GuideNavSettings", SkipOnBack: true)
        { IsDone = s => s.CurrentView == "Settings" || s.Ack("settings-look") },

        new GuideNode("settings-look", "Settings", 6,
            "Adjust how Grog looks and works, and what's included in your backups. Highlighted here: the "
          + "backup schedule - set one up and Grog runs it automatically while open or minimized to the tray.",
            "GuideScheduleCard", RequiresView: "Settings", Optional: true),

        // ---- The sign-off ----
        new GuideNode("farewell", "You're All Set", 0,
            "And that's it. Your library is cataloged, your first backup is on disk, and Grog will keep the "
          + "rest in step whenever you ask it to.\n\n"
          + "Good luck!",
            "", Optional: true,
            Detail: "Guides, the CLI reference and release notes live at getgrog.org. You can replay this "
                  + "tour any time from Settings.")
        { DestinationFor = s => s.RunStarted ? "Overview" : "Library" },
    };

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

# Frequently Asked Questions

## Using Grog

### Does removing a game delete my files?

No. Removing a game stops tracking it; the files stay on disk. Grog deletes files only when you choose
**Delete** and confirm.

### Installed or portable?

Installed (Windows setup, macOS `.dmg`) keeps settings in your user profile. Portable (Windows ZIP, macOS
`.tar.gz`, Linux) keeps settings and, by default, backups beside the app, so the folder moves as one thing.
See the install guides: [Windows](install/Windows.md), [macOS](install/macOS.md), [Linux](install/Linux.md).

### What happens when GOG updates a game I already backed up?

The next scan flags it and queues the new version. With **Keep the version an update replaces** on, the
old installer moves to `Old Versions/`: GOG stops offering old builds, so that is the only way back.

### Does Grog need to be running for scheduled backups?

Yes; minimized or in the tray is enough (Settings, **When you close the window**: **Keep Running in
Tray**). For backups with no app running, schedule [`grogcli backup`](CLI.md) instead.

### Can I move my backups later?

Yes, on the Storage page. Moves copy, verify, then delete the original, so an interrupted move never
loses a file. A portable copy can also be moved as a whole folder, to any drive.

### Where do my extras go?

In each game's folder (`Games/<game>/Extras/`) by default. Settings, **Where Extras are stored**, can
put them in their own `Extras/` tree instead, sorted by game or by type, even on a second drive.

### More than one GOG account?

Yes. Each game shows who owns it, and shared purchases appear once.

### What do the red and orange rows mean?

Red: GOG no longer offers the game to any of your accounts, so guard that backup. Orange: the game's
account is signed out or removed; the backup is safe, but it can't be re-downloaded until you reconnect.

### Can backups go in OneDrive, Dropbox or iCloud Drive?

Better not: sync clients fight Grog's live writes. Back up to a local or external drive and copy from there.

### Does Grog work offline?

Checking files you already have works offline; scanning and downloading need GOG.

## When something looks wrong

### Windows or macOS won't open Grog

Grog is unsigned, so the first launch needs a confirmation. The install guides show how:
[Windows](install/Windows.md), [macOS](install/macOS.md).

### A drive disconnected during a backup

Downloads pause until it is back, then continue by themselves. Gone for good? **Mark as Lost** on the
Storage page, and its files are downloaded elsewhere.

### A folder shows Disconnected

Its drive is unplugged or asleep; nothing is lost. Mark a USB drive **Removable** on the Storage page and
Grog treats its absence as normal.

### My network share shows offline (Windows)

`\\server\share` paths aren't supported yet. Map the share to a drive letter instead.

### A file shows Unavailable

GOG refused to serve it to your account. It's not damage and doesn't count against you; a later scan
clears it if GOG offers it again.

### Downloads use my whole connection

Settings, **Limit download speed**. It applies at once, even to a running download.

### Linux says my sign-in isn't encrypted

No desktop keyring was found. Install `libsecret-tools` (and a keyring such as GNOME Keyring or KWallet),
then sign in again.

### A second copy won't open

One copy runs at a time; the running one comes to the front. Quit it to start the other.

### How do I report a bug?

[Open an issue](https://github.com/KGScottCode/Grog/issues). On the Overview, **(Details)** then **Copy**
under **Recent errors** gives the lines that help most. The log file is on the About page (**Open Log File**).

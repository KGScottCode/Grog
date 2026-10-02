# Grog v0.1.0

The first public release of Grog: back up your GOG.com library (installers, extras and cloud saves) to your own
drives, verified against GOG's checksums. A desktop app and a command-line tool for Windows, macOS and Linux.

> Unofficial; not affiliated with or endorsed by GOG.com. Grog uses the same API GOG's own clients use, signed in
> with your account.

## Highlights

### Back up everything you own
- **Back Up Now** queues everything missing or out of date.
- **Installers, extras, DLC and cloud saves**, per game and per account; installers verified by checksum.
- **Library dashboard**: backup status per file, per game and for the whole library.
- **What's new**: after a rescan, **Back Up New Now** queues just the new games and files.
- **Import** a folder of installers you already have.

### Keeps your backups safe
- **Fix Now** re-downloads a file that goes missing or corrupt; **Try Again** retries exactly what failed.
- **At-risk flags**: red for games GOG no longer sells, orange for games from a signed-out account.
- **Private**: you sign in on GOG's own page, tokens are encrypted on disk, and there is no telemetry.

### Handles the unexpected
- **Resumable downloads**, and **safe moves** between drives (copy, verify, then delete).
- **A drive disconnects**: downloads pause and continue when it's back. Gone for good? Mark it as lost.
- **Out of space**: what doesn't fit stays queued and is shown plainly.
- **App and `grogcli` never clash**: while the app is open, `grogcli` can't change the library.

### Works your way
- **Extras** with each game, or in their own folder tree, even on another drive.
- **Several drives**: the primary fills first, the rest go to the next.
- **Ratings, languages and an optional mature-content filter.**
- **Download speed limit.**
- **Command-line tool** for scheduled and unattended runs ([docs/CLI.md](https://github.com/KGScottCode/Grog/blob/master/docs/CLI.md)).

## Downloads

| Platform | File | Guide |
|---|---|---|
| Windows 10 / 11 | `Grog-win-x64-setup.exe` (installer, no admin), or `Grog-win-x64.zip` (portable) | [Windows](https://github.com/KGScottCode/Grog/blob/master/docs/install/Windows.md) |
| macOS 14+ | `Grog-macos-arm64.dmg` (Apple Silicon) or `Grog-macos-x64.dmg` (Intel); `.tar.gz` for portable | [macOS](https://github.com/KGScottCode/Grog/blob/master/docs/install/macOS.md) |
| Linux (x64) | `Grog-linux-x64.tar.gz` | [Linux](https://github.com/KGScottCode/Grog/blob/master/docs/install/Linux.md) |

Grog is unsigned, so Windows and macOS ask you to confirm the first launch; the guides show how. SHA-256
checksums are attached.

## Known issues

- **Windows 11 Smart App Control**, when on, blocks Grog from starting (it blocks all unsigned apps).
- **macOS:** a locked login Keychain makes Grog look signed out. Unlock it, or log out and back in.
- **Windows network paths** (`\\server\share`) can't be backup locations yet. Map a drive letter instead.

## Platform notes

- **Linux:** install `libsecret-tools` (or `libsecret`) so your sign-in is protected by the desktop keyring.
- **macOS:** notifications appear inside the app, not in Notification Center.

GPL-3.0-or-later.

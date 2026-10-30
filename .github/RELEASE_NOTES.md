## What's new in v0.1.4

- **Network storage** ([issue #3](https://github.com/KGScottCode/Grog/issues/3)): a backup folder on a NAS or share (`\\server\share`) showed 0 bytes free and every download failed with "No room to download". Fixed. The same audit turned up more drive-letter assumptions on shares, also fixed:
  - the per-file room check before each download was skipped on a share;
  - two backup folders on one share were counted as two drives, doubling its free space in the plan;
  - a move onto a share was not subtracted from its free space while it ran;
  - a share's cluster size and filesystem were unknown, so FAT32-backed shares had no 4 GB file limit;
  - network storage (a share or a mapped drive) now shows a NET chip on its Storage card, and the rail counts it as a network share.
- **Windows:** saving the sign-in token from two places at once (app and `grogcli`) no longer fails with "access denied"; a read-only `reorg.json` no longer leaves a read-only backup behind.
- **About:** shows build number and commit. gogrepoc / lgogdownloader credit updated (the CLI sign-in it described was removed in 0.1.2).

## What's new in v0.1.3

- **Patches** ([issue #2](https://github.com/KGScottCode/Grog/issues/2)): GOG's "Patch (a to b)" downloads are now recognised as their own file type, separate from Game and Mod. They are backed up by default; Settings > Content > **Include patches** turns them off, which drops them from the scope and the queue while leaving anything already downloaded on disk. The Library gains a **Patches** filter chip, patch files carry a matching chip in the product details, and chips for content you have excluded from backup are hidden rather than filtering to nothing. A rescan picks up the new type. CLI: `--skip-patches` per run, `scope set --patches|--no-patches` to persist, `scope show` lists it; `--installers-only` still includes patches, `--skip-installers` now skips them too.
- **Overview** ([issue #1](https://github.com/KGScottCode/Grog/issues/1)): the Library Coverage numbers no longer clip on four- and five-digit file counts; the columns size to the numbers.
- **Library selection:** after a rescan the Rescan button no longer stays on "Rescan Selected" with nothing selected; Esc clears a stuck selection.

## What's new in v0.1.2

- **Sign-in on Linux/Wayland:** detects a failed GTK login-window creation and falls back to X11 automatically.
- **CLI sign-in:** `grogcli login` now always signs you in on GOG's own page in your browser; the username/password prompt is gone. The app and the CLI both use GOG's page.
- **Logs and debug output:** links, ids and your home-directory path are redacted everywhere Grog writes: `grog.log`, `--debug`, `--json` errors and the diagnostic verbs. Logs are safe to attach to a bug report.
- **Update check:** always asks GitHub; the testing override is removed.
- **SECURITY.md** now describes exactly how sign-in works on each platform and when tokens are stored unencrypted.
- **Code:** doc-comment and markup-comment cleanup across the codebase.

## What's new in v0.1.1

- **macOS 26 icon:** Grog's icon now fills the shape like other apps (no grey tile); sized correctly on macOS 14-15 too.
- **Queue sizes:** rows show each file's real size once it's known, not GOG's rounded label, and the Size sort uses it.
- **Clearer status:** after closing Grog mid-backup, Overview says "paused when Grog last closed".
- **Smoother startup on macOS and Linux:** a drive check no longer runs on the window's thread.
- **Install guides:** macOS covers installing without admin rights; the portable uninstall steps now keep your backups.

## v0.1.0

First public release. What Grog does, and how to install it, is in the [README](https://github.com/KGScottCode/Grog#readme).

## Downloads

| Platform | File | Guide |
|---|---|---|
| Windows 10 / 11 | `Grog-win-x64-setup.exe` (installer, no admin), or `Grog-win-x64.zip` (portable) | [Windows](https://github.com/KGScottCode/Grog/blob/master/docs/install/Windows.md) |
| macOS 14+ | `Grog-macos-arm64.dmg` (Apple Silicon) or `Grog-macos-x64.dmg` (Intel); `.tar.gz` for portable | [macOS](https://github.com/KGScottCode/Grog/blob/master/docs/install/macOS.md) |
| Linux (x64) | `Grog-linux-x64.tar.gz` | [Linux](https://github.com/KGScottCode/Grog/blob/master/docs/install/Linux.md) |

Grog is unsigned, so Windows and macOS ask you to confirm the first launch; the guides show how. SHA-256
checksums are attached.

> Unofficial; not affiliated with or endorsed by GOG.com. GPL-3.0-or-later.

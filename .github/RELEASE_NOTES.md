## What's new in v0.1.3

- **Overview:** the Library Coverage numbers no longer clip on libraries with four- or five-digit file counts; the columns size to the numbers. (#1)
- **Include patches:** a toggle in Settings > Content > What to back up. Off, patch files leave the backup scope and the queue; anything already downloaded stays on disk. Default on. (#2)
- **Patches are now recognised:** GOG's "Patch (a to b)" installers are tagged as patches instead of counted as game installers. A rescan picks them up.
- **Patches chip:** the Library filter row has a terracotta Patches chip, and patch files carry a matching chip in the detail pane beside the OS badge.
- **Scope-aware chips:** filter chips for content you have excluded from backup (games, extras or patches) are hidden instead of filtering to nothing.
- **Library selection:** after a rescan the Rescan button no longer stays on "Rescan Selected" with nothing selected; Esc clears a stuck selection.
- **CLI:** `download`/`backup` take `--skip-patches`; `scope set` takes `--patches|--no-patches` and `scope show` lists patches. `--installers-only` still includes patches; `--skip-installers` now also skips them.

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

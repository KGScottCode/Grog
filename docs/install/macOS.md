# Installing Grog on macOS

macOS 14 (Sonoma) or later, Apple Silicon or Intel. Grog is not yet signed by Apple, so the first
launch needs one extra step. Prefer to build it yourself? See [Building from source](macOS-source.md).

## Install

**1. Download** from the [latest release](https://github.com/KGScottCode/Grog/releases/latest):
`Grog-macos-arm64.dmg` for Apple Silicon (M1 and later), `Grog-macos-x64.dmg` for Intel. Not sure
which? **Apple menu > About This Mac**: "Chip: Apple M…" is Apple Silicon.

**2. Install.** Open the `.dmg` and drag **Grog** onto **Applications**.

**3. Allow it to open.** Open Grog once and let macOS refuse ("damaged" or "cannot be opened"). Then
open **System Settings > Privacy & Security**, scroll down to the message about Grog, click
**Open Anyway** and confirm.

### Portable copy (no install)

Download `Grog-macos-arm64.tar.gz` (or `-x64`) instead, double-click it to extract, and open
`Grog.app` from wherever you keep it (allow it as in step 3). The app is the whole install: your
library, settings and sign-in live inside it.

## Updating

Quit Grog, download the new `.dmg`, and drag **Grog** onto **Applications** again; choose
**Replace**. Allow it to open as in step 3. Your library, settings and sign-in live in
`~/Library/Application Support/Grog` and are kept.

**Portable copy:** replacing `Grog.app` replaces its data too. Use the `.dmg` if you plan to update.

## The command-line tool

`grogcli` ships inside the app. To call it from any Terminal window:

```sh
APPS=/Applications; [ -d "$APPS/Grog.app" ] || APPS=~/Applications
grep -q 'alias grogcli=' ~/.zshrc 2>/dev/null || echo "alias grogcli=\"$APPS/Grog.app/Contents/MacOS/grogcli\"" >> ~/.zshrc
source ~/.zshrc
grogcli status
```

## Uninstalling

Quit Grog and drag it from **Applications** to the Trash.

To also remove your library, settings and sign-in:

```sh
rm -rf ~/Library/Application\ Support/Grog
security delete-generic-password -s Grog
sed -i '' '/alias grogcli=/d' ~/.zshrc
```

Backed-up games on your drives are never touched; delete those folders yourself if you want them gone.

## If something goes wrong

**No Open Anyway button**: clear the download mark in Terminal, then open Grog again:

```sh
xattr -dr com.apple.quarantine /Applications/Grog.app
```

**Can't drag to Applications** (no admin rights): drag Grog to the **Applications** folder in your
home folder instead, and use that path above.

**Signed out right after signing in**: your login keychain is locked. Unlock it, or log out of macOS and
back in.

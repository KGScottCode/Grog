# Building Grog from source on macOS

An alternative to the [download](macOS.md): build Grog on your own Mac. A self-built app opens without
the "Open Anyway" step. You don't need an Apple account or a developer certificate.

## Install

Needs macOS 14 (Sonoma) or later, Apple Silicon or Intel. Run each step in Terminal, top to bottom.

**1. Git** (the Xcode Command Line Tools). Skip this if `git --version` already prints a version.

```sh
xcode-select --install
```

This needs admin rights. Without them, skip it and use the no-Git line in step 3.

**2. .NET 10 SDK.** Skip this if `dotnet --version` already prints 10.x. Installs into your home folder;
no admin rights needed.

```sh
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 10.0 --install-dir ~/.dotnet
echo 'export PATH="$HOME/.dotnet:$PATH"' >> ~/.zshrc && export PATH="$HOME/.dotnet:$PATH"
dotnet --version
```

The last line must print 10.x.

**3. Get the source.**

```sh
cd ~ && git clone https://github.com/KGScottCode/Grog.git
```

Without Git:

```sh
cd ~ && curl -sSL https://github.com/KGScottCode/Grog/archive/refs/heads/master.tar.gz | tar xz && mv Grog-master Grog
```

Already have a `~/Grog` folder? Use [Updating](#updating) instead.

**4. Build.** Builds for this Mac, Apple Silicon or Intel.

```sh
cd ~/Grog
bash packaging/macos/build-app.sh
```

**5. Install and open.** Goes to Applications; without admin rights, to the Applications folder in your
home folder.

```sh
APPS=/Applications; [ -w "$APPS" ] || APPS=~/Applications
mkdir -p "$APPS" && rm -rf "$APPS/Grog.app" && cp -R dist/Grog.app "$APPS/"
open "$APPS/Grog.app"
```

## Updating

Quit Grog first. This replaces the installed app; there is only ever one copy.

```sh
cd ~/Grog && git pull
bash packaging/macos/build-app.sh
APPS=/Applications; [ -w "$APPS" ] || APPS=~/Applications
rm -rf "$APPS/Grog.app" && cp -R dist/Grog.app "$APPS/"
open "$APPS/Grog.app"
```

Without Git: replace the first line with
`cd ~ && rm -rf Grog && curl -sSL https://github.com/KGScottCode/Grog/archive/refs/heads/master.tar.gz | tar xz && mv Grog-master Grog && cd Grog`.

Your library, settings and sign-in live in `~/Library/Application Support/Grog` and are untouched by a
rebuild.

## The command-line tool

`grogcli` ships inside the app. To call it from any Terminal window:

```sh
APPS=/Applications; [ -w "$APPS" ] || APPS=~/Applications
grep -q 'alias grogcli=' ~/.zshrc 2>/dev/null || echo "alias grogcli=\"$APPS/Grog.app/Contents/MacOS/grogcli\"" >> ~/.zshrc
source ~/.zshrc
grogcli status
```

## Uninstalling

Quit Grog, then:

```sh
rm -rf /Applications/Grog.app ~/Applications/Grog.app ~/Grog
sed -i '' '/alias grogcli=/d' ~/.zshrc
```

To also remove your library, settings and sign-in:

```sh
rm -rf ~/Library/Application\ Support/Grog
security delete-generic-password -s Grog
```

Backed-up games on your drives are never touched; delete those folders yourself if you want them gone.

## If something goes wrong

**"dotnet: command not found"**: the SDK is not installed or not on `PATH`. Open a new terminal after
installing. Step 2 adds `~/.dotnet` to `PATH`; the admin installer adds `/usr/local/share/dotnet`.

**The build fails on a missing workload or runtime pack**: Grog needs .NET 10. `dotnet --version` must
print 10.x; if not, repeat step 2.

**"Grog is damaged and can't be opened"**: the source was downloaded in a browser, so macOS marked it.
Clear the mark, then repeat steps 4 and 5:

```sh
xattr -dr com.apple.quarantine ~/Grog
```

**Signed out right after signing in**: your login keychain is locked. Unlock it, or log out of macOS and
back in.

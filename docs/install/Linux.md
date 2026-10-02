# Installing Grog on Linux

Grog for Linux is a self-contained folder: it carries its own .NET runtime, so there is nothing else to
install. Tested on Linux Mint and Ubuntu; other x64 distributions should work the same.

## Install

Run each step in a terminal, top to bottom.

**1. Keyring support** (recommended). Lets Grog protect your GOG sign-in with your desktop keyring;
without it, the sign-in is kept in a file only your account can read.

```bash
sudo apt install libsecret-tools    # Ubuntu, Mint, Debian
sudo dnf install libsecret          # Fedora
sudo pacman -S libsecret            # Arch
```

**2. Download.**

```bash
mkdir -p ~/grog && cd ~/grog
wget -qO- https://github.com/KGScottCode/Grog/releases/latest/download/Grog-linux-x64.tar.gz | tar xz
```

Downloaded it in your browser instead? Replace the second line with
`tar -xzf ~/Downloads/Grog-linux-x64.tar.gz`.

**3. Add Grog to your app menu.**

```bash
mkdir -p ~/.local/share/applications
cat > ~/.local/share/applications/grog.desktop <<END
[Desktop Entry]
Type=Application
Name=Grog
Comment=GOG library backup
Exec=$HOME/grog/Grog
Icon=$HOME/grog/grog.png
Terminal=false
Categories=Utility;
END
```

**4. Open Grog** from your app menu, or:

```bash
~/grog/Grog
```

## Updating

Quit Grog first, then download over the old copy. Your library, settings and sign-in live in
`~/grog/GrogData` and are kept.

```bash
cd ~/grog
wget -qO- https://github.com/KGScottCode/Grog/releases/latest/download/Grog-linux-x64.tar.gz | tar xz
./Grog
```

## The command-line tool

`grogcli` ships beside the app. To call it from any terminal:

```bash
grep -q 'alias grogcli=' ~/.bashrc 2>/dev/null || echo "alias grogcli=\"$HOME/grog/grogcli\"" >> ~/.bashrc
source ~/.bashrc
grogcli status
```

## Uninstalling

Quit Grog, then:

```bash
rm -rf ~/grog ~/.local/share/applications/grog.desktop
sed -i '/alias grogcli=/d' ~/.bashrc
secret-tool clear service Grog 2>/dev/null
```

This also removes your library, settings and sign-in (they live in `~/grog/GrogData`). Backed-up games
on your drives are never touched; delete those folders yourself if you want them gone.

## If something goes wrong

**Grog does not start**: the files lost their "executable" mark on the way. Restore it:

```bash
chmod +x ~/grog/Grog ~/grog/grogcli
```

**Signed out right after signing in**: your desktop keyring is locked. Unlock it (your desktop usually
asks), or log out and back in.

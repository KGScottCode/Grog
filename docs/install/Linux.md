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

## Backing up to a network share / NAS

Mount the share as a normal folder first, then in Grog choose **Add a backup folder** and pick a folder
inside it.

Use a system mount, not your file manager's "Other Locations" / network browser: those connections
(`/run/user/.../gvfs/...`) are slow and unreliable for large files. For an SMB share:

```bash
sudo apt install cifs-utils              # Ubuntu, Mint, Debian (Fedora: dnf, Arch: pacman)
sudo mkdir -p /mnt/nas
printf 'username=YOUR_NAS_USER\npassword=YOUR_NAS_PASSWORD\n' > ~/.nas-credentials
chmod 600 ~/.nas-credentials
echo "//nas.local/games /mnt/nas cifs credentials=$HOME/.nas-credentials,uid=$(id -u),gid=$(id -g),_netdev,nofail 0 0" | sudo tee -a /etc/fstab
sudo mount /mnt/nas
```

Replace `nas.local/games` with your NAS and share name. The share then mounts at every start. An NFS share
works the same way with an `nfs` line in `/etc/fstab`.

Backups to a share are slower than to a local drive, and Grog shows the share as offline while the NAS is
asleep or unreachable; it carries on when the share answers again.

## Updating

Quit Grog first, then download over the old copy. Your library, settings and sign-in (`~/grog/GrogData`)
and any backups in `~/grog/GOG_Library` are kept.

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

Quit Grog first.

> **Your backups may be inside `~/grog`.** Unless you chose another folder, Grog's default backup folder is
> `~/grog/GOG_Library`, and the command below deletes it. Move it out first if you want to keep it:
> `mv ~/grog/GOG_Library ~/GOG_Library`

Then:

```bash
rm -rf ~/grog ~/.local/share/applications/grog.desktop
sed -i '/alias grogcli=/d' ~/.bashrc
secret-tool clear service Grog 2>/dev/null
```

This removes the app, your library, settings and sign-in (`~/grog/GrogData`) and anything else inside
`~/grog`. Backups in folders outside `~/grog` are not touched; delete those yourself if you want them gone.

## If something goes wrong

**Grog does not start**: the files lost their "executable" mark on the way. Restore it:

```bash
chmod +x ~/grog/Grog ~/grog/grogcli
```

**Signed out right after signing in**: your desktop keyring is locked. Unlock it (your desktop usually
asks), or log out and back in.

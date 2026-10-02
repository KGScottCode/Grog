# Installing Grog on Windows

Windows 10 or 11 (x64). Grog installs for your Windows account only, so no admin rights are needed.

## Install

**1. Download** `Grog-win-x64-setup.exe` from the
[latest release](https://github.com/KGScottCode/Grog/releases/latest) and run it.

**2. Allow it to run.** Windows shows "Windows protected your PC" for new apps that are not yet widely
downloaded. Click **More info**, then **Run anyway**.

**3. Follow the setup.** It adds Grog to the Start menu (and, unless you untick it, the desktop), then
opens Grog.

### Portable copy (no install)

To run a portable copy without installing:

1. Download `Grog-win-x64.zip` from the same release.
2. Right-click it, choose **Extract All**, and extract to a folder you own, such as `C:\Grog` or a USB
   drive. Not `Program Files`.
3. Open the folder and run `Grog.exe` (allow it as in step 2 above).

The folder is the whole install: your library, settings and sign-in live in its `GrogData` folder.

## Updating

Run the new `Grog-win-x64-setup.exe`. It closes Grog if it is open and replaces the app; your library,
settings and sign-in are kept.

**Portable copy:** quit Grog, then extract the new ZIP into the same folder and choose to replace the
files. `GrogData` is kept.

## The command-line tool

`grogcli` ships beside the app. To call it from any terminal, tick **Add grogcli to PATH** during setup
(run the setup again to add it later), then open a new terminal:

```powershell
grogcli status
```

**Portable copy:** right-click inside the Grog folder and choose **Open in Terminal** (Windows 10:
Shift+right-click, **Open PowerShell window here**), then run `.\grogcli status`.

## Uninstalling

Open **Settings > Apps > Installed apps** (Windows 10: **Apps & features**), find **Grog**, and choose
**Uninstall**.

The uninstaller then asks whether to also remove your library, settings and sign-in; the default is
No, so a reinstall picks up where you left off. Backed-up games on your drives are never touched; delete
those folders yourself if you want them gone.

**Portable copy:** delete its folder. That also removes its `GrogData`.

## If something goes wrong

**Only a "Don't run" button**: click **More info** first; **Run anyway** appears.

**Portable copy forgets its settings**: its folder is read-only to Grog (for example under
`Program Files`), so Grog saved them to your profile instead. Move the folder somewhere you own.

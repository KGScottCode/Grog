#!/usr/bin/env bash
# Grog — pull latest from GitHub, build, and launch the GUI.
# Backs the "Grog (Update & Run)" desktop shortcut; also fine to run directly.
# Repo root = one level up from this script (tools/ -> repo), so it works wherever the clone lives.
REPO="$(cd "$(dirname "$(readlink -f "$0")")/.." && pwd)"
cd "$REPO" || exit 1
pause() { echo; read -n1 -r -p "Press any key to close…"; echo; }
echo "==> Updating from GitHub…"
if ! git pull --ff-only; then echo "git pull failed."; pause; exit 1; fi
echo "==> Building (Release)…"
# BUILD BOTH the GUI and the CLI: this script only built Grog.App, so grogcli went stale on
# test boxes and poisoned a whole CLI test round (seen during CLI testing).
if ! dotnet build src/Grog.App/Grog.App.csproj -c Release; then
  echo; echo "Build FAILED — see the errors above."; pause; exit 1
fi
if ! dotnet build src/Grog.Cli/Grog.Cli.csproj -c Release; then
  echo; echo "CLI build FAILED — see the errors above."; pause; exit 1
fi
# A still-running instance is the OLD binary: a login clicked in its window runs pre-fix code and
# burned a GOG login during the 2026-08-23 walk. Close it before launching the fresh build.
if pgrep -f "bin/Release/net10.0/Grog" >/dev/null; then
  echo "==> Closing the running Grog instance (old binary)…"
  pkill -f "bin/Release/net10.0/Grog"
  sleep 2
fi
echo "==> Launching Grog…"
# Launch detached, then hold the terminal briefly: closing it during the app's
# first seconds of startup kills the process (observed on Linux Mint/Cinnamon).
nohup "$REPO/src/Grog.App/bin/Release/net10.0/Grog" >/tmp/grog-icon.log 2>&1 &
disown
sleep 5


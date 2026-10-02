#!/usr/bin/env bash
# One-time setup: install Grog launchers on this Linux machine.
#   • "Grog"                — runs the already-built app (fast)
#   • "Grog (Update & Run)" — git pull + build + launch, in a terminal so you see progress/errors
# Adds both to the application menu and, if possible, the Desktop. Re-runnable safely.
set -e

REPO="$(cd "$(dirname "$(readlink -f "$0")")/.." && pwd)"
APP_BIN="$REPO/src/Grog.App/bin/Release/net10.0/Grog"
UPDATE_RUN="$REPO/tools/grog-update-run.sh"
ICON="$REPO/src/Grog.App/Assets/grog-amber.png"
APPS="$HOME/.local/share/applications"

chmod +x "$UPDATE_RUN"
mkdir -p "$APPS"

cat > "$APPS/grog.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Grog
Comment=GOG library backup
Icon=$ICON
Exec=$APP_BIN
Path=$REPO/src/Grog.App
Terminal=false
Categories=Utility;
EOF

cat > "$APPS/grog-update-run.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Grog (Update & Run)
Comment=Pull latest from GitHub, build, and launch Grog
Icon=$ICON
Exec=x-terminal-emulator -e "$UPDATE_RUN"
Terminal=false
Categories=Utility;
EOF

update-desktop-database "$APPS" 2>/dev/null || true
echo "Installed to the application menu — search 'Grog'."

# Optional: also drop icons on the Desktop (Cinnamon needs them marked trusted).
DESK="${XDG_DESKTOP_DIR:-$HOME/Desktop}"
if [ -d "$DESK" ]; then
  cp "$APPS/grog.desktop" "$APPS/grog-update-run.desktop" "$DESK/"
  chmod +x "$DESK"/grog*.desktop
  gio set "$DESK/grog.desktop" metadata::trusted true 2>/dev/null || true
  gio set "$DESK/grog-update-run.desktop" metadata::trusted true 2>/dev/null || true
  echo "Placed icons on the Desktop (marked trusted)."
fi

echo "Done. Tip: 'Grog' runs the last build; 'Grog (Update & Run)' refreshes from GitHub first."

#!/usr/bin/env bash
# Builds Grog.app (and optionally a .dmg) on macOS, mirroring what the Windows installer script does:
# publish the app and the CLI into ONE place, stage the license files, then wrap the result.
#
#   bash packaging/macos/build-app.sh                 # this Mac's architecture, unsigned beyond ad-hoc
#   bash packaging/macos/build-app.sh --rid osx-x64
#   bash packaging/macos/build-app.sh --universal     # lipo'd fat binaries, both architectures
#   bash packaging/macos/build-app.sh --dmg
#
# Output: dist/Grog.app, and dist/Grog-macos-<rid>.dmg with --dmg
#
# SIGNING. Set CODESIGN_IDENTITY to a Developer ID and the bundle is signed with the hardened runtime;
# leave it unset and the bundle is signed AD HOC, which is the minimum Apple Silicon needs to run a
# binary at all but does NOT satisfy Gatekeeper. An unnotarized bundle tells the user the app is
# damaged, so a distributable build also needs `xcrun notarytool submit --wait` and `stapler staple`,
# both of which require an Apple Developer ID. Nothing in this script can substitute for that.
#
#   CODESIGN_IDENTITY="Developer ID Application: Name (TEAMID)" bash packaging/macos/build-app.sh --dmg

set -euo pipefail

CONFIG=Release
RID=""
UNIVERSAL=0
MAKE_DMG=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid) RID="$2"; shift 2 ;;
    --configuration) CONFIG="$2"; shift 2 ;;
    --universal) UNIVERSAL=1; shift ;;
    --dmg) MAKE_DMG=1; shift ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
# No --rid: build for THIS Mac. hw.optional.arm64 reads the hardware, so a Terminal running under Rosetta on
# Apple Silicon still gets arm64 (uname -m would say x86_64 there). An Intel Mac has no such key.
if [[ -z "$RID" ]]; then
  if [[ "$(sysctl -n hw.optional.arm64 2>/dev/null || true)" == "1" ]]; then RID=osx-arm64; else RID=osx-x64; fi
fi

[[ "$(uname -s)" == "Darwin" ]] || { echo "This script must run on macOS." >&2; exit 1; }

# PREFLIGHT. Without this, a missing or too-old SDK fails deep inside `dotnet publish` with an
# NETSDK error about a target framework, which tells a first-time user nothing they can act on.
# Say what is wrong and what to type, then stop.
SDK_URL="https://dotnet.microsoft.com/download/dotnet/10.0"

if ! command -v dotnet >/dev/null 2>&1; then
  cat >&2 <<EOF
The .NET SDK is not installed, or is not on your PATH.

Install .NET 10:
    brew install --cask dotnet-sdk
  or download it from $SDK_URL

If you have just installed it, open a new terminal window and try again --
the installer adds dotnet to the PATH only for new shells.
EOF
  exit 1
fi

# `dotnet --version` reports the SDK selected for this folder, which global.json can pin. It fails
# outright on a runtime-only install, so treat a non-zero exit as "no SDK" rather than trusting output.
if ! DOTNET_VERSION="$(dotnet --version 2>/dev/null)" || [[ -z "$DOTNET_VERSION" ]]; then
  cat >&2 <<EOF
'dotnet' is present but no .NET SDK is installed -- this looks like a runtime-only install.

Install the .NET 10 SDK:
    brew install --cask dotnet-sdk
  or download it from $SDK_URL
EOF
  exit 1
fi

DOTNET_MAJOR="${DOTNET_VERSION%%.*}"
if ! [[ "$DOTNET_MAJOR" =~ ^[0-9]+$ ]] || (( DOTNET_MAJOR < 10 )); then
  cat >&2 <<EOF
Grog needs the .NET 10 SDK. You have $DOTNET_VERSION.

Install .NET 10 (it installs alongside your current version and does not remove it):
    brew install --cask dotnet-sdk
  or download it from $SDK_URL

If .NET 10 is already installed, a global.json in a parent folder may be pinning
an older SDK; 'dotnet --list-sdks' shows what is available.
EOF
  exit 1
fi

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO"

# Version is derived exactly the way the build derives it, so the bundle can never disagree with the
# app's own About page: VersionPrefix and GrogBuildOffset from the csproj, plus the commit count.
# CFBundleShortVersionString is the three-part release number; CFBundleVersion carries the build.
SHORT_VERSION="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' src/Grog.App/Grog.App.csproj | head -1)"
[[ -n "$SHORT_VERSION" ]] || { echo "Could not read <VersionPrefix> from src/Grog.App/Grog.App.csproj" >&2; exit 1; }
OFFSET="$(sed -n 's:.*<GrogBuildOffset>\(.*\)</GrogBuildOffset>.*:\1:p' src/Grog.App/Grog.App.csproj | head -1)"
COUNT="$(git rev-list --count HEAD 2>/dev/null || true)"
[[ -n "$COUNT" ]] && COUNT=$(( COUNT + ${OFFSET:-0} ))
BUILD_VERSION="${COUNT:+$SHORT_VERSION.$COUNT}"
BUILD_VERSION="${BUILD_VERSION:-$SHORT_VERSION}"
echo "Grog $BUILD_VERSION (short $SHORT_VERSION)"

STAGE="$REPO/dist/stage"
APP="$REPO/dist/Grog.app"
rm -rf "$STAGE" "$APP"
mkdir -p "$STAGE"

publish_one() {   # $1 = rid, $2 = destination
  local rid="$1" dest="$2"
  # Not PublishSingleFile: a bundle wants a plain folder, and Avalonia's native assets
  # (Skia, HarfBuzz, the WebView shim) extract more predictably left as files.
  dotnet publish src/Grog.App -c "$CONFIG" -r "$rid" --self-contained true \
      -p:PublishSingleFile=false -o "$dest/app" >/dev/null
  dotnet publish src/Grog.Cli -c "$CONFIG" -r "$rid" --self-contained true \
      -p:PublishSingleFile=false -o "$dest/cli" >/dev/null
}

if [[ $UNIVERSAL -eq 1 ]]; then
  RID=universal
  publish_one osx-arm64 "$STAGE/arm64"
  publish_one osx-x64   "$STAGE/x64"
  # Take the arm64 tree as the layout, then replace every Mach-O with a fat one. Managed DLLs are
  # architecture-neutral and must NOT be lipo'd; only the native files differ between the two trees.
  cp -R "$STAGE/arm64/app" "$STAGE/merged"
  cp -R "$STAGE/arm64/cli/." "$STAGE/merged/"
  while IFS= read -r rel; do
    a="$STAGE/arm64/app/$rel"; x="$STAGE/x64/app/$rel"
    [[ -f "$x" ]] || continue
    file "$a" | grep -q 'Mach-O' || continue
    lipo -create "$a" "$x" -output "$STAGE/merged/$rel" 2>/dev/null || cp "$a" "$STAGE/merged/$rel"
  done < <(cd "$STAGE/arm64/app" && find . -type f | sed 's|^\./||')
  PAYLOAD="$STAGE/merged"
else
  publish_one "$RID" "$STAGE/single"
  cp -R "$STAGE/single/app" "$STAGE/merged"
  cp -R "$STAGE/single/cli/." "$STAGE/merged/"
  PAYLOAD="$STAGE/merged"
fi

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PAYLOAD/." "$APP/Contents/MacOS/"
cp packaging/macos/Grog.icns "$APP/Contents/Resources/Grog.icns"
cp LICENSE THIRD-PARTY-NOTICES.txt "$APP/Contents/Resources/" 2>/dev/null || true

sed -e "s|@SHORT_VERSION@|$SHORT_VERSION|g" -e "s|@BUILD_VERSION@|$BUILD_VERSION|g" \
    packaging/macos/Info.plist > "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"

chmod +x "$APP/Contents/MacOS/Grog" "$APP/Contents/MacOS/grogcli" 2>/dev/null || true

# Sign inside out: nested Mach-O first, the bundle last, or the outer signature is invalidated
# the moment anything inside it is touched.
if [[ -n "${CODESIGN_IDENTITY:-}" ]]; then
  echo "Signing with: $CODESIGN_IDENTITY"
  find "$APP/Contents/MacOS" -type f \( -name '*.dylib' -o -name '*.so' \) -print0 \
    | xargs -0 -I{} codesign --force --timestamp --options runtime -s "$CODESIGN_IDENTITY" {}
  codesign --force --timestamp --options runtime -s "$CODESIGN_IDENTITY" "$APP/Contents/MacOS/grogcli"
  codesign --force --timestamp --options runtime -s "$CODESIGN_IDENTITY" "$APP/Contents/MacOS/Grog"
  codesign --force --timestamp --options runtime -s "$CODESIGN_IDENTITY" "$APP"
  codesign --verify --deep --strict --verbose=2 "$APP"
  echo "NOT NOTARIZED. Run notarytool and stapler before distributing."
else
  echo "No CODESIGN_IDENTITY set; signing ad hoc (runs locally, will NOT pass Gatekeeper)."
  codesign --force --deep -s - "$APP" >/dev/null 2>&1 || true
fi

# The Finder and the Dock cache icons aggressively, and a stale cache is the most common reason a
# correct bundle still shows the generic tile. Nudge both rather than let it read as a bug.
touch "$APP"
echo "Built $APP"

if [[ $MAKE_DMG -eq 1 ]]; then
  DMG="$REPO/dist/Grog-macos-$RID.dmg"
  rm -f "$DMG"
  STAGEDIR="$REPO/dist/dmg"
  rm -rf "$STAGEDIR"; mkdir -p "$STAGEDIR"
  cp -R "$APP" "$STAGEDIR/"
  ln -s /Applications "$STAGEDIR/Applications"
  hdiutil create -volname Grog -srcfolder "$STAGEDIR" -ov -format UDZO "$DMG" >/dev/null
  rm -rf "$STAGEDIR"
  echo "Built $DMG"
fi

rm -rf "$STAGE"

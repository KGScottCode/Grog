#!/usr/bin/env bash
# Grog container build, step 1: a .NET 10 runtime + the C# 14 compiler, from nuget only.
#
# WHY THIS EXISTS, and why you must not "improve" it back into apt:
# the target sandbox has no root (no_new_privs is set, /etc/sudo.conf is owned by nobody) and the
# egress proxy allows exactly one host, api.nuget.org. apt-get, add-apt-repository, the
# dotnet/backports PPA, packages.microsoft.com and dot.net's own dotnet-install.sh ALL need root
# and/or are 403'd. Every session that retries them burns the session and delivers nothing.
#
# What you get: `dotnet <app.dll>` and `csc`. NOT a full SDK - nuget ships no MSBuild, so
# dotnet build / run / new / restore do not work and never will here. The .axaml half of the build
# is driven by a reflection driver over Avalonia.Build.Tasks instead; see build.sh.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"

VER="${DOTNET_VERSION:-10.0.10}"
ROOT="${GROG_BUILD_ROOT:-$HOME/.grog-build}"
DR="$ROOT/dotnet"
PKG="$ROOT/pkg"
BIN="$ROOT/bin"
HOSTPKG="$PKG/x/microsoft.netcore.app.host.linux-x64"
# Persistent across sessions - see the note in fetch-packages.sh.
CACHE="${GROG_PKG_CACHE:-$REPO/_build-cache/nupkg}"

mkdir -p "$PKG" "$BIN" "$DR" "$CACHE"

fetch() {   # fetch <id> <version>
  local id="$1" ver="$2" nupkg="$1.$2.nupkg" out="$PKG/$1.$2.nupkg"
  if [ ! -s "$out" ]; then
    if [ -s "$CACHE/$nupkg" ]; then
      cp "$CACHE/$nupkg" "$out"
    else
      curl -sSfL -o "$out" "https://api.nuget.org/v3-flatcontainer/$id/$ver/$id.$ver.nupkg"
      unzip -tq "$out" >/dev/null 2>&1 && cp "$out" "$CACHE/$nupkg"
    fi
  fi
  local d="$PKG/x/$id"
  [ -d "$d" ] || { mkdir -p "$d"; (cd "$d" && unzip -oq "$out"); }
}

echo "[1/4] runtime + ref packs ($VER)"
for id in microsoft.netcore.app.runtime.linux-x64 microsoft.netcore.app.host.linux-x64 \
          microsoft.netcore.app.ref microsoft.aspnetcore.app.runtime.linux-x64; do
  fetch "$id" "$VER"
done
fetch microsoft.net.compilers.toolset "${ROSLYN_VERSION:-5.0.0}"

echo "[2/4] assembling DOTNET_ROOT at $DR"
mkdir -p "$DR/shared/Microsoft.NETCore.App/$VER" "$DR/host/fxr/$VER" "$DR/packs/ref/net10.0"
cp -r "$PKG/x/microsoft.netcore.app.runtime.linux-x64/runtimes/linux-x64/lib/net10.0/."    "$DR/shared/Microsoft.NETCore.App/$VER/"
cp -r "$PKG/x/microsoft.netcore.app.runtime.linux-x64/runtimes/linux-x64/native/."         "$DR/shared/Microsoft.NETCore.App/$VER/"
cp -r "$PKG/x/microsoft.aspnetcore.app.runtime.linux-x64/runtimes/linux-x64/lib/net10.0/." "$DR/shared/Microsoft.NETCore.App/$VER/" 2>/dev/null || true
cp "$DR/shared/Microsoft.NETCore.App/$VER/libhostfxr.so" "$DR/host/fxr/$VER/"
cp -r "$PKG/x/microsoft.netcore.app.ref/ref/net10.0/." "$DR/packs/ref/net10.0/"

# hostfxr reports ".NET location: Not found" unless a file literally named `dotnet` sits at
# DOTNET_ROOT. It is never executed as a muxer; its presence is what the probe looks for.
cp "$HOSTPKG/runtimes/linux-x64/native/apphost" "$DR/dotnet"
chmod +x "$DR/dotnet"

echo "[3/4] apphost patcher + dotnet/csc shims"
# NuGet ships no muxer. To RUN a dll you copy apphost and overwrite its 1024-byte placeholder with
# the dll's filename, NUL-padded. That is exactly what the SDK does when it emits an executable.
cat > "$BIN/apphost-patch.py" <<'PY'
import sys, os
apphost, dll, out = sys.argv[1], sys.argv[2], sys.argv[3]
PLACEHOLDER = b"c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2"
data = bytearray(open(apphost, "rb").read())
i = data.find(PLACEHOLDER)
if i < 0:
    raise SystemExit("apphost placeholder not found - wrong package or a patched copy")
name = dll.encode()
if len(name) >= 1024:
    raise SystemExit("dll name too long for the apphost slot")
data[i:i + 1024] = name + b"\0" * (1024 - len(name))
open(out, "wb").write(bytes(data))
os.chmod(out, 0o755)
PY

# `dotnet <app.dll> [args]` - the ONLY muxer form supported here. Deliberately does not cd: the app
# inherits the caller's working directory, because tests resolve fixtures and the repo root from it.
cat > "$BIN/dotnet" <<EOF
#!/usr/bin/env bash
set -euo pipefail
export DOTNET_ROOT="$DR"
DLL="\$1"; shift
D=\$(cd "\$(dirname "\$DLL")" && pwd); B=\$(basename "\$DLL")
H="\$D/.apphost_\${B%.dll}"
[ -x "\$H" ] || python3 "$BIN/apphost-patch.py" "$HOSTPKG/runtimes/linux-x64/native/apphost" "\$B" "\$H"
exec "\$H" "\$@"
EOF
chmod +x "$BIN/dotnet"

cat > "$BIN/csc" <<EOF
#!/usr/bin/env bash
exec "$BIN/dotnet" "$PKG/x/microsoft.net.compilers.toolset/tasks/netcore/bincore/csc.dll" "\$@"
EOF
chmod +x "$BIN/csc"

cat > "$PKG/x/microsoft.net.compilers.toolset/tasks/netcore/bincore/csc.runtimeconfig.json" <<EOF
{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"},"rollForward":"Major"}}
EOF

echo "[4/4] verifying"
export PATH="$BIN:$PATH"
csc /version
echo "OK. Add to PATH:  export PATH=$BIN:\$PATH"

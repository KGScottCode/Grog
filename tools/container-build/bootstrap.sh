#!/usr/bin/env bash
# Grog container build - ONE ENTRY POINT. Run this first thing in every container session:
#
#     bash tools/container-build/bootstrap.sh && bash tools/container-build/build.sh test
#
# Cold (empty cache): a few minutes, mostly downloading ~400MB from api.nuget.org.
# Warm (cache present in _build-cache/): no network at all.
#
# It is idempotent - re-running costs seconds and re-verifies the toolchain, so when in doubt, run it.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
ROOT="${GROG_BUILD_ROOT:-$HOME/.grog-build}"

# The sandbox disk is small and a full one produces TRUNCATED downloads rather than an error you can
# read (a half-written Tmds.DBus.dll cost a debugging round once). Fail loudly and early instead.
avail_kb=$(df -Pk "$HOME" | awk 'NR==2{print $4}')
if [ "$avail_kb" -lt 1500000 ]; then
  echo "WARNING: only $((avail_kb/1024))MB free under $HOME. The build needs ~1.5GB."
  echo "         Clear \$HOME/tmp (the test suite leaves ~31MB of grog-log-* per run) or set"
  echo "         GROG_BUILD_ROOT to a roomier filesystem."
fi

bash "$HERE/install-dotnet10.sh"
bash "$HERE/fetch-packages.sh"

echo "== avdriver (MSBuild stand-in)"
export DOTNET_ROOT="$ROOT/dotnet"
export PATH="$ROOT/bin:$PATH"
mkdir -p "$ROOT/gen"
csc /nologo /target:exe /langversion:latest /nullable:enable \
  /out:"$ROOT/gen/avdriver.dll" \
  $(for d in "$ROOT"/dotnet/packs/ref/net10.0/*.dll; do echo "/r:$d"; done) \
  /r:"$ROOT/tasks/Microsoft.Build.Framework.dll" \
  "$HERE/avdriver.cs" 2>&1 | grep -v "CS1701" || true
cat > "$ROOT/gen/avdriver.runtimeconfig.json" <<'EOF'
{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"},"rollForward":"Major"}}
EOF
# The task assembly and the MSBuild assemblies it loads must sit beside the driver.
cp -f "$ROOT"/tasks/*.dll "$ROOT/gen/"

cat <<EOF

Ready. Add to PATH in each shell:
    export PATH=$ROOT/bin:\$PATH DOTNET_ROOT=$ROOT/dotnet

Then:
    bash tools/container-build/build.sh test    # build + run the suite
    bash tools/container-build/build.sh shot    # build + render (GROG_SHOT_* env vars apply)
EOF

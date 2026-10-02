#!/usr/bin/env bash
# Grog container build, step 2: pull the pinned package set from api.nuget.org and lay it out so
# csc and the Avalonia build tasks can find everything by directory rather than by restore graph.
#
# Layout produced under $GROG_BUILD_ROOT (default ~/.grog-build):
#   lib/       managed reference assemblies (net10.0 > net9.0 > net8.0 > netstandard2.0, first hit wins)
#   native/    libSkiaSharp.so, libHarfBuzzSharp.so - copied beside the app at run time
#   tasks/     Avalonia.Build.Tasks.dll + the MSBuild assemblies it loads
#   analyzers/ CommunityToolkit.Mvvm source generators (these DO run under bare csc)
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
ROOT="${GROG_BUILD_ROOT:-$HOME/.grog-build}"
PKG="$ROOT/pkg"
LIB="$ROOT/lib"; NATIVE="$ROOT/native"; TASKS="$ROOT/tasks"; ANALYZERS="$ROOT/analyzers"

# THE ONE THING THAT SURVIVES A SESSION. The Linux sandbox is discarded every time; the repo folder
# is on the user's own disk and is not. So the .nupkg files - immutable, named by id+version, pure
# data with no exec bits to lose across the Windows mount - live there, gitignored. A cold session
# with a warm cache does no network at all.
CACHE="${GROG_PKG_CACHE:-$REPO/_build-cache/nupkg}"
mkdir -p "$PKG/x" "$LIB" "$NATIVE" "$TASKS" "$ANALYZERS" "$CACHE"

echo "[1/3] packages (cache: $CACHE)"
hits=0; misses=0
while read -r id ver _; do
  [ -z "${id:-}" ] && continue
  case "$id" in \#*) continue;; esac
  nupkg="$id.$ver.nupkg"
  out="$PKG/$nupkg"
  if [ ! -s "$out" ]; then
    if [ -s "$CACHE/$nupkg" ]; then
      cp "$CACHE/$nupkg" "$out"; hits=$((hits+1))
    else
      curl -sSfL -o "$out" "https://api.nuget.org/v3-flatcontainer/$id/$ver/$id.$ver.nupkg"
      # Only cache a file that unzips. A truncated download (this happened once, on a full disk)
      # cached forever would be a permanently broken build with a confusing error.
      unzip -tq "$out" >/dev/null 2>&1 && cp "$out" "$CACHE/$nupkg"
      misses=$((misses+1))
    fi
  fi
  d="$PKG/x/$id"
  [ -d "$d" ] || { mkdir -p "$d"; (cd "$d" && unzip -oq "$out"); }
done < <(grep -v '^\s*#' "$HERE/packages.txt" | grep -v '^\s*$')
echo "  cached=$hits downloaded=$misses"

echo "[2/3] flattening managed assemblies"
# ONE target framework per package, best-first. Copying every tfm would put two System.* identities
# in the reference set and csc picks arbitrarily; that is how a build starts failing with CS0433.
for d in "$PKG"/x/*/; do
  for tfm in net10.0 net9.0 net8.0 net6.0 netstandard2.1 netstandard2.0; do
    if compgen -G "$d/lib/$tfm/*.dll" > /dev/null; then
      cp -n "$d"/lib/$tfm/*.dll "$LIB/" 2>/dev/null || true
      break
    fi
  done
done

# Avalonia's platform backends live under runtimes/, not lib/ - without these the headless X11/Skia
# platform cannot be located and the render dies with "Unable to locate 'IWindowingPlatform'".
for d in "$PKG"/x/*/; do
  for tfm in net10.0 net9.0 net8.0 netstandard2.0; do
    if compgen -G "$d/runtimes/linux-x64/lib/$tfm/*.dll" > /dev/null; then
      cp -n "$d"/runtimes/linux-x64/lib/$tfm/*.dll "$LIB/" 2>/dev/null || true
      break
    fi
  done
done

# Natives, from the SkiaSharp/HarfBuzz asset packages ONLY. A blanket runtimes/**/native sweep also
# scoops libcoreclr.so and friends out of the runtime pack; a second copy of those sitting beside the
# app is at best noise and at worst a host that loads the wrong one.
# The RID path is NOT optional. These packages ship the same filename for arm, arm64, riscv64,
# loongarch64, musl and bionic as well as linux-x64, so an unpinned find copies all of them into one
# flat folder and the LAST one wins. That lands a 32-bit ARM libSkiaSharp.so on an x64 host, and the
# loader reports it as "cannot open shared object file" - which reads as missing, not as wrong arch.
for id in skiasharp.nativeassets.linux harfbuzzsharp.nativeassets.linux; do
  find "$PKG/x/$id/runtimes/linux-x64/native" -name "*.so" -exec cp -f {} "$NATIVE/" \; 2>/dev/null || true
done
for f in "$NATIVE"/*.so; do
  file "$f" | grep -q "x86-64" || { echo "FATAL: $f is not x86-64"; exit 1; }
done

echo "[3/3] build tasks + analyzers"
# Avalonia.Build.Tasks ships INSIDE the main avalonia package, under tools/.
find "$PKG/x/avalonia" -name "Avalonia.Build.Tasks.dll" -exec cp -f {} "$TASKS/" \;
# ...and it needs the whole tools/ folder beside it (XamlX, Mono.Cecil, and friends).
find "$PKG/x/avalonia/tools" -name "*.dll" -exec cp -n {} "$TASKS/" \; 2>/dev/null || true
for id in microsoft.build.framework microsoft.build.utilities.core; do
  find "$PKG/x/$id" -path "*/lib/net9.0/*.dll" -exec cp -f {} "$TASKS/" \; 2>/dev/null || true
  find "$PKG/x/$id" -path "*/lib/net8.0/*.dll" -exec cp -n {} "$TASKS/" \; 2>/dev/null || true
done
find "$PKG/x/communitytoolkit.mvvm" -path "*analyzers*roslyn4.3*cs*.dll" -exec cp -f {} "$ANALYZERS/" \; 2>/dev/null || true
[ -z "$(ls -A "$ANALYZERS")" ] && find "$PKG/x/communitytoolkit.mvvm" -path "*analyzers*" -name "*.dll" -exec cp -n {} "$ANALYZERS/" \;

echo "[4/4] pruning extracted packages"
# ~700MB of extracted nupkg trees whose useful contents have just been copied into lib/native/tasks/
# analyzers. On a 10GB sandbox that is the difference between a suite that runs and one that dies
# with "the disk was full" partway through (which reads as a test failure, not an environment one).
# The .nupkg files stay in the persistent cache, so a re-extract is free if it is ever needed.
KEEP="microsoft.netcore.app.host.linux-x64 microsoft.net.compilers.toolset"
for d in "$PKG"/x/*/; do
  id="$(basename "$d")"
  case " $KEEP " in *" $id "*) continue;; esac
  rm -rf "$d"
done
rm -f "$PKG"/*.nupkg

echo "lib=$(ls "$LIB" | wc -l) native=$(ls "$NATIVE" | wc -l) tasks=$(ls "$TASKS" | wc -l) analyzers=$(ls "$ANALYZERS" | wc -l) pkgdir=$(du -sh "$PKG" | cut -f1)"

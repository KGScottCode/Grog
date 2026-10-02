#!/usr/bin/env bash
# Grog container build, step 3: compile everything and (optionally) run the suite / render a page.
#
#   build.sh              Core + Tests + Cli + App + HeadlessShots
#   build.sh core         Core only, then Tests
#   build.sh test         ...and run the suite
#   build.sh app          Core + App + HeadlessShots (no tests)
#   build.sh shot [name]  ...and render; extra GROG_SHOT_* env vars pass straight through
#
# ORDER MATTERS in one place and it has bitten before: the XAML compiler reads the RESOURCES BLOB,
# not the .axaml on disk. Edit an .axaml, skip the blob, and the render silently shows the PREVIOUS
# layout while you debug a change that was never in the binary. `blob` therefore always runs before
# the App compile here; never hand-run the later steps alone after an axaml edit.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
ROOT="${GROG_BUILD_ROOT:-$HOME/.grog-build}"
export DOTNET_ROOT="$ROOT/dotnet"
export PATH="$ROOT/bin:$PATH"

REF="$ROOT/dotnet/packs/ref/net10.0"
LIB="$ROOT/lib"; OBJ="$ROOT/obj"; OUT="$ROOT/out"; RUN="$ROOT/run"
APPDIR="$REPO/src/Grog.App"
mkdir -p "$OBJ" "$OUT" "$OUT/t" "$OUT/xamlout" "$RUN" "$ROOT/shots"

refs()    { for d in "$REF"/*.dll; do echo "/r:$d"; done; }
avrefs()  { for d in "$LIB"/*.dll; do echo "/r:$d"; done; }
sources() { find "$1" -name "*.cs" -not -path "*_to_delete*" -not -path "*/obj/*" -not -path "*/bin/*"; }
# CS1701 is the ref-assembly version nag every nuget package produces against a ref pack; CS0649 fires
# on fields only ever assigned from the test assembly. Neither exists in the VS build.
quiet()   { grep -v "CS1701\|CS1702\|CS0649" || true; }

# ---- generated inputs -------------------------------------------------------
cat > "$OBJ/ivt-core.cs" <<'EOF'
// The csproj declares these via <InternalsVisibleTo>; the SDK turns that into an attribute and there
// is no SDK here. Without it the tests compile and then fail at RUNTIME with MethodAccessException.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Grog.Core.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Grog")]
EOF
cat > "$OBJ/ivt-app.cs" <<'EOF'
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("HeadlessShots")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Grog.Core.Tests")]
EOF
# The SDK turns <Version> into these attributes; bare csc emits neither, so the rail and About page fall
# back to 0.0.0.0 and every container render quietly misstates the version. Read it from the csproj so a
# render can be trusted as a picture of a real build.
# Since 09-11 the build number is STAMPED by an MSBuild target, so the csproj's only <Version> line is
# the unevaluated `$(VersionPrefix).$([MSBuild]::Add(...))` inside that target. Taking it literally used
# to hand csc a version string it rejects (CS7034). Skip any line still holding a property expression and
# rebuild the number the way the target does: prefix + offset + commit count, prefix alone with no git.
APPVER="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$APPDIR/Grog.App.csproj" | { grep -v '\$(' || true; } | head -1)"
if [ -z "$APPVER" ]; then
  PREFIX="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$APPDIR/Grog.App.csproj" | head -1)"
  OFFSET="$(sed -n 's:.*<GrogBuildOffset>\(.*\)</GrogBuildOffset>.*:\1:p' "$APPDIR/Grog.App.csproj" | head -1)"
  COUNT="$(git -C "$REPO" rev-list --count HEAD 2>/dev/null || true)"
  if [ -n "$PREFIX" ] && [ -n "$OFFSET" ] && [ -n "$COUNT" ]; then
    APPVER="$PREFIX.$((COUNT + OFFSET))"
  else
    APPVER="$PREFIX"
  fi
fi
APPVER="${APPVER:-0.0.1}"
# AssemblyVersion takes at most 4 parts. <Version> is normally 3 (0.1.0) and wants a .0 revision, but a
# test build carries a 4th part already (0.1.0.1002) -- padding that one would make five and fail CS7034.
case "$APPVER" in
  *.*.*.*) ASMVER="$APPVER" ;;
  *)       ASMVER="$APPVER.0" ;;
esac
cat > "$OBJ/version-app.cs" <<EOF
[assembly: System.Reflection.AssemblyVersion("$ASMVER")]
[assembly: System.Reflection.AssemblyFileVersion("$ASMVER")]
[assembly: System.Reflection.AssemblyInformationalVersion("$APPVER")]
EOF
# Grog.Core sets ImplicitUsings=disable, so it gets NO global usings here either - that is the point.
# The crutch hid a missing `using System.Linq` that broke the VS build (2026-08-21).
cat > "$OBJ/gu-tests.cs" <<'EOF'
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
EOF

# ---- steps ------------------------------------------------------------------
build_core() {
  echo "== Grog.Core"
  csc /nologo /target:library /langversion:latest /nullable:enable /noconfig /nostdlib+ \
    /out:"$OUT/Grog.Core.dll" $(refs) \
    /r:"$LIB/System.Security.Cryptography.ProtectedData.dll" "$OBJ/ivt-core.cs" \
    $(sources "$REPO/src/Grog.Core") 2>&1 | quiet
}

build_cli() {
  echo "== Grog.Cli"
  csc /nologo /target:exe /langversion:latest /nullable:enable /noconfig /nostdlib+ \
    /out:"$OUT/Grog.Cli.dll" $(refs) /r:"$OUT/Grog.Core.dll" "$OBJ/gu-tests.cs" \
    $(sources "$REPO/src/Grog.Cli") 2>&1 | quiet
}

build_tests() {
  echo "== Grog.Core.Tests"
  # THE FULL SUITE, App-referencing files included (09-01). They used to be excluded here, and a
  # test file that never compiled in the container (FileState.NotDownloaded) kept CI red for a day
  # while every container run reported green. The tests link against the XamlIl-compiled Grog.dll,
  # so build_app must have run first; the tally here now equals CI's and VS's (614/0/5 of 619).
  [ -f "$OUT/xamlout/Grog.dll" ] || { echo "build_tests: Grog.dll missing - build_app must run first"; exit 1; }
  cp -f "$LIB"/*.dll "$OUT/xamlout/Grog.dll" "$OUT/t/"
  csc /nologo /target:exe /langversion:latest /nullable:enable /noconfig /nostdlib+ \
    /out:"$OUT/t/Grog.Core.Tests.dll" $(refs) $(avrefs) /r:"$OUT/Grog.Core.dll" /r:"$OUT/t/Grog.dll" \
    "$OBJ/gu-tests.cs" \
    $(find "$REPO/tests/Grog.Core.Tests" -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*") 2>&1 | quiet
  cp -f "$OUT/Grog.Core.dll" "$OUT/t/"
  cat > "$OUT/t/Grog.Core.Tests.runtimeconfig.json" <<'EOF'
{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"},"rollForward":"Major"}}
EOF
}

run_tests() {
  echo "== suite"
  # The suite writes real files (a 66MB verify fixture, a 10MB rotated log) and does not clean up
  # after itself, so leftovers accumulate at ~30MB per run. On a full disk the failures surface as
  # IOException inside unrelated tests, which reads as a code regression rather than an out-of-space.
  rm -rf "${TMPDIR:-$HOME/tmp}"/grog-* /tmp/grog-* 2>/dev/null || true
  local free_kb; free_kb=$(df -Pk "$HOME" | awk 'NR==2{print $4}')
  [ "$free_kb" -lt 300000 ] && echo "  WARNING: only $((free_kb/1024))MB free; the suite needs ~150MB of scratch"
  # From the REPO ROOT: one test walks up for Grog.slnx and errors out anywhere else. That single
  # error is an artifact of where the binary sits, not a failure.
  cd "$REPO" && dotnet "$OUT/t/Grog.Core.Tests.dll" "$@"
}

# A "--" inside an XML comment is ILLEGAL and has broken this build ten times (tracker gotcha, 08-13).
# The compiler's message points at a parser deep in Avalonia.Build.Tasks and never names the comment, so it
# costs a rebuild to find by hand. One second here instead, with the file and line.
check_axaml_comments() {
  echo "== axaml comments"
  python3 - "$APPDIR" <<'EOF'
import re, sys, pathlib
bad = []
for p in pathlib.Path(sys.argv[1]).rglob("*.axaml"):
    if "_to_delete" in p.parts or "obj" in p.parts or "bin" in p.parts:
        continue
    text = p.read_text(encoding="utf-8", errors="replace")
    for m in re.finditer(r"<!--.*?-->", text, re.S):
        if "--" in m.group(0)[4:-3]:
            line = text.count("\n", 0, m.start()) + 1
            bad.append(f"{p}:{line}: '--' inside an XML comment (use ';' or ':')")
for b in bad:
    print("ERROR " + b, file=sys.stderr)
sys.exit(1 if bad else 0)
EOF
}

build_blob() {
  check_axaml_comments
  echo "== resources blob"
  : > "$OBJ/res.list"
  ( cd "$APPDIR"
    find . -name "*.axaml" -not -path "*/_to_delete/*" -not -path "./obj/*" -not -path "./bin/*" \
      | sed 's|^\./||' | while read -r f; do echo "$APPDIR/$f|$f" >> "$OBJ/res.list"; done
    for f in Assets/Fonts/*.ttf Assets/*.png; do
      [ -f "$f" ] && echo "$APPDIR/$f|$f" >> "$OBJ/res.list"
    done )
  dotnet "$ROOT/gen/avdriver.dll" "$ROOT/tasks/Avalonia.Build.Tasks.dll" \
    resources "$APPDIR" "$OBJ/Grog.App.avares" "$OBJ/res.list" | tail -1
}

build_app() {
  build_blob
  echo "== name shim"
  python3 "$HERE/gen-name-shim.py" "$APPDIR" "$OBJ/name-shim.g.cs"
  echo "== Grog.App"
  csc /nologo /target:library /langversion:latest /nullable:enable /noconfig /nostdlib+ \
    /out:"$OUT/Grog.dll" $(refs) $(avrefs) /r:"$OUT/Grog.Core.dll" \
    /resource:"$OBJ/Grog.App.avares",'!AvaloniaResources' \
    $(for a in "$ROOT"/analyzers/*.dll; do echo "/analyzer:$a"; done) \
    "$OBJ/name-shim.g.cs" "$OBJ/ivt-app.cs" "$OBJ/version-app.cs" $(sources "$APPDIR") 2>&1 | quiet

  echo "== XamlIl"
  : > "$OBJ/refs.list"
  for d in "$LIB"/*.dll "$REF"/*.dll; do echo "$d" >> "$OBJ/refs.list"; done
  echo "$OUT/Grog.Core.dll" >> "$OBJ/refs.list"
  dotnet "$ROOT/gen/avdriver.dll" "$ROOT/tasks/Avalonia.Build.Tasks.dll" \
    xaml "$APPDIR" "$OUT/Grog.dll" "$OUT/xamlout/Grog.dll" "$OBJ/refs.list" | grep -E "^xaml:|ERROR"
}

build_shots() {
  echo "== HeadlessShots"
  cp -f "$LIB"/*.dll "$ROOT"/native/*.so "$RUN/"
  cp -f "$OUT/xamlout/Grog.dll" "$OUT/Grog.Core.dll" "$RUN/"
  csc /nologo /target:exe /langversion:latest /nullable:enable /noconfig /nostdlib+ \
    /out:"$RUN/HeadlessShots.dll" $(refs) $(avrefs) \
    /r:"$RUN/Grog.dll" /r:"$OUT/Grog.Core.dll" \
    $(sources "$REPO/tools/HeadlessShots") 2>&1 | quiet
  cat > "$RUN/HeadlessShots.runtimeconfig.json" <<'EOF'
{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"},"rollForward":"Major"}}
EOF
}

render() {
  mkdir -p "$ROOT/shots"
  cd "$RUN"
  LD_LIBRARY_PATH="$RUN" GROG_SHOT_DIR="${GROG_SHOT_DIR:-$ROOT/shots}" \
    GROG_SHOT_SIZE="${GROG_SHOT_SIZE:-1366x768}" timeout 600 dotnet "$RUN/HeadlessShots.dll"
  echo "shots in ${GROG_SHOT_DIR:-$ROOT/shots}"
}

# (09-25, CLAUDE.md rule 10) The UI-thread budget as a gate: 700 synthetic games (4,908 files, 1,821 queued) through
# the stress pass. Fails on any Core entry point that ran on the UI thread (GUARD) or any queue action over 250 ms.
stress() {
  local out; out=$(GROG_SHOT_STRESS="${GROG_SHOT_STRESS:-700}" render 2>&1)
  echo "$out" | grep -E "^(STRESS|QUEUEOP|GUARD)"
  local bad=0
  if echo "$out" | grep -q "^GUARD"; then echo "STRESS GATE: a Core entry point ran on the UI thread"; bad=1; fi
  if echo "$out" | awk '/^QUEUEOP/ { for (i=1;i<=NF;i++) if ($i=="wall" && $(i+1)+0 > 250) f=1 } END { exit !f }'; then
    echo "STRESS GATE: a queue action took over 250 ms"; bad=1; fi
  [ "$bad" = 0 ] && echo "STRESS GATE: pass"
  return $bad
}

case "${1:-all}" in
  core)  build_core; build_app; build_tests ;;
  test)  build_core; build_app; build_tests; shift || true; run_tests "$@" ;;
  cli)   build_core; build_cli ;;
  app)   build_core; build_app; build_shots ;;
  shot)  build_core; build_app; build_shots; render ;;
  render) render ;;
  stress) build_core; build_app; build_shots; stress ;;
  all)   build_core; build_cli; build_app; build_tests; build_shots; run_tests ;;
  *)     echo "usage: build.sh [all|core|test|cli|app|shot|render|stress]"; exit 2 ;;
esac

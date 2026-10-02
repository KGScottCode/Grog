# Container build

A full .NET 10 + Avalonia 12 build of Grog inside a minimal Linux sandbox (no root, NuGet-only network): **Core, Cli, Tests,
Grog.App and HeadlessShots**, with no SDK, no MSBuild, no root and one allowlisted network host.

```bash
bash tools/container-build/bootstrap.sh     # warm ~5s (no network) / cold ~1 min
bash tools/container-build/build.sh test    # ~25s  614 pass, 0 fail, 0 error, 5 skipped of 619 (builds App first)
bash tools/container-build/build.sh shot    # ~24s  renders the app to PNG
bash tools/container-build/build.sh all     # everything
```

## Why any of this exists

The sandbox has no root (`no_new_privs` is set) and the egress proxy allows exactly one host,
`api.nuget.org`. So `apt-get`, `add-apt-repository`, the dotnet PPA, `packages.microsoft.com` and
`dotnet-install.sh` are all dead ends. Sessions used to rediscover that, burn their budget, and
deliver an axaml change nobody had ever rendered.

Everything here is assembled from nuget packages by hand:

| Piece | Stands in for |
| --- | --- |
| `install-dotnet10.sh` | the .NET SDK: runtime, ref packs, `csc`, and a `dotnet <app.dll>` muxer |
| `avdriver.cs` | MSBuild, for the two Avalonia tasks that turn `.axaml` into IL |
| `gen-name-shim.py` | Avalonia's XAML `NameGenerator` |
| `packages.txt` | NuGet restore and version resolution |

## What survives a session, and what does not

The Linux sandbox is destroyed every time. The repo folder is on the developer's own disk and is
not. So `_build-cache/nupkg/` (gitignored, ~190MB) holds the downloaded `.nupkg` files, and a
second session bootstraps with **no network at all**. Delete it whenever you like; the next
bootstrap re-downloads.

Only `.nupkg` files are cached, deliberately: they are immutable, named by id+version, and pure
data. The assembled tree is rebuilt in the sandbox each time, because executable bits do not
survive the Windows mount reliably and a cached binary with no `+x` is a baffling failure.

## Rules that are not optional

- **The XAML compiler reads the resources blob, not the `.axaml` on disk.** Change an axaml and skip
  the blob and the render silently shows the PREVIOUS layout while you debug a change that was never
  in the binary. `build.sh` always regenerates the blob before compiling `Grog.App`; never hand-run
  the later steps on their own after an axaml edit.
- **`Grog.Core` is compiled with NO global usings**, matching `ImplicitUsings=disable` in its csproj.
  A `gu.cs` crutch here once hid a missing `using System.Linq` that broke the VS build.
- **VS remains the build of record.** This validates layout and logic; it is not the shipping build.
  Since 09-01 the container runs the FULL suite, App-referencing test files included (they link the
  XamlIl-compiled `Grog.dll`, so `test` builds the App first). The tally here equals CI's and VS's;
  the old exclusion let a test file that never compiled keep CI red for a day while this said green.

## Failure modes already paid for

- **`libSkiaSharp.so`: "cannot open shared object file"** while the file is plainly there. The
  SkiaSharp native packages ship the same filename for arm, arm64, riscv64, musl and bionic as well
  as x64; an unpinned `find` copies all of them into one flat folder and the last one wins. The
  loader reports a wrong-architecture ELF as *missing*. `fetch-packages.sh` pins `linux-x64` and
  asserts `x86-64` afterwards.
- **A truncated `.dll` from a full disk.** Shows up as `CS0009: PE image doesn't contain managed
  metadata`. `bootstrap.sh` warns under 1.5GB free, and only verified archives enter the cache. The
  usual space hog is `$HOME/tmp` - the test suite leaves ~31MB of `grog-log-*` per run.
- **`Avalonia.HarfBuzz` missing** kills the render inside `UseHeadless()` before the first frame.
- **`DesktopNotifications.Windows`** is needed to COMPILE on Linux; `Notifier.cs` has an
  unconditional `using` and picks a backend at run time.
- **DataGrid and WebView stay at 12.0.1.** There is no 12.0.5 of either, and 12.1.x refuses to link
  against Avalonia 12.0.5 (CS1705).

## When VS moves to a new Avalonia

Bump the versions in `packages.txt` in the same change. The csproj can float on `12.0.*` because VS
does a real restore; there is no restore here, so `packages.txt` **is** the resolution. If it drifts,
this container renders a layout the product no longer has - which is worse than not rendering at all.

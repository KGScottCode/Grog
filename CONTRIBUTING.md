# Contributing to Grog

Bug reports and pull requests are welcome.

## Ground rules

- **Grog only backs up games you own.** It never circumvents DRM (GOG installers are already DRM-free).
  Contributions must respect that.
- Be respectful, and assume good faith.

## Build and test

Requires the **.NET 10 SDK**.

```bash
dotnet build Grog.slnx                           # build everything (should be warning-free)
dotnet run --project tests/Grog.Core.Tests       # run the tests, then read the console tally
dotnet run --project src/Grog.App                # the app
dotnet run --project src/Grog.Cli -- --help      # the command-line tool
```

The tests use a small in-house runner (`[Test]`, `Assert.*`), so `dotnet test` and Visual Studio's Test
Explorer find nothing; in Visual Studio, set `Grog.Core.Tests` as the startup project. Tests that need a
live GOG sign-in skip themselves, so the suite passes offline.

## Layout

- `src/Grog.Core`: the engine (sign-in, GOG API, scanning, downloads, storage, verification). No UI.
- `src/Grog.App`: the Avalonia app.
- `src/Grog.Cli`: the command-line tool.
- `tests/Grog.Core.Tests`: the tests. Every behavior change comes with one.

## Pull requests

One logical change per PR, a clean build and a passing suite. Say what changed and why; screenshots help
for UI changes.

## Bugs and ideas

Use the issue templates. For a bug, include your OS, the Grog version (About page) and steps to reproduce.

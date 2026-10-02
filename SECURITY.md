# Security

## Reporting a vulnerability

Please don't open a public issue. Use **Security → Report a vulnerability** on this repository,
which creates a private advisory.

## What Grog stores, and where

**Sign-in tokens.** You log in on GOG's own page in a native WebView. Grog never sees your password.
What it keeps is the OAuth token GOG returns.

- **Windows** - encrypted at rest with DPAPI, scoped to your Windows user account.
- **Linux** - encrypted with AES-GCM using a key held in the OS keyring via `libsecret`.
- **macOS** - encrypted with AES-GCM using a key held in the macOS Keychain.

Removing an account deletes its stored credentials. It does not delete anything you have backed up.

**Everything else is a local file.** The library manifest, settings and logs live in your user
profile (or beside the app, in a portable copy). There is no Grog server, no account, and no telemetry of any kind. Grog talks only to GOG,
for sign-in, your library and backups, and to GitHub to check for updates.

## What is not hardened yet

- Binaries are **unsigned**: Windows SmartScreen warns on first run, and macOS needs **Open Anyway**.
  Each release publishes SHA-256 checksums to verify your download.
- Without a Linux keyring (or if the macOS Keychain refuses), tokens fall back to a file only your
  account can read, and Grog says so in the interface.
- Anyone who can already read your home directory can read Grog's data.

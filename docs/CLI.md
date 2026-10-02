# The command-line tool (grogcli)

`grogcli` is Grog without the window: the same engine and the same library, for scripts, servers and
scheduled jobs. It ships with every install; the install guides for [Windows](install/Windows.md),
[macOS](install/macOS.md) and [Linux](install/Linux.md) show how to call it from any terminal.

## Quick start

```
grogcli login              # sign in (browserless; --browser uses the system browser)
grogcli sync               # fetch your library from GOG
grogcli download           # fetch everything missing or outdated
grogcli verify --full      # re-check every file against GOG's checksums
grogcli backup --silent    # the whole scheduled run: sync, download, verify
grogcli status             # what is backed up and what is missing
grogcli help               # every command
```

Most commands accept `--json`. `grogcli` uses the same library as the app; while the app is open,
commands that change it are refused, so close the app first.

**Unattended backups:** run `grogcli backup --silent` from Task Scheduler (Windows), cron or a systemd
timer (Linux), or launchd (macOS). `status` and `whoami` never prompt, so they are safe in scripts.

## Watching Grog from another program

Three layers, each optional: exit code, `--json`, run journal. Nothing needs enabling.

---

### 1. The exit code - "did it work?"

Enough for pass/fail alerting.

| Code | Meaning |
|-----:|---------|
| 0 | OK |
| 1 | Error, or the run was stopped (Ctrl-C) before it finished |
| 2 | Usage / bad arguments |
| 3 | Not configured (no backup folder) |
| 4 | Auth expired - needs a one-time interactive login |
| 5 | Login blocked (captcha / 2FA) |
| 6 | Network |
| 7 | Out of space - nothing fits, **or some files were held back for space** (what fit was still fetched) |
| 8 | Integrity failed - corrupt or missing files, **or a download that did not finish everything** |
| 9 | The Grog app is open and owns this profile - a verb that writes (`sync`, `backup`, `verify`, `import`, `root`, ...) did not run. Read-only verbs (`status`, `list`, `report`, `runs`, ...) still work. The refusal is also written to both logs and shown in the app. |

A non-zero exit from a scheduled backup is the whole alert. Files GOG refuses to serve to your account,
and files whose account is signed out, are reported but never fail the run.

---

### 2. `--json` - "what are the numbers?"

One machine-readable document on stdout instead of a table. It goes before or after the command name.

Supported on: `whoami`, `list`, `report`, `health`, `verify`, `root list`, `orphans`, `delisted`,
`runs`, `download`, `backup` (a one-line summary on success; failures come back as JSON on stderr).

`--json` (and `--silent`) keep stdout for the document alone. Progress still exists, on **stderr**:
scan milestones, a `starting <file> <size>` line as each download begins, a heartbeat every 10 seconds
while bytes move, and a `completed`/`failed` line per file.

The two run verbs report the same numbers, so one parser serves both:

```
grogcli --json download
```

```json
{
  "command": "download",
  "queued": 12,
  "completed": 11,
  "failed": 1,
  "skipped": 0,
  "unavailable": 0,
  "heldNoSpace": 0,
  "failures": [ { "file": "setup_b.exe", "state": "Failed", "error": "disk full" } ],
  "runLog": "C:\\Users\\you\\AppData\\Roaming\\Grog\\runs\\run-20260904-031500-download.jsonl",
  "outcome": "Partial",
  "exitCode": 8
}
```

`failed` counts every file that did not land and is not excused (`skipped`, `unavailable` and
`heldNoSpace` are the excuses). `backup` adds the verify pass: `downloaded`, `verified`, `corrupt`,
`missing`.

```
grogcli --json health
```

```json
{
  "totalItems": 125,
  "complete": 48,
  "partial": 77,
  "updatesAvailable": 0,
  "notDownloaded": 0,
  "errors": 0,
  "corruptFiles": 0,
  "missingFiles": 0,
  "delisted": 2,
  "bytesOnDisk": 805361479680,
  "roots": [
    { "label": "Primary", "online": true, "files": 1904 }
  ]
}
```

**Failures come back as JSON too - on stderr, not stdout.**

```json
{
  "error": true,
  "command": "sync",
  "kind": "AuthExpired",
  "message": "GOG session has expired and needs a one-time interactive login…",
  "exitCode": 4
}
```

**Parse stdout always; read stderr only when the exit code is non-zero.**

---

### 3. The run journal - "what happened, even if it crashed?"

A killed process (power loss, crash, Task Manager) prints nothing, so Grog writes the record **as the
run goes**, flushed on every event. Both files carry a `schema` field (currently `1`; absent means `1`);
shapes are stable for a given value.

Two files, both under `<config>\runs\`
(`%APPDATA%\Grog\runs\` on Windows, `~/.config/Grog/runs/` on Linux,
`~/Library/Application Support/Grog/runs/` on macOS):

#### `current.json` - the live summary

Replaced atomically on every event, at a fixed path. **This is the one to poll.**

```json
{
  "runId": "20260810-031500",
  "command": "download",
  "state": "running",
  "startedAt": "2026-08-10T03:15:00-04:00",
  "updatedAt": "2026-08-10T03:22:41-04:00",
  "total": 40,
  "completed": 12,
  "failed": 1,
  "currentFile": "setup_witcher3_2.exe",
  "eventsFile": "run-20260810-031500-download.jsonl"
}
```

`grogcli --json runs` prints this file; reading it directly also works while another process runs.

#### `run-<stamp>-<command>.jsonl` - the event log

Append-only, one JSON object per line, flushed as each file finishes. Tail it live, or read it after a
crash to see what landed.

```
{"ts":"2026-08-10T03:15:00-04:00","event":"run.started","command":"download","total":40}
{"ts":"2026-08-10T03:15:44-04:00","event":"file.completed","file":"setup_a.exe","bytes":734003200,"completed":1,"failed":0,"total":40}
{"ts":"2026-08-10T03:16:02-04:00","event":"file.failed","file":"dlc_b.bin","error":"disk full","completed":1,"failed":1,"total":40}
{"ts":"2026-08-10T03:16:30-04:00","event":"file.unavailable","file":"bonus_c.zip","error":"not for your region","completed":1,"failed":1,"total":40}
{"ts":"2026-08-10T03:40:11-04:00","event":"run.finished","outcome":"completed-with-failures","completed":39,"failed":1,"total":40,"elapsedSeconds":1511}
```

A kill mid-write can only tear the last line; discard it and every line before it is intact.

`file.canceled` (you stopped the run), `file.unavailable` (GOG refuses it) and `file.skipped` (account
signed out) are recorded but are not failures.

The last 20 runs per command are kept. The desktop app writes the same journal for its runs, so the
folder is one history either way.

---

### Detecting a hard crash

A killed run leaves:

- **no `run.finished` line** in its `.jsonl`, and
- `current.json` still saying `"state": "running"`.

"running" with a stale `updatedAt` means the process is gone; the event log still lists what completed.
The library is checkpointed every few seconds, so at most the last few seconds are unrecorded, and the
next `verify` picks those up.

---

### Recipes

**Nightly backup, alert on failure**

```
grogcli --silent --json backup
if errorlevel 1 (  rem read the JSON error document from stderr and notify )
```

`--silent` writes nothing to stdout and never prompts; it still logs to `<config>\logs\grog.log` and the journal.

**Poll status every 5 seconds while a backup runs**

Read `<config>\runs\current.json` rather than calling `grogcli` in a loop; it is valid at every instant.

**After an unexpected reboot, find out what was lost**

```
grogcli runs
```

Then open the `eventsFile` it points at. Anything with `file.completed` landed.

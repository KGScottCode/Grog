// Grog CLI -- headless engine driver. Every Grog capability lands here first.
using System.Threading;
// Licensed under GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Download;
using Grog.Core.Verify;
using Grog.Cli;
using static Grog.Cli.CliVerbs;
using Grog.Core.Format;

// Global flags may precede the command (`grog --json report`); rotate them behind it so args[0] is the
// command. See ArgOrder -- everything below scans argv positionally-agnostic, so the rotation is invisible.
args = ArgOrder.CommandFirst(args);
var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
// --version / version: answer before any storage or session work so it runs on a bare machine.
if (command == "version" || args.Contains("--version", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine($"grogcli {CliVersion.Current()}");
    return (int)Grog.Core.Cli.ExitCode.Success;
}
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected output */ }
bool browserFlag = args.Contains("--browser", StringComparer.OrdinalIgnoreCase);

using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Grog/0.1 (+https://github.com/)");

// Resolve storage: explicit --root, else GROG_BACKUP_ROOT, else the exe's own dir (portable).
// Grog's own state (manifest, tokens) lives in <root>/.grog; content folders sit beside it.
var paths = Grog.Core.Storage.GrogPaths.Resolve(GetArgValue(args, "--root"));
var rootFlag = paths.BackupRoot;   // explicit --root only (empty when not given -- no env/exe fallback)

// --silent: scheduled/headless mode. Quiets stdout + progress, never prompts; failures still hit stderr
// and every notable line lands in <config>/logs/grog.log. --non-interactive is a back-compat alias.
bool silent = args.Contains("--silent", StringComparer.OrdinalIgnoreCase);
bool jsonOut = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
CliText.JsonMode = jsonOut;   // the static Fail() helper reads this (top-level locals are invisible to static locals)
Out.Silent = silent || jsonOut;   // --json also suppresses human chatter, so stdout carries ONLY the JSON
Out.ConfigureLog(paths.ConfigDir);
// Belt-and-suspenders: null stdout entirely in silent mode so even raw Console.WriteLine content (list
// rows, tables) is suppressed -- not just the Out.* helpers. stderr (failures) and the log are untouched.
// --json keeps stdout open (the JSON is the output); its human chatter is suppressed via Out.Silent above.
if (silent && !jsonOut) Console.SetOut(System.IO.TextWriter.Null);


var manifest = new JsonManifestStore(paths);
await manifest.LoadAsync();
// Upgrade a pre-multi-account install before any session binds: the unnamed slot becomes an ordinary
// account and its bare tokens.json is renamed into the per-account scheme.
try { await new Grog.Core.Auth.AccountService(manifest, paths).MigrateLegacyPrimaryAsync(); } catch { }

// The default session is the FIRST registered account's; `login` on a fresh install writes to a scratch
// file that gets adopted on registration, same as the GUI. --account (below) rebinds to a named account.
var firstAccount = manifest.Current.Accounts.FirstOrDefault();
var defaultTokenPath = firstAccount is null
    ? System.IO.Path.Combine(paths.ConfigDir, "tokens-connect.tmp.json")
    : new Grog.Core.Auth.AccountService(manifest, paths).TokenPathFor(firstAccount.Id);
var tokenStore = new FileTokenStore(defaultTokenPath);
bool nonInteractive = silent || args.Contains("--non-interactive", StringComparer.OrdinalIgnoreCase);
IInteractiveLoginProvider loginProvider =
    nonInteractive ? new NonInteractiveLoginProvider()
    : browserFlag ? new BrowserLoginProvider()
    : new CredentialLoginProvider(fallback: new BrowserLoginProvider());
var auth = new GogAuthService(http, tokenStore, loginProvider);
var api = new GogApiClient(http, auth);
api.OnThrottle = msg => Out.Warn(msg);   // 429/5xx backoff waits are said out loud, not silent stalls

// Multi-account step 4: the per-account session cache. Lazy per account, non-interactive by design
// (a secondary account's lapsed session must never pop a login mid-run -- it skips with a log line).
// Commands use it whenever named accounts exist; a legacy single-account setup keeps the plain `api`.
var sessions = new Grog.Core.Auth.AccountSessions(manifest, paths, http) { OnThrottle = msg => Out.Warn(msg) };

// ContentRoot() and the lazy Layout() moved onto CliContext with the verb split (09-02).

// Reject unknown/mistyped options up front: a typo like `--primry` fails loud with a suggestion, never
// silently ignored. CliKnownFlagsTests asserts this set contains every double-dash literal in this file.
{
    var knownFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--account","--all","--blacklist","--browser","--client","--concurrency","--confirm","--debug","--depth","--details",
        "--diag","--download","--dry-run","--export","--extra-type","--extras-only","--force","--full","--help","--id",
        "--ignore-saved-scope","--installers-only","--json","--label","--lang","--largest","--limit","--limit-mbps","--local","--mode",
        "--move","--moved","--no-dlc","--no-verify","--non-interactive","--os","--os-fallback","--parallel",
        "--primary","--removable","--retry-failed","--role","--root","--secondary","--show-config","--silent","--skip-extras",
        "--skip-installers","--title","--title-regex","--to","--type","--updates","--verbose","--with","--yes",
        "--keep","--failed","--corrupt","--missing","--game",   // (S2.1) fix / cloudsaves --all / prune
        "--new",   // (New items 09-09) list --new / backup --new
        "--verify-checksums",   // import: fetch GOG's checksum for adopted files with none on record
        "--version",
        "--ignore-pause",   // backup: run even while the queue is paused
        "--with-extras","--games","--no-games",   // scope set
    };
    foreach (var a in args)
    {
        if (!a.StartsWith("--") || a == "--") continue;
        var name = a.Split('=', 2)[0];
        if (knownFlags.Contains(name)) continue;
        var suggest = NearestFlag(name, knownFlags);
        Out.Usage($"Unknown option '{name}'."
            + (suggest is not null ? $" Did you mean '{suggest}'?" : "")
            + " Run 'grog help' for the full list.");
        return (int)Grog.Core.Cli.ExitCode.UsageError;
    }
}

// --account <id>: point the session verbs (whoami / refresh / logout) at ONE named account's token
// file instead of the tokens.json slot. Non-interactive on purpose -- inspecting or refreshing a
// secondary account must never pop a login; signing one in is `account add <id>`.
if (GetArgValue(args, "--account") is { } accountArg)
{
    if (command is not ("whoami" or "status" or "refresh" or "logout"))
    {
        Out.Usage("--account applies to whoami, refresh and logout. To sign an account in, use 'account add <id>'.");
        return (int)Grog.Core.Cli.ExitCode.UsageError;
    }
    await manifest.LoadAsync();
    var acctSvc = new Grog.Core.Auth.AccountService(manifest, paths);
    tokenStore = new FileTokenStore(acctSvc.TokenPathFor(accountArg));
    auth = new GogAuthService(http, tokenStore, new NonInteractiveLoginProvider());
    api = new GogApiClient(http, auth);
    api.OnThrottle = msg => Out.Warn(msg);
    Out.Dim($"Account: {accountArg}");
}

try
{
    var verbs = new Dictionary<string, Func<CliContext, Task<int>>>(StringComparer.OrdinalIgnoreCase)
    {
        ["login"] = CliVerbs.Login,
        ["status"] = CliVerbs.Status,
        ["whoami"] = CliVerbs.Status,
        ["refresh"] = CliVerbs.Refresh,
        ["logout"] = CliVerbs.Logout,
        ["sync"] = CliVerbs.Sync,
        ["list"] = CliVerbs.List,
        ["resolve"] = CliVerbs.Resolve,
        ["download"] = CliVerbs.Download,
        ["verify"] = CliVerbs.Verify,
        ["import"] = CliVerbs.Import,
        ["root"] = CliVerbs.Root,
        ["policy"] = CliVerbs.Policy,
        ["relocate"] = CliVerbs.Relocate,
        ["blacklist"] = CliVerbs.Blacklist,
        ["health"] = CliVerbs.Health,
        ["delisted"] = CliVerbs.Delisted,
        ["serials"] = CliVerbs.Serials,
        ["zipcheck"] = CliVerbs.Zipcheck,
        ["delete"] = CliVerbs.Delete,
        ["orphans"] = CliVerbs.Orphans,
        ["changes"] = CliVerbs.Changes,
        ["changelog"] = CliVerbs.Changelog,
        ["account"] = CliVerbs.Account,
        // (S2.1) `cloudsaves --all` / `cloudsaves prune` route to the Core CloudSaveRun verbs; anything else is the
        // per-game verb as before.
        ["cloudsaves"] = ctx => ctx.Args.Skip(1).Any(a => a.Equals("prune", StringComparison.OrdinalIgnoreCase)) ? CliVerbs.CloudsavesPrune(ctx)
                             : ctx.Args.Contains("--all", StringComparer.OrdinalIgnoreCase) ? CliVerbs.CloudsavesAll(ctx)
                             : CliVerbs.Cloudsaves(ctx),
        ["show"] = CliVerbs.Show,
        ["runs"] = CliVerbs.Runs,
        ["report"] = CliVerbs.Report,
        // (S2.1) `reorg layout` = the guided flat-layout migration on Core's LayoutMigrationRun.
        ["reorg"] = ctx => ctx.Args.Skip(1).Any(a => a.Equals("layout", StringComparison.OrdinalIgnoreCase)) ? CliVerbs.ReorgLayout(ctx) : CliVerbs.Reorg(ctx),
        ["fix"] = CliVerbs.Fix,
        ["mark-missing"] = CliVerbs.MarkMissing,
        ["clear-new"] = CliVerbs.ClearNew,   // (New items 09-09)
        ["backup"] = CliVerbs.Backup,
        // (Purchase dates 09-09) `sync` already dates what it can; this is the catch-up verb for a library
        // that predates the feature, plus --force to re-walk every order page.
        ["dates"] = CliVerbs.Dates,
        // The queue's Paused fact and the saved scope, from the command line (writer verbs: they take the lock).
        ["pause"] = CliVerbs.Pause,
        ["resume"] = CliVerbs.Resume,
        ["scope"] = CliVerbs.Scope,
    };
    // Research verbs (09-08): API probes and render demos used while building Grog. Reachable with
    // GROG_DEV_VERBS=1, never listed in help, never in a scheduled caller's way.
    if (Environment.GetEnvironmentVariable("GROG_DEV_VERBS") == "1")
    {
        verbs["resolvenames"] = CliVerbs.Resolvenames;
        verbs["artprobe"] = CliVerbs.Artprobe;
        verbs["extratypes"] = CliVerbs.Extratypes;
        verbs["cloudprobe"] = CliVerbs.Cloudprobe;
        verbs["renderdemo"] = CliVerbs.Renderdemo;
        verbs["mark-new"] = CliVerbs.MarkNew;         // (New items 09-09) replay the New badge in testing
        verbs["ordersprobe"] = CliVerbs.Ordersprobe;  // (New items 09-09) research: does order history carry purchase dates?
    }
    // The App has priority over the profile (09-08): a verb that can write the manifest refuses while Grog is
    // open, says so on stderr / the JSON envelope, in BOTH logs (the CLI's and the App's grog.log, for anything
    // parsing them) and leaves a note the App turns into a toast. Read-only verbs run regardless.
    var readOnlyVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "help", "status", "whoami", "list", "show", "runs", "report", "health", "delisted", "changes", "changelog",
          "orphans", "resolve", "resolvenames", "serials", "extratypes", "artprobe", "cloudprobe", "ordersprobe", "renderdemo", "policy" };
    using var presence = readOnlyVerbs.Contains(command) ? null
        : Grog.Core.Storage.ProcessPresence.TryHold(paths.ConfigDir, Grog.Core.Storage.ProcessPresence.CliLockName, command);
    if (!readOnlyVerbs.Contains(command)
        && Grog.Core.Storage.ProcessPresence.Holder(paths.ConfigDir, Grog.Core.Storage.ProcessPresence.AppLockName) is { } app)
    {
        var msg = $"'{command}' was not run: the Grog app is open (pid {app.Pid}) and owns this profile. Close Grog, or use the app.";
        Out.Error(msg);   // stderr + the CLI log
        try { new Grog.Core.Storage.LogFile(paths.ConfigDir).Append(DateTimeOffset.Now, "cli", true, $"grogcli {msg}"); } catch { }
        Grog.Core.Storage.ProcessPresence.WriteBlockedNote(paths.ConfigDir, command);
        if (jsonOut) EmitJsonError(command, "AppRunning", msg, (int)Grog.Core.Cli.ExitCode.AppRunning);
        return (int)Grog.Core.Cli.ExitCode.AppRunning;
    }
    // Two writer CLIs on one profile would race the manifest: the second refuses with the same code the App
    // path uses. A lock the filesystem cannot take leaves no holder note and runs (fail open, as TryHold says).
    if (presence is { Held: false }
        && Grog.Core.Storage.ProcessPresence.Holder(paths.ConfigDir, Grog.Core.Storage.ProcessPresence.CliLockName) is { } other)
    {
        var msg = $"'{command}' was not run: another grogcli (pid {other.Pid}, '{other.Note}') is using this profile. Wait for it to finish.";
        Out.Error(msg);
        if (jsonOut) EmitJsonError(command, "AppRunning", msg, (int)Grog.Core.Cli.ExitCode.AppRunning);
        return (int)Grog.Core.Cli.ExitCode.AppRunning;
    }
    var ctx = new CliContext(args, command, http, paths, rootFlag, silent, jsonOut, nonInteractive, browserFlag,
                             loginProvider, manifest, tokenStore, auth, api, sessions, firstAccount, defaultTokenPath);
    var code = await (verbs.TryGetValue(command, out var verb) ? verb : CliVerbs.Help)(ctx);
    if (code != CliVerbs.Continue) return code;
    // A command that reported a usage/argument problem exits 2 (scriptable), not 0.
    return Out.HadUsageError ? (int)Grog.Core.Cli.ExitCode.UsageError : 0;
}
catch (Grog.Core.Auth.AuthExpiredException ex)
{
    // Distinct, quiet signal for schedulers: the session lapsed and needs a one-time re-login.
    if (jsonOut) EmitJsonError(command, "AuthExpired", ex.Message, (int)Grog.Core.Cli.ExitCode.AuthExpired);
    else Console.Error.WriteLine(ex.Message);
    return (int)Grog.Core.Cli.ExitCode.AuthExpired;
}
catch (Exception ex)
{
    var code = (int)Grog.Core.Cli.CliExit.ForException(ex);
    if (jsonOut) EmitJsonError(command, ex.GetType().Name, Grog.Core.Api.GogError.Describe(ex), code);
    else Console.Error.WriteLine($"error: {Grog.Core.Api.GogError.Describe(ex)}");
    return code;
}


/// <summary>Fixed-width download progress (apt/nuget style): [NN/NN] name [####----] PCT got/total speed.
/// The name column is fixed (longest queued label, capped) so all rows align; completed files keep a
/// permanent 100% line while the active file's live line finalizes in place.</summary>
internal sealed class DownloadRenderer
{
    private readonly int _total;
    private readonly int _nameCol;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, DownloadTask> _active = new();
    private int _completed, _failed, _skipped;
    private int _liveLines;   // height of the live block currently on screen (one line per in-flight file)
    private long _lastDraw;
    private bool _blockMode = true;   // false = demoted to the single-line view because the console is slow
    private const int RedrawMs = 120;   // ~8 fps: smooth to the eye, cheap on the console
    private readonly bool _interactive = !Console.IsOutputRedirected && !Out.Silent;

    private void WriteColored(string prefix, string name, string bracket, string stats, ConsoleColor barColor)
    {
        // Quiet runs get their pulse from QuietProgress on stderr; nothing here may touch a --json stdout.
        if (Out.Silent) return;
        // prefix (gray) + name (default) + bracket/percent (state color) + size (default).
        Out.Segment(prefix, ConsoleColor.DarkGray);
        Console.Write(name);
        Out.Segment(bracket, barColor);
        Console.Write(stats);
    }

    private const int MaxName = 64;
    private const int RightWidth = 16; // "  82%  71.5/87.0 MB"

    public DownloadRenderer(int total, int nameCol)
    {
        _total = total;
        _nameCol = Math.Clamp(nameCol, 16, MaxName);
    }

    public int CompletedCount => _completed;
    public int FailedCount => _failed;
    public int SkippedCount => _skipped;

    /// <summary>Every task that did not finish, kept so --json can report WHICH files failed and why --
    /// not just how many. The console view prints each failure as it happens and then forgets it.</summary>
    private readonly List<(string File, string State, string? Error)> _failures = new();
    public IReadOnlyList<(string File, string State, string? Error)> Failures => _failures;

    /// <summary>Quiet runs (--silent, --json, piped) still need a pulse: a 20 GB file with no output looks
    /// exactly like a hang (owner, 09-04). One STDERR line per finished file and one heartbeat every
    /// <see cref="HeartbeatMs"/> while bytes move; stdout stays untouched for the JSON document.</summary>
    private const int HeartbeatMs = 10_000;
    private long _lastBeat = Environment.TickCount64;
    private void QuietProgress(DownloadTask t, bool terminal, bool starting = false)
    {
        if (_interactive) return;
        var now = Environment.TickCount64;
        if (!terminal && !starting && now - _lastBeat < HeartbeatMs) return;
        _lastBeat = now;
        var n = _completed + _failed + _skipped;
        if (starting)
            Console.Error.WriteLine($"  [{n:00}/{_total:00}] {"starting",-9} {Label(t)}  {Human(t.BytesTotal ?? t.File.ExpectedSizeBytes ?? 0)}");
        else if (terminal)
            Console.Error.WriteLine($"  [{n:00}/{_total:00}] {t.State.ToString().ToLowerInvariant(),-9} {Label(t)}");
        else
        {
            long got = 0, tot = 0;
            foreach (var a in _active.Values) { got += a.BytesReceived; tot += a.BytesTotal ?? 0; }
            Console.Error.WriteLine($"  [{n:00}/{_total:00}] {_active.Count} in flight, {Grog.Core.Format.ByteFormat.Size(got)} of {Grog.Core.Format.ByteFormat.Size(tot)}");
        }
    }

    public void Update(DownloadTask t)
    {
        lock (_lock)
        {
            if (t.State is DownloadTaskState.Completed or DownloadTaskState.Failed or DownloadTaskState.Canceled or DownloadTaskState.Skipped)
            {
                EndLiveLine();
                _active.Remove(t.Id);
                bool completed = t.State == DownloadTaskState.Completed;
                bool skippedNoOwner = t.State == DownloadTaskState.Skipped;
                if (completed) _completed++;
                else if (skippedNoOwner) _skipped++;   // no owner signed in: not done, but NOT a failure
                else
                {
                    _failed++;   // Failed OR Canceled -- either way it did not finish, so it counts as not-done
                    _failures.Add((Label(t), t.State.ToString(), t.Error));
                }

                QuietProgress(t, terminal: true);
                var n = _completed + _failed + _skipped;
                var prefix = $"[{n:00}/{_total:00}] ";
                if (completed)
                {
                    var (name, bracket, stats) = BuildParts(t, done: true);
                    WriteColored(prefix, name, bracket, stats, ConsoleColor.Green);
                }
                else if (skippedNoOwner)
                {
                    var name = PadName(Label(t)) + " ";
                    var bracket = $"[{Center("SKIPPED", BarWidth())}]";
                    var stats = t.SkipReason is not null ? $"  {Trunc(t.SkipReason, 40)}" : "";
                    WriteColored(prefix, name, bracket, stats, ConsoleColor.Yellow);
                }
                else
                {
                    var name = PadName(Label(t)) + " ";
                    var label = t.State == DownloadTaskState.Canceled ? "CANCELED" : "FAILED";
                    var bracket = $"[{Center(label, BarWidth())}]";
                    var stats = t.Error is not null ? $"  {Trunc(t.Error, 24)}" : "";
                    WriteColored(prefix, name, bracket, stats, ConsoleColor.Red);
                }
                if (!Out.Silent) Console.WriteLine();
                DrawActive();
            }
            else if (t.State is DownloadTaskState.Active)
            {
                bool isNew = !_active.ContainsKey(t.Id);
                _active[t.Id] = t;
                // Throttle progress repaints. Byte-progress events fire far faster than anyone can read, and
                // each repaint rewrites the whole block; redrawing on every one is what makes a parallel run
                // feel sluggish. A file ENTERING the block always redraws immediately -- that is news.
                QuietProgress(t, terminal: false, starting: isNew);
                var now = Environment.TickCount64;
                if (!isNew && now - _lastDraw < RedrawMs) return;
                _lastDraw = now;
                DrawActive();
            }
        }
    }

    /// <summary>Redraw the live block: ONE LINE PER IN-FLIGHT FILE, ordered by queue Index so lines do
    /// not jump around as throughput fluctuates.</summary>
    private void DrawActive()
    {
        if (!_interactive) return;
        EraseLiveBlock();
        if (_active.Count == 0) return;

        var prefix = $"[{_completed + _failed:00}/{_total:00}] ";
        var blank = new string(' ', prefix.Length);
        var live = _active.Values.OrderBy(x => x.Index).ToList();
        // Demoted (see EraseLiveBlock): show only the furthest-along file, on one line, as before.
        if (!_blockMode) live = live.OrderByDescending(x => x.BytesReceived).Take(1).ToList();
        for (int i = 0; i < live.Count; i++)
        {
            var t = live[i];
            bool starting = !(t.BytesReceived > 0 && t.BytesTotal is > 0);
            var (name, bracket, stats) = BuildParts(t, done: false);
            // Only the first line carries the N/total counter; the rest indent under it, so the block reads
            // as one group rather than repeating a count that is identical on every line.
            WriteColored(i == 0 ? prefix : blank, name, bracket, stats,
                         starting ? ConsoleColor.DarkGray : ConsoleColor.Yellow);
            Console.WriteLine();
        }
        _liveLines = live.Count;
    }

    /// <summary>Wipe the live block and put the cursor back at its first line, so the next thing written
    /// (a completed-file line, or the next redraw) starts where the block was. Best-effort: cursor moves can
    /// throw on an odd console, and a failed redraw must never take the download down with it.</summary>
    private void EraseLiveBlock()
    {
        if (!_interactive || _liveLines == 0) return;
        try
        {
            // ONE cursor read and two moves for the whole block, not two per line: on some terminals each
            // CursorTop read costs a round-trip cursor-position query, and at parallel-download event rates
            // that traffic is enough to stall the render.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var pad = new string(' ', Math.Max(0, GetWidth() - 1));
            int start = Math.Max(0, Console.CursorTop - _liveLines);
            Console.SetCursorPosition(0, start);
            for (int i = 0; i < _liveLines; i++) Console.WriteLine(pad);
            Console.SetCursorPosition(0, start);

            // Self-demotion: Update() runs on the download worker threads, and some terminals answer each
            // cursor move with a ~tens-of-ms query -- if repainting is that costly, drop to the single-line
            // view for the rest of the run so the render is never a brake on the backup.
            if (sw.ElapsedMilliseconds > 50) _blockMode = false;
        }
        catch { /* console without cursor addressing - the block just stays on screen */ }
        _liveLines = 0;
    }

    /// <summary>Returns the line split into (name, bracket+percent, size) so each can be colored.</summary>
    private (string name, string bracket, string stats) BuildParts(DownloadTask t, bool done)
    {
        var name = PadName(Label(t)) + " ";
        int barW = BarWidth();
        bool flowing = done || (t.BytesReceived > 0 && t.BytesTotal is > 0);

        if (!flowing)
            return (name, $"[{StartingBanner(barW)}]", "");

        double pct = done ? 100 : t.PercentComplete;
        var bar = $"[{Bar(pct, barW)}] {pct,3:0}%";
        var size = t.BytesTotal is { } b ? SizePair(done ? b : t.BytesReceived, b) : Human(t.BytesReceived);
        var stats = $"  {size}";
        // Cap total width to avoid wrap.
        int max = GetWidth() - 10;
        int used = name.Length + bar.Length + stats.Length;
        if (used > max) stats = stats.Length > (used - max) ? stats[..(stats.Length - (used - max))] : "";
        return (name, bar, stats);
    }

    /// <summary>"71.5/87.0 MB" -- shared unit, single suffix (the renderer's fixed right column).</summary>
    private static string SizePair(long got, long total) => ByteFormat.CompactPair(got, total);

    /// <summary>Progress bar growing an arrow: "" -> "--->" -> "###--->". The ">" tip rides the leading
    /// edge with up to 3 dashes behind it; empty at 0%, solid "#" at 100%.</summary>
    private static string Bar(double pct, int width)
    {
        int fill = Math.Clamp((int)Math.Round(pct / 100 * width), 0, width);
        if (fill >= width) return new string('#', width);   // 100%: solid
        if (fill <= 0) return new string(' ', width);        // 0%: empty
        int dashes = Math.Min(3, fill - 1);                  // arrow body behind the ">" tip
        int solid = fill - dashes - 1;                       // "#" filled in behind the arrow
        int spaces = width - fill;                           // blank ahead
        return new string('#', solid) + new string('-', dashes) + ">" + new string(' ', spaces);
    }

    /// <summary>"----*** STARTING ***----" centered to fill the bar width.</summary>
    private static string StartingBanner(int width)
    {
        const string core = "*** STARTING ***";
        if (width <= core.Length) return Center("STARTING", width);
        int dashes = width - core.Length;
        int left = dashes / 2, rightD = dashes - left;
        return new string('-', left) + core + new string('-', rightD);
    }

    private int BarWidth()
    {
        int w = GetWidth();
        // prefix "[NN/NN] " = 9, name col, " [" + "] " = 4, right stats, + 2 safety margin so the
        // full line stays under the console width and never wraps.
        int bar = w - 9 - _nameCol - 4 - RightWidth - 2;
        return Math.Clamp(bar, 8, 30);
    }

    private string PadName(string label) =>
        label.Length > _nameCol ? label[..(_nameCol - 1)] + "…" : label.PadRight(_nameCol);

    private static string Center(string s, int width)
    {
        if (s.Length >= width) return s[..width];
        int pad = (width - s.Length) / 2;
        return new string(' ', pad) + s + new string(' ', width - s.Length - pad);
    }

    private static string Label(DownloadTask t)
    {
        if (!string.IsNullOrEmpty(t.ResolvedFileName))
            return t.ResolvedFileName!;
        var meta = string.Join(", ", new[] { t.File.Os, t.File.Language }.Where(s => !string.IsNullOrEmpty(s)));
        return meta.Length > 0 ? $"{t.File.Name} ({meta})" : t.File.Name;
    }

    private void EndLiveLine() => EraseLiveBlock();

    private static int GetWidth()
    {
        try { return Console.WindowWidth is > 40 and < 240 ? Console.WindowWidth : 80; } catch { return 80; }
    }

    private static string Trunc(string s, int n) => CliText.Trunc(s, n);
    private static string Human(long b) => ByteFormat.Size(b);   // one formatter product-wide

    public void Finish()
    {
        lock (_lock) { EndLiveLine(); }
    }

}

/// <summary>
/// Live console progress: a spinner (proves the process is alive even when one API call
/// stalls), a bar, percentage, count, and a rolling ETA. Redraws in place on one line.
/// </summary>
internal sealed class ConsoleProgress
{
    private static readonly char[] Spinner = { '|', '/', '-', '\\' };
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    private int _spin;
    private bool _reported;
    private bool _redirected = Console.IsOutputRedirected || Out.Silent;

    public void Report(int completed, int total)
    {
        _reported = true;
        _spin = (_spin + 1) % Spinner.Length;
        double frac = total == 0 ? 0 : (double)completed / total;
        int pct = (int)(frac * 100);

        // ETA from average time per completed item.
        string eta = "";
        if (completed > 0 && completed < total)
        {
            var perItem = _sw.Elapsed.TotalSeconds / completed;
            var remain = TimeSpan.FromSeconds(perItem * (total - completed));
            eta = remain.TotalMinutes >= 1
                ? $" ~{(int)remain.TotalMinutes}m{remain.Seconds:00}s left"
                : $" ~{remain.Seconds}s left";
        }

        if (_redirected)
        {
            // Non-interactive (piped/CI) or quiet (--silent/--json): occasional milestones, no carriage
            // returns, and on STDERR so a --json stdout stays one document.
            if (completed == total || completed % 25 == 0)
                Console.Error.WriteLine($"  synced {completed}/{total} ({pct}%)");
            return;
        }

        const int barWidth = 28;
        int filled = (int)(frac * barWidth);
        var bar = new string('#', filled) + new string('.', barWidth - filled);
        Console.Write($"\r  {Spinner[_spin]} [{bar}] {pct,3}%  {completed}/{total}{eta}      ");
    }

    public void Finish()
    {
        if (!_reported) return;   // a scan that failed before its first page has nothing to report
        if (!_redirected)
            Console.Write("\r  " + new string(' ', 70) + "\r"); // clear the line
        (_redirected ? Console.Error : Console.Out).WriteLine($"  completed in {_sw.Elapsed.TotalSeconds:F0}s");
    }
}

// ---- login providers ----

internal sealed class CredentialLoginProvider(IInteractiveLoginProvider fallback) : IInteractiveLoginProvider
{
    public async Task<string> AcquireAuthorizationCodeAsync(Uri loginUrl, CancellationToken ct = default)
    {
        Console.Write("GOG username (email): ");
        var username = Console.ReadLine() ?? "";
        Console.Write("GOG password (typing hidden): ");
        var password = ReadHidden();
        try
        {
            return await GogFormLogin.LoginAsync(username, password, PromptTwoFactorAsync, ct);
        }
        catch (Exception ex) when (ex is CaptchaRequiredException or LoginFlowChangedException)
        {
            Console.WriteLine();
            Console.WriteLine($"{ex.Message}");
            Console.WriteLine("Falling back to browser login…");
            return await fallback.AcquireAuthorizationCodeAsync(loginUrl, ct);
        }
    }

    private static Task<string> PromptTwoFactorAsync(CancellationToken ct)
    {
        Console.Write("GOG emailed you a 4-character security code. Enter it: ");
        return Task.FromResult(Console.ReadLine()?.Trim() ?? "");
    }

    private static string ReadHidden()
    {
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        return new string(chars.ToArray());
    }
}

internal sealed class BrowserLoginProvider : IInteractiveLoginProvider
{
    public Task<string> AcquireAuthorizationCodeAsync(Uri loginUrl, CancellationToken ct = default)
    {
        Console.WriteLine("Opening the GOG login page in your browser…");
        Console.WriteLine("(If it doesn't open, copy this URL manually:)");
        Console.WriteLine();
        Console.WriteLine($"  {loginUrl}");
        Console.WriteLine();
        TryOpenBrowser(loginUrl.ToString());
        Console.WriteLine("After logging in you'll land on a mostly-blank page.");
        Console.WriteLine("Copy that page's FULL address from the browser bar and paste it here.");
        Console.Write("> ");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pasted = Console.ReadLine();
            var code = GogOAuth.ExtractCode(pasted ?? "");
            if (code is not null) return Task.FromResult(code);
            Console.Write("Couldn't find a code in that. Paste the full URL (contains 'code='): ");
        }
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
        }
        catch { }
    }
}

/// <summary>The `--version` readout, from this assembly.</summary>
internal static class CliVersion
{
    public static string Current()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var attr = (System.Reflection.AssemblyInformationalVersionAttribute?)System.Attribute.GetCustomAttribute(
            asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
        return CliText.TrimVersion(attr?.InformationalVersion, asm.GetName().Version?.ToString());
    }
}

/// <summary>Text helpers shared by the top-level verbs and the renderer classes (a top-level local function
/// is invisible inside a class, which is how Trunc came to exist twice).</summary>
internal static class CliText
{
    /// <summary>MAJOR.MINOR.PATCH from the assembly's informational version: build metadata and a fourth
    /// build-counter part are dropped, the same trim the App shows.</summary>
    public static string TrimVersion(string? info, string? fallback) => Grog.Core.Format.VersionText.Trim3(info, fallback);

    /// <summary>--json was passed: failures go to stderr as JSON (Fail) and human chatter is suppressed.</summary>
    public static bool JsonMode;
    public static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Download;
using Grog.Core.Format;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Verify;

namespace Grog.Cli;

/// <summary>
/// One method per verb, dispatched from Program.cs by name. Until 09-02 these were 1,880 lines of case
/// bodies in a single switch; the bodies are unchanged, each reads what it needs off the
/// <see cref="CliContext"/>. A verb returns an exit code, or <see cref="Continue"/> to take the switch's
/// old fall-through ("usage error seen -> 2, else 0").
/// </summary>
internal static partial class CliVerbs
{
    /// <summary>The verb finished without choosing an exit code (the old <c>break;</c>).</summary>
    internal const int Continue = int.MinValue;
    private static readonly object SettleGate = new();

    internal static async Task<int> Login(CliContext ctx)
    {
        var paths = ctx.Paths;
        var manifest = ctx.Manifest;
        var auth = ctx.Auth;
        var api = ctx.Api;
        var defaultTokenPath = ctx.DefaultTokenPath;
    {
        await auth.EnsureAuthenticatedAsync();
        var user = await api.GetUserInfoAsync();
        var svc = new Grog.Core.Auth.AccountService(manifest, paths);
        var acct = await svc.AddOrUpdateAsync("", user.Username, user.Username);
        var home = svc.TokenPathFor(acct.Id);
        if (!string.Equals(defaultTokenPath, home, StringComparison.OrdinalIgnoreCase)
            && System.IO.File.Exists(defaultTokenPath))
        {
            System.IO.File.Copy(defaultTokenPath, home, overwrite: true);
            try { System.IO.File.Delete(defaultTokenPath); } catch { }
        }
        Out.Success($"Logged in as {user.Username} (user id {user.UserId}).");
        Out.Dim($"Tokens stored at: {home}");
        Out.Dim("(Your password was not stored anywhere.)");
        return Continue;
    }
    }

    internal static async Task<int> Status(CliContext ctx)
    {
        var args = ctx.Args;
        var command = ctx.Command;
        var silent = ctx.Silent;
        var jsonOut = ctx.JsonOut;
        var tokenStore = ctx.TokenStore;
        var auth = ctx.Auth;
        var api = ctx.Api;
    {
        // Immediate feedback BEFORE the token read: a dead/locked keyring can eat timeouts and leave
        // the command silent for the whole wait. Suppressed for --json so machine output stays pure.
        if (!jsonOut) Out.Dim("Checking stored session…");
        // Local freshness readout first -- no network, so it works offline and for monitoring.
        var stored = await tokenStore.LoadAsync();
        if (jsonOut)
        {
            object session;
            if (stored is not null)
            {
                var fr = Grog.Core.Cli.TokenFreshness.For(stored, DateTimeOffset.UtcNow);
                session = new { loggedIn = true, lastRefreshed = fr.LastRefreshed, hasAccessToken = true, hasRefreshToken = fr.HasRefreshToken };   // token material never leaves the store
            }
            else session = new { loggedIn = false };

            // No readable session: report and STOP -- a read-only verb must never prompt, and a cron
            // shell would hang on stdin. Failure rides STDERR; stdout under --json is the success doc only.
            if (stored is null)
            {
                EmitJsonError(command, "auth", "not logged in -- run 'grogcli login'", (int)Grog.Core.Cli.ExitCode.AuthExpired);
                return (int)Grog.Core.Cli.ExitCode.AuthExpired;
            }
            if (args.Contains("--local", StringComparer.OrdinalIgnoreCase)) { EmitJson(new { session }); return Continue; }
            try
            {
                var u = await api.GetUserInfoAsync();
                var ownedIds = await api.GetOwnedProductIdsAsync();
                EmitJson(new { session, user = new { u.Username, u.Email }, ownedProducts = ownedIds.Count });
            }
            catch (Exception ex) { EmitJson(new { session, error = ex.Message }); }
            return Continue;
        }
        if (stored is not null)
        {
            var r = Grog.Core.Cli.TokenFreshness.For(stored, DateTimeOffset.UtcNow);
            Out.Heading("Session:");
            Console.WriteLine($"  last refreshed .... {r.LastRefreshed}");
            Console.WriteLine("  access token ...... (stored)");
            Console.WriteLine($"  refresh token ..... {(r.HasRefreshToken ? "present (enables unattended renewal)" : "MISSING -- re-login needed")}");
        }
        else
        {
            // Same rule as the json branch: no session, no network, NO PROMPT. Exit 4 so a script
            // can distinguish "needs login" from success.
            Out.Dim("No stored session -- run 'grogcli login'.");
            return (int)Grog.Core.Cli.ExitCode.AuthExpired;
        }

        // --local: freshness only, skip the live identity call (handy for scheduled monitoring).
        if (args.Contains("--local", StringComparer.OrdinalIgnoreCase)) return Continue;

        var user = await api.GetUserInfoAsync();
        var owned = await api.GetOwnedProductIdsAsync();
        Console.WriteLine($"{user.Username} <{user.Email}>");
        Console.WriteLine($"Owned products: {owned.Count}");
        return Continue;
    }
    }

    internal static async Task<int> Refresh(CliContext ctx)
    {
        var auth = ctx.Auth;
    {
        // Keepalive: force a token refresh to hold the unattended window open. Rotates the refresh
        // token. Exits 4 if the session has truly lapsed and needs a fresh login.
        await auth.RefreshNowAsync();
        Out.Success("Session refreshed (token rotated).");
        Console.WriteLine("  access token ...... (stored)");
        return Continue;
    }
    }

    internal static async Task<int> Logout(CliContext ctx)
    {
        var auth = ctx.Auth;
        await auth.SignOutAsync();
        Out.Success("Signed out; stored tokens removed.");
        return Continue;
    }

    internal static async Task<int> Sync(CliContext ctx)
    {
        var args = ctx.Args;
        var paths = ctx.Paths;
        var manifest = ctx.Manifest;
        await manifest.LoadAsync();
        _ = ctx.Layout();   // establish the primary root on first sync
        bool syncDebug = args.Contains("--debug", StringComparer.OrdinalIgnoreCase);
        bool syncDetails = args.Contains("--details", StringComparer.OrdinalIgnoreCase);
        if (syncDetails)
            Out.Dim("--details: also capturing release date / in-development / OS / languages (slower).");
        Out.Dim($"Syncing library into {ctx.ContentRoot()}");

        // One scan path for CLI and App: Core runs every connected account (or the legacy single token) and
        // aggregates; this verb only draws the spinner and the summary.
        var failuresDir = Path.Combine(paths.ConfigDir, "_parse_failures");
        var host = new ConsoleBackupHost(syncDebug, syncDetails, failuresDir);
        var scan = await Grog.Core.Runs.LibraryScan.RunAsync(manifest, ctx.Api, ctx.Sessions, host, CancellationToken.None, datePurchases: true);
        host.FinishScan();
        // Storage registered before the library had items owes an import; the scan just gave it something to
        // match against (sweep 2 #28: only the App ran these).
        if (manifest.Read(m => Grog.Core.Verify.PendingImports.Runnable(m).Count) > 0)
            await Grog.Core.Runs.ImportRun.RunPendingAsync(manifest, host, CancellationToken.None);
        var r = scan.Aggregate;
        if (scan.Passes is { } passes)
        {
            int ranCount = passes.Count(p => p.Result is not null);
            int skippedCount = passes.Count(p => p.Skipped);
            int erroredCount = passes.Count(p => !p.Skipped && p.Result is null);
            Out.Dim($"Accounts: {ranCount} scanned"
                + (skippedCount > 0 ? $", {skippedCount} skipped" : "")
                + (erroredCount > 0 ? $", {erroredCount} failed" : "") + ".");
            if (scan.AllAccountsFailed)
            {
                return Fail("sync", Grog.Core.Cli.ExitCode.AuthExpired, "auth",
                    "No account could be scanned -- sign in (GUI Accounts page or 'grog login' / 'grog account add').");
            }
        }

        // end-of-sync "what changed this run" one-liner (derived from diff state).
        var changesSummary = new Grog.Core.Sync.ChangesService(manifest).OneLineSummary();
        Out.Dim($"Changes: {changesSummary}");

        Out.Success($"Done. Seen {r.GamesSeen}, games with details {r.NewGames + (r.GamesSeen - r.NewGames - r.NoDetailsProducts - r.ParseFailures)}, " +
                   $"new games {r.NewGames}, new files {r.NewFiles}, updated files {r.UpdatedFiles}.");
        if (r.NoDetailsProducts > 0)
            Console.WriteLine($"Note: {r.NoDetailsProducts} owned product(s) have no direct downloads " +
                              "(DLC bundled under a base game, or bonus items). This is normal.");
        if (r.ParseFailures > 0)
        {
            Out.Warn($"WARNING: {r.ParseFailures} product(s) could not be parsed and were skipped: " +
                 string.Join(", ", r.FailedProductIds));
            if (syncDebug)
                Out.Dim($"Raw JSON of failures saved to: {failuresDir}");
            else
                Console.WriteLine("Re-run 'sync --debug' to save the offending JSON for diagnosis.");
        }
        Out.Dim($"Manifest: {paths.ManifestPath}");
        return Continue;
    }

    internal static async Task<int> List(CliContext ctx)
    {
        var args = ctx.Args;
        var jsonOut = ctx.JsonOut;
        var manifest = ctx.Manifest;
    {
        await manifest.LoadAsync();
        var items = manifest.Current.Items.OrderBy(i => i.Title).AsEnumerable();

        // Optional: list --type game|dlc|pack|mod|movie
        var typeArg = GetArgValue(args, "--type");
        if (typeArg is not null && Enum.TryParse<ProductType>(typeArg, ignoreCase: true, out var wantType))
            items = items.Where(i => i.Type == wantType);

        // (New items 09-09) list --new: only items flagged New. Narrows the SAME output, human and JSON.
        bool onlyNew = args.Contains("--new", StringComparer.OrdinalIgnoreCase);
        if (onlyNew) items = items.Where(i => i.IsNew);

        var list = items.ToList();
        if (jsonOut)
        {
            EmitJson(list.Select(g => new
            {
                id = g.GogId,
                title = g.Title,
                type = g.Type.ToString(),
                status = g.Status.ToString(),
                isNew = g.IsNew,   // (New items 09-09)
                files = g.Files.Count,
                extras = g.Files.Count(f => f.Kind == FileKind.Extra),
                os = g.Files.Where(f => f.Kind == FileKind.Installer).Select(f => f.Os).Distinct().OrderBy(s => s).ToArray(),
            }).ToArray());
            return Continue;
        }
        if (list.Count == 0) { Out.Warn(onlyNew ? "Nothing is flagged New." : "Nothing to show -- run 'sync' first (or check --type)."); return Continue; }
        foreach (var g in list)
        {
            var oses = string.Join("/", g.Files.Where(f => f.Kind is FileKind.Installer)
                .Select(f => f.Os).Distinct().OrderBy(s => s));
            int extras = g.Files.Count(f => f.Kind == FileKind.Extra);
            Console.WriteLine($"  [{Symbol(g.Status)}] {g.Type.ToString().ToUpper(),-5} {Trunc(g.Title, 44),-44}  {oses,-18}  " +
                              $"{g.Files.Count,3} files  {(extras > 0 ? extras + " extras" : "")}");
        }
        Console.WriteLine($"\n{list.Count} item(s){(typeArg is not null ? $" of type {typeArg}" : "")}{(onlyNew ? " flagged New" : "")}.");
        return Continue;
    }
    }

    internal static async Task<int> Resolve(CliContext ctx)
    {
        var paths = ctx.Paths;
        var manifest = ctx.Manifest;
        var api = ctx.Api;
    {
        await manifest.LoadAsync();
        Console.WriteLine("Syncing first to find products with no direct downloads…");
        var sync = new LibrarySyncService(api, manifest);
        var syncProgress = new ConsoleProgress();
        sync.Progress += p => syncProgress.Report(p.Completed, p.Total);
        var sr = await sync.RunAsync();
        syncProgress.Finish();

        var ids = sr.NoDetailProductIds;
        if (ids.Count == 0) { Console.WriteLine("Every owned product has downloadable details -- nothing to resolve."); return Continue; }

        Console.WriteLine($"\nResolving {ids.Count} products and checking backup coverage…");
        var resolver = new NoDownloadResolver(api);
        var resolveProgress = new ConsoleProgress();
        resolver.Progress += p => resolveProgress.Report(p.Completed, p.Total);
        var resolved = await resolver.ResolveAsync(ids, manifest.Current.Items);
        resolveProgress.Finish();

        Console.WriteLine();
        var csv = new List<string> { "id,title,type,status,coverage,covered_by,store_url" };
        foreach (var r in resolved)
        {
            var verdict = r.Coverage switch
            {
                CoverageKind.SameProduct => $"= same as game \"{r.CoveredBy}\"",
                CoverageKind.LikelyCoveredByBaseGame => $"covered by \"{r.CoveredBy}\"",
                CoverageKind.NotObviouslyCovered => "** NOT obviously covered -- check this **",
                _ => "? undetermined",
            };
            Console.WriteLine($"  {r.Id,-12} {Trunc(r.Title, 40),-40} {r.Type,-6} {verdict}");
            csv.Add($"{r.Id},\"{r.Title.Replace("\"", "\"\"")}\",{r.Type},{r.Status}," +
                    $"{r.Coverage},\"{r.CoveredBy}\",{r.StoreUrl}");
        }

        int flagged = resolved.Count(r => r.Coverage == CoverageKind.NotObviouslyCovered);
        Console.WriteLine();
        Console.WriteLine(flagged == 0
            ? "All no-download products appear covered by base games you're backing up."
            : $"{flagged} product(s) NOT obviously covered -- review the ** lines above.");
        var csvPath = Path.Combine(paths.ConfigDir, "no_download_products.csv");
        await File.WriteAllLinesAsync(csvPath, csv);
        Console.WriteLine($"Saved: {csvPath}");
        return Continue;
    }
    }

    internal static async Task<int> Download(CliContext ctx)
    {
        var args = ctx.Args;
        var manifest = ctx.Manifest;
        await manifest.LoadAsync();
        bool Has(string f) => args.Contains(f, StringComparer.OrdinalIgnoreCase);

        // --id / --title narrow to games; everything else is a Core option. The run itself (selection,
        // placement, engine, settlement, journal, checkpoints) is BackupRun -- the same code the App runs.
        HashSet<long>? onlyGames = null;
        var idArg = GetArgValue(args, "--id");
        var titleArg = GetArgValue(args, "--title");
        if (idArg is not null)
            onlyGames = manifest.Current.Items.Where(i => i.GogId.ToString() == idArg
                || string.Equals(i.Title, idArg, StringComparison.OrdinalIgnoreCase)).Select(i => i.GogId).ToHashSet();
        else if (titleArg is not null)
            onlyGames = manifest.Current.Items.Where(i => i.Title.Contains(titleArg, StringComparison.OrdinalIgnoreCase))
                .Select(i => i.GogId).ToHashSet();

        // (S2.3) defaults come from the library's transfer settings, the same ones the App shows: a fixed worker
        // count when Auto is off (Auto = the CLI's own 3), and the bandwidth cap when enabled. Flags override.
        var xfer = manifest.Current.Transfer;
        int parallel = xfer.FixedConcurrency ?? 3;
        if (GetArgValue(args, "--parallel") is { } pStr && int.TryParse(pStr, out var pv)) parallel = Math.Clamp(pv, 1, 8);
        long limitBps = xfer.BytesPerSecondLimit;
        if (GetArgValue(args, "--limit-mbps") is { } lim && double.TryParse(lim, out var mbps)) limitBps = (long)(mbps * 1024 * 1024);
        int? keepLargest = null;
        if (Has("--largest"))
            keepLargest = int.TryParse(GetArgValue(args, "--largest"), out var nlarge) && nlarge > 0 ? nlarge : 1;
        if (Has("--debug")) ctx.Api.DebugTrace = msg => Console.WriteLine($"    [dbg] {msg}");

        var options = new Grog.Core.Runs.BackupRunOptions(
            Scan: Grog.Core.Runs.ScanPolicy.Never,
            UpdatesOnly: Has("--updates"),
            RetryFailed: Has("--retry-failed"),
            KeepLargest: keepLargest,
            OnlyGameIds: onlyGames,
            RunScope: BuildScope(args, manifest.Current),
            IgnoreSavedScope: Has("--ignore-saved-scope"),
            FixedConcurrency: parallel,
            DeviceMonitor: GetArgValue(args, "--parallel") is null ? new Grog.Core.Download.DeviceWriteMonitor() : null,
            BytesPerSecondLimit: limitBps,
            JournalCommand: "download");

        if (Has("--dry-run"))
        {
            // Selection only: nothing is queued, placed or saved.
            var selection = Grog.Core.Runs.BackupQueueBuilder.Build(manifest.Current, ctx.Layout(), options);
            if (selection.Count == 0) { Out.Warn("Nothing to download (all present, or filters excluded everything)."); return Continue; }
            var vol = new Grog.Core.Volumes.VolumeService(manifest);
            var needByRoot = new Dictionary<string, long>();
            foreach (var (_, f) in selection)
            {
                var rid = manifest.Current.Routing.ResolveRootId(f.GameGogId, f.Kind, manifest.Current.PrimaryRootId!, manifest.Current.ExtrasLayout);
                needByRoot[rid] = needByRoot.GetValueOrDefault(rid) + (f.ExpectedSizeBytes ?? 0);
            }
            long totalBytes = needByRoot.Values.Sum();
            Out.Heading($"Dry run -- would download {selection.Count} file(s), ~{ByteFormat.Size(totalBytes)} total:");
            foreach (var (rid, bytes) in needByRoot)
            {
                var label = vol.FindRoot(rid)?.Label ?? rid[..6];
                Console.WriteLine($"  {label,-16} {ByteFormat.Size(bytes)}");
            }
            Out.Dim("No files were downloaded. Remove --dry-run to fetch.");
            return Continue;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("\nStopping…"); };

        var host = new ConsoleBackupHost(false, false, null) { EngineDebug = Has("--debug") };
        var run = new Grog.Core.Runs.BackupRun(manifest, ctx.Api, ctx.Http, ctx.Sessions, ctx.Layout(), ctx.ContentRoot(), ctx.Paths.ConfigDir, host);
        var r = await run.RunAsync(options, cts.Token);
        return DownloadSummary("download", ctx, r, host);
    }

    /// <summary>The console/JSON tail shared by `download` and `backup`: counts, failures, run log, exit code.</summary>
    private static int DownloadSummary(string command, CliContext ctx, Grog.Core.Runs.BackupRunResult r, ConsoleBackupHost host)
    {
        if (r.Error is { } err)
            return Fail(command, Grog.Core.Cli.CliExit.ForException(err), "error", Grog.Core.Api.GogError.Describe(err));
        if (r.NothingFits)
            return Fail(command, Grog.Core.Cli.ExitCode.InsufficientSpace, "space",
                $"Won't fit: {r.HeldNoSpace} file(s), {Grog.Core.Format.ByteFormat.Size(r.HeldNoSpaceBytes)}, have no room on any storage. Free space or add a device.");
        if (r.NothingToDo)
        {
            if (ctx.JsonOut) EmitJson(new { command, queued = 0, completed = 0, failed = 0, skipped = 0, failures = Array.Empty<object>(), runLog = (string?)null, outcome = r.Outcome.ToString() });
            else Out.Warn("Nothing to download (all present, or filters excluded everything).");
            return Continue;
        }
        var failures = host.Render?.Failures ?? Array.Empty<(string File, string State, string? Error)>();
        if (!ctx.JsonOut && r.JournalPath is not null) Out.Dim($"Run log: {r.JournalPath}");
        // A stopped run is not a success (exit 1, so a script does not report a partial backup as done); files
        // that failed exit 8 whether or not they will retry next run; held-for-space alone exits 7.
        int failed = r.NotLanded;
        int exit = r.Canceled ? (int)Grog.Core.Cli.ExitCode.Error
            : failed > 0 ? (int)Grog.Core.Cli.ExitCode.IntegrityFailed
            : r.HeldNoSpace > 0 ? (int)Grog.Core.Cli.ExitCode.InsufficientSpace
            : (int)Grog.Core.Cli.ExitCode.Success;
        if (ctx.JsonOut)
        {
            // The run's outcome as data: how much got through, and exactly what didn't. One shape every time.
            EmitJson(new
            {
                command,
                queued = r.Queued,
                completed = r.Completed,
                failed,
                skipped = r.Skipped,   // every owner signed out; not a failure, waits for a sign-in
                unavailable = r.Refused,
                heldNoSpace = r.HeldNoSpace,
                failures = failures.Select(f => new { file = f.File, state = f.State, error = f.Error }),
                runLog = r.JournalPath,
                outcome = r.Outcome.ToString(),
                exitCode = exit,
            });
            return exit;
        }
        var notes = new List<string>();
        if (r.Skipped > 0) notes.Add($"{r.Skipped} skipped (owner signed out)");
        if (r.Refused > 0) notes.Add($"{r.Refused} unavailable (GOG refused)");
        if (r.HeldNoSpace > 0) notes.Add($"{r.HeldNoSpace} held (no space)");
        var tail = notes.Count > 0 ? ", " + string.Join(", ", notes) : "";
        var line = $"Manifest updated. {r.Completed} completed, {failed} failed{tail}.";
        if (r.Canceled) Out.Warn("Stopped. " + line);
        else if (exit != 0) Out.Warn(line);
        else Out.Success(line);
        return exit == 0 ? Continue : exit;
    }

    internal static async Task<int> Resolvenames(CliContext ctx)
    {
        var args = ctx.Args;
        var silent = ctx.Silent;
        var manifest = ctx.Manifest;
        var tokenStore = ctx.TokenStore;
        var auth = ctx.Auth;
        var api = ctx.Api;
    {
        // Diagnostic: resolve real filenames (+ sizes) for every file in the library, timing the pass
        // and caching into ResolvedFileName; shows a live counter and reports timing + a sample.
        await manifest.LoadAsync();
        var files = manifest.Current.Items
            .SelectMany(i => i.Files.Select(f => (item: i, file: f)))
            .Where(x => !string.IsNullOrEmpty(x.file.FileKey))
            .ToList();
        int limit = int.TryParse(GetArgValue(args, "--limit"), out var lim) ? lim : files.Count;
        files = files.Take(limit).ToList();
        if (files.Count == 0) { Out.Dim("No files to resolve -- scan your library first."); return Continue; }

        // Pre-flight auth WITHOUT triggering an interactive prompt: a missing/expired token would
        // otherwise send EnsureAuthenticatedAsync into a blocking console login and look like a hang.
        var storedToken = await tokenStore.LoadAsync();
        if (storedToken is null)
        {
            Out.Error("Not logged in. Open the Grog app and connect your GOG account (or run 'grog login'), then re-run this.");
            return Continue;
        }
        if (Grog.Core.Auth.GogAuthService.IsExpired(storedToken))
            Out.Dim("Stored token is expired; attempting a silent refresh. If this stalls, log in via the app first.");

        Console.Write("Authenticating… ");
        var authSw = System.Diagnostics.Stopwatch.StartNew();
        try { await auth.EnsureAuthenticatedAsync(); authSw.Stop(); Console.WriteLine($"ok ({authSw.ElapsedMilliseconds} ms)"); }
        catch (Exception ex) { Console.WriteLine(); Out.Error($"Auth failed: {Grog.Core.Api.GogError.Describe(ex)}  --  log in via the app and retry."); return Continue; }

        int concurrency = int.TryParse(GetArgValue(args, "--concurrency"), out var cc) ? Math.Clamp(cc, 1, 16) : 8;
        Out.Heading($"Resolving filenames for {files.Count} file(s), {concurrency}-way concurrent…");
        bool verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        // Surface the resolver's internal phases. Note: with concurrency the phase lines interleave;
        // run with --concurrency 1 for a clean single-file trace.
        if (verbose) api.DebugTrace = msg => Console.WriteLine($"      · {msg}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int done = 0, ok = 0, failed = 0; long totalBytes = 0, slowestMs = 0;
        string slowestName = "";
        var samples = new List<string>();
        var sampleLock = new object();
        using var gate = new System.Threading.SemaphoreSlim(concurrency);

        // Fire all files as tasks, gated to `concurrency` at once -- same shape as the GUI's parallel
        // metadata pull, so one slow request never blocks the rest.
        var tasks = files.Select(async x =>
        {
            var (item, file) = x;
            await gate.WaitAsync();
            var fsw = System.Diagnostics.Stopwatch.StartNew();
            string outcome;
            try
            {
                var r = await api.ResolveDownloadAsync(file.FileKey);
                fsw.Stop();
                if (!string.IsNullOrEmpty(r.FileName))
                {
                    file.ResolvedFileName = r.FileName;
                    if (r.ContentLength is { } len && len > 0) { file.ExpectedSizeBytes = len; System.Threading.Interlocked.Add(ref totalBytes, len); }
                    System.Threading.Interlocked.Increment(ref ok);
                    outcome = $"{r.FileName}  ({(r.ContentLength is { } l ? ByteFormat.Size(l) : "?")})";
                    lock (sampleLock) { if (samples.Count < 12) samples.Add(outcome); }
                }
                else { System.Threading.Interlocked.Increment(ref failed); outcome = "(no filename returned)"; }
            }
            catch (Exception ex)
            {
                fsw.Stop(); System.Threading.Interlocked.Increment(ref failed);
                outcome = $"FAILED: {Grog.Core.Api.GogError.Describe(ex)}";
            }
            finally { gate.Release(); }

            int n = System.Threading.Interlocked.Increment(ref done);
            if (fsw.ElapsedMilliseconds > System.Threading.Interlocked.Read(ref slowestMs))
                { System.Threading.Interlocked.Exchange(ref slowestMs, fsw.ElapsedMilliseconds); slowestName = item.Title; }
            var flag = fsw.ElapsedMilliseconds >= 3000 ? "  <== SLOW" : "";
            Console.WriteLine($"  [{n}/{files.Count}] {fsw.ElapsedMilliseconds,6} ms  (total {sw.Elapsed.TotalSeconds,5:F1}s)  {item.Title} -> {outcome}{flag}");
        });
        await Task.WhenAll(tasks);
        sw.Stop();
        await manifest.SaveAsync();

        Out.Success($"Done in {sw.Elapsed.TotalSeconds:F1}s  ·  {ok} resolved, {failed} failed  ·  " +
                    $"{(files.Count > 0 ? sw.ElapsedMilliseconds / (double)files.Count : 0):F0} ms/file wall-avg  ·  " +
                    $"slowest {slowestMs} ms ({slowestName})  ·  " +
                    $"{ByteFormat.Size(totalBytes)} sized.");
        if (slowestMs >= 3000)
            Out.Dim("A file taking multiples of a second is almost always the HEAD probe timing out on the CDN. " +
                    "Re-run with --verbose to see the downlink/HEAD phases per file.");
        return Continue;
    }
    }

    /// <summary>GOG's current checksum for a file, so a hash mismatch on a repacked build reads Outdated rather than
    /// Corrupt. Owner-picked session when named accounts exist; the default session otherwise. Null on any failure.</summary>
    internal static Func<GameFile, CancellationToken, Task<string?>> ServerChecksumFetcher(CliContext ctx)
        => async (f, ct) =>
        {
            try
            {
                var api = ctx.Api;
                if (ctx.Manifest.Current.Accounts.Count > 0 && ctx.Sessions.FirstAuthenticatedOwner(f) is { } owner)
                    api = ctx.Sessions.ApiFor(owner);
                var resolved = await api.ResolveDownloadAsync(f.FileKey, ct, probe: false);
                return resolved.ChecksumXmlUrl is { } url ? await api.FetchMd5Async(url, ct) : null;
            }
            catch { return null; }
        };

    internal static async Task<int> Verify(CliContext ctx)
    {
        var args = ctx.Args;
        var jsonOut = ctx.JsonOut;
        var manifest = ctx.Manifest;
        string ContentRoot() => ctx.ContentRoot();
        Grog.Core.Volumes.BackupLayout Layout() => ctx.Layout();
    {
        await manifest.LoadAsync();
        bool full = args.Contains("--full", StringComparer.OrdinalIgnoreCase);
        var prog = new ConsoleProgress();
        // Core's VerifyRun owns the pass; a server checksum fetcher lets a repack read as Outdated, not Corrupt.
        // Under --json stdout must carry ONLY the JSON document, so the progress bar stays off.
        var request = Grog.Core.Runs.VerifyRequest.Cli(full) with
        {
            Progress = jsonOut ? null : p => prog.Report(p.Done, p.Total),
            FetchCurrentServerMd5 = ServerChecksumFetcher(ctx),
        };
        Out.Dim($"Verifying local files ({(full ? "full MD5" : "fast size")} mode)…");
        var r = (await Grog.Core.Runs.VerifyRun.RunAsync(manifest, Layout(), ContentRoot(), request,
                                                         new ConsoleBackupHost(false, false, null), CancellationToken.None)).Verify;
        if (!jsonOut) prog.Finish();
        if (jsonOut)
        {
            EmitJson(new
            {
                mode = full ? "full" : "fast",
                checkedFiles = r.Checked,
                verified = r.Verified,
                sizeOnlyOk = r.SizeOnlyOk,
                corrupt = r.Corrupt,
                missing = r.Missing,
                offlineSkipped = r.OfflineSkipped,
            });
            return r.Corrupt > 0 || r.Missing > 0
                ? (int)Grog.Core.Cli.ExitCode.IntegrityFailed : (int)Grog.Core.Cli.ExitCode.Success;
        }
        var offlineNote = r.OfflineSkipped > 0 ? $"  ({r.OfflineSkipped} on offline roots, skipped)" : "";
        if (r.Corrupt > 0 || r.Missing > 0)
            Out.Warn($"Checked {r.Checked}: verified {r.Verified}, size-ok {r.SizeOnlyOk}, corrupt {r.Corrupt}, missing {r.Missing}.{offlineNote}");
        else
            Out.Success($"Checked {r.Checked}: verified {r.Verified}, size-ok {r.SizeOnlyOk}, corrupt 0, missing 0.{offlineNote}");
        if (r.Corrupt > 0 || r.Missing > 0)
        {
            return Fail("verify", Grog.Core.Cli.ExitCode.IntegrityFailed, "integrity",
                // 'download' never re-fetches a Corrupt file (BackupScope.NeedsFetch: corrupt waits for a fix
                // decision), so that advice repaired nothing (sweep 2 #32). 'fix' is the verb, per what was found.
                "Verification found corrupt or missing files. Run 'fix "
                + string.Join(' ', new[] { r.Corrupt > 0 ? "--corrupt" : null, r.Missing > 0 ? "--missing" : null }.Where(s => s is not null))
                + "' to re-download them.");
        }
        return Continue;
    }
    }

    internal static async Task<int> Import(CliContext ctx)
    {
        var args = ctx.Args;
        var paths = ctx.Paths;
        var manifest = ctx.Manifest;
    {
        await manifest.LoadAsync();
        var src = PositionalArgs(args.Skip(1)).FirstOrDefault();
        if (src is null)
        {
            Out.Usage("Usage: import <folder> [--mode preview|adopt|adopt-orphans] [--depth N] [--verify-checksums]");
            Console.WriteLine("  Scans <folder> up to N levels (default 2), stopping at game folders, and");
            Console.WriteLine("  matches them to your library. Scan your library first so we know what to look for.");
            return Continue;
        }
        if (manifest.Current.Items.Count == 0)
        {
            Out.Dim("Your library is empty. Run 'sync' first so import knows which files to look for.");
            return Continue;
        }

        var modeArg = GetArgValue(args, "--mode")?.ToLowerInvariant() ?? "preview";
        var mode = modeArg switch
        {
            "adopt" => ImportMode.Adopt,
            "adopt-orphans" => ImportMode.AdoptAndReportOrphans,
            _ => ImportMode.Preview,
        };
        int depth = int.TryParse(GetArgValue(args, "--depth"), out var d) ? Math.Clamp(d, 1, 6) : 2;
        bool verifyChecksums = args.Contains("--verify-checksums", StringComparer.OrdinalIgnoreCase);

        // Core's ImportRun owns the sequence: register the root (preview registers nothing), plan, adopt the
        // confident matches, verify what was adopted. The CLI only draws the plan.
        var prog = new ConsoleProgress();
        var options = new Grog.Core.Runs.ImportRun.Options
        {
            Progress = (done, total) => prog.Report(done, total),
            MaxDepth = depth,
            FetchCurrentServerMd5 = ServerChecksumFetcher(ctx),
            FetchMissingChecksums = verifyChecksums,
        };
        var host = new ConsoleBackupHost(false, false, null);
        Grog.Core.Runs.ImportRunResult result;
        if (mode == ImportMode.Preview)
        {
            var previewRoot = new Grog.Core.Models.BackupRoot { Id = "", Label = Path.GetFileName(Path.GetFullPath(src)), PathHint = Path.GetFullPath(src) };
            result = await Grog.Core.Runs.ImportRun.ImportExistingRootAsync(manifest, previewRoot, src, mode, host, CancellationToken.None, options);
        }
        else
            result = await Grog.Core.Runs.ImportRun.AddAndImportAsync(manifest, src, mode, host, CancellationToken.None, options);
        prog.Finish();
        if (result.Status == Grog.Core.Runs.ImportRunStatus.Failed) throw result.Error!;
        var plan = result.Plan;
        if (plan is null)
        {
            Out.Dim("Import deferred until the first library scan.");
            return Continue;
        }

        Out.Success($"Scanned {plan.FoldersScanned} game folder(s): " +
                    $"{plan.Matched.Count} matched, {plan.Ambiguous.Count} ambiguous, " +
                    $"{plan.UnmatchedGameFolders.Count} unmatched.");
        foreach (var m in plan.Matched)
            Console.WriteLine($"  [match] {m.Title}  ({m.Files.Count} file(s), {m.UnmatchedFileCount} unmatched)  <- {Path.GetFileName(m.Path)} [{m.Reason}]");
        foreach (var a in plan.Ambiguous)
            Console.WriteLine($"  [ambig] {Path.GetFileName(a.Path)} -> {string.Join(", ", a.Candidates.Select(c => c.Title))}");
        foreach (var u in plan.UnmatchedGameFolders)
            Console.WriteLine($"  [none ] {Path.GetFileName(u)}");

        // Adopt mode: the host already printed Core's "Adopted N file(s)" and "Verified" lines; no second copy here.
        if (mode == ImportMode.Preview)
            Out.Dim("Preview only - nothing changed. Re-run with --mode adopt to register this folder as a backup folder and adopt matches.");

        if (mode == ImportMode.AdoptAndReportOrphans && plan.UnmatchedGameFolders.Count > 0)
        {
            var orphanPath = Path.Combine(paths.ConfigDir, "import_unmatched.txt");
            await File.WriteAllLinesAsync(orphanPath, plan.UnmatchedGameFolders);
            Out.Dim($"Unmatched folder list: {orphanPath}");
        }
        return Continue;
    }
    }

    internal static async Task<int> Root(CliContext ctx)
    {
        var args = ctx.Args;
        var jsonOut = ctx.JsonOut;
        var manifest = ctx.Manifest;
        Grog.Core.Volumes.BackupLayout Layout() => ctx.Layout();
    {
        await manifest.LoadAsync();
        var lay = Layout();   // ensure primary exists + scan
        var vol = new Grog.Core.Volumes.VolumeService(manifest);
        var sub = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))?.ToLowerInvariant();
        switch (sub)
        {
            case "list":
            case null:
                if (jsonOut)
                {
                    EmitJson(vol.Roots.Select(r => new
                    {
                        id = r.Id,
                        label = r.Label,
                        path = r.PathHint,
                        primary = r.Id == manifest.Current.PrimaryRootId,
                        online = lay.IsOnline(r.Id),
                        removable = r.Removable,
                        detached = r.State == Grog.Core.Models.RootState.Detached,
                    }));
                    break;
                }
                Out.Heading("Backup roots:");
                foreach (var r in vol.Roots)
                {
                    bool primary = r.Id == manifest.Current.PrimaryRootId;
                    bool online = lay.IsOnline(r.Id);
                    var tag = primary ? " (primary)" : "";
                    bool detached = r.State == Grog.Core.Models.RootState.Detached;
                    var state = detached ? "detached" : online ? "online" : "OFFLINE";
                    var line = $"  {r.Label,-16} {r.Id[..8]}  {state,-8} {r.PathHint}{tag}";
                    if (online || detached) Console.WriteLine(line); else Out.Warn(line);
                }
                break;
            case "add":
            {
                var path = PositionalArgs(args.Skip(2)).FirstOrDefault();
                if (path is null) { Out.Usage("Usage: root add <path> [--label <name>] [--removable]"); break; }
                var r = vol.AddRoot(path, GetArgValue(args, "--label"),
                                    args.Contains("--removable", StringComparer.OrdinalIgnoreCase));
                await manifest.SaveAsync();
                Out.Success($"Added root '{r.Label}' ({r.Id[..8]}) at {r.PathHint}" + (r.Removable ? " [removable]" : ""));
                break;
            }
            case "purpose":
            {
                var which = PositionalArgs(args.Skip(2)).FirstOrDefault();
                var pStr = PositionalArgs(args.Skip(2)).Skip(1).FirstOrDefault()?.ToLowerInvariant();
                // "overflow" = clear any pinned role; "extras"/"games" pin that content here.
                bool known = pStr is "overflow" or "extras" or "games";
                ContentRole? role = pStr switch
                {
                    "extras" => ContentRole.Extras,
                    "games" => ContentRole.Games,
                    _ => null,
                };
                if (which is null || !known) { Out.Usage("Usage: root purpose <label|id> <overflow|extras|games>"); break; }
                try
                {
                    vol.PinRole(which, role);
                    await manifest.SaveAsync();
                    Out.Success($"Set {which} purpose to {pStr}.");
                }
                catch (Exception ex) { Out.Error(ex.Message); return (int)Grog.Core.Cli.ExitCode.Error; }
                break;
            }
            case "remove":
            {
                var which = PositionalArgs(args.Skip(2)).FirstOrDefault();
                if (which is null) { Out.Usage("Usage: root remove <label|id> [--force]"); break; }
                try
                {
                    vol.RemoveRoot(which, args.Contains("--force", StringComparer.OrdinalIgnoreCase));
                    await manifest.SaveAsync();
                    Out.Success($"Removed root {which}.");
                }
                catch (Exception ex) { Out.Error(ex.Message); return (int)Grog.Core.Cli.ExitCode.Error; }
                break;
            }
            case "mode":
            {
                var which = PositionalArgs(args.Skip(2)).FirstOrDefault();
                if (which is null || !Enum.TryParse<Grog.Core.Models.RoutingMode>(which, ignoreCase: true, out var newMode))
                {
                    Out.Usage("Usage: root mode <overflow|bycontent|manual>");
                    Out.Dim($"Current: {manifest.Current.Routing.Mode}");
                    break;
                }
                manifest.Mutate(m =>   // (manifest gate 09-08)
                {
                    m.Routing.Mode = newMode;
                    if (newMode == Grog.Core.Models.RoutingMode.Overflow) m.Routing.RoleRoots.Clear();
                });
                await manifest.SaveAsync();
                Out.Success($"Backup mode set to {newMode}.");
                if (newMode == Grog.Core.Models.RoutingMode.ByContent)
                    Out.Dim("Assign roles with: root purpose <device> games|extras");
                break;
            }
            case "set-primary":
            {
                var which = PositionalArgs(args.Skip(2)).FirstOrDefault();
                if (which is null) { Out.Usage("Usage: root set-primary <label|id>"); break; }
                try
                {
                    vol.SetPrimary(which);
                    await manifest.SaveAsync();
                    Out.Success($"Primary (default) device set to {which}.");
                }
                catch (Exception ex) { Out.Error(ex.Message); return (int)Grog.Core.Cli.ExitCode.Error; }
                break;
            }
            case "reset":
            {
                // Destructive (manifest-wise): drops every non-primary root and marks their files missing.
                // Gate on --yes like `root lost`, so a mistyped invocation can't wipe the volume layout.
                if (!args.Contains("--yes"))
                {
                    Out.Usage("root reset removes all non-primary roots and marks their files missing. Re-run with --yes to confirm.");
                    return (int)Grog.Core.Cli.ExitCode.UsageError;
                }
                // Clear all non-primary roots and route everything back to Primary. Files that
                // lived on removed roots are re-pointed to Primary and marked missing so they
                // can be re-downloaded. Handy for re-running the multi-volume test sequence.
                var primary = manifest.Current.PrimaryRootId!;
                int removed = 0;
                using (manifest.Gate.Enter())   // (manifest gate 09-08)
                {
                    foreach (var f in manifest.Current.Items.SelectMany(i => i.Files).Where(f => f.RootId is not null && f.RootId != primary))
                    {
                        f.RootId = primary;
                        if (Grog.Core.Sync.BackupScope.IsPresent(f.State)) f.State = FileState.Missing;
                    }
                    removed = manifest.Current.Roots.RemoveAll(r => r.Id != primary);
                    manifest.Current.Routing.RoleRoots.Clear();
                    manifest.Current.Routing.SetRole(Grog.Core.Models.ContentRole.Games, primary);
                    manifest.Current.Routing.SetRole(Grog.Core.Models.ContentRole.Extras, primary);
                }
                await manifest.SaveAsync();

                Out.Success($"Reset to Primary-only ({removed} extra root(s) removed, routing cleared).");
                Out.Dim("Note: this doesn't delete Volume_* folders on disk -- remove those manually if you want a fully clean slate.");
                break;
            }
            case "replace":
            {
                var which = PositionalArgs(args.Skip(2)).FirstOrDefault();
                var withPath = GetArgValue(args, "--with");
                if (which is null || withPath is null) { Out.Usage("Usage: root replace <label|id> --with <newpath>"); break; }
                try
                {
                    vol.ReplaceRoot(which, withPath);
                    await manifest.SaveAsync();
                    Out.Success($"Root {which} replaced → {withPath}. Its files are marked missing; run 'download' to re-fetch (or 'import' first to adopt survivors).");
                }
                catch (Exception ex) { Out.Error(ex.Message); return (int)Grog.Core.Cli.ExitCode.Error; }
                break;
            }
            case "removable":
            {
                // Toggle the removable flag on an EXISTING device. Removable drives read
                // "Disconnected" (safe) when unplugged, not "Missing".
                var which = PositionalArgs(args.Skip(2)).FirstOrDefault();
                if (which is null) { Out.Usage("Usage: root removable <label|id> [on|off]"); return (int)Grog.Core.Cli.ExitCode.UsageError; }
                var target = vol.FindRoot(which);
                if (target is null) { Out.Error($"No device matched '{which}'."); return (int)Grog.Core.Cli.ExitCode.UsageError; }
                var stateArg = PositionalArgs(args.Skip(2)).Skip(1).FirstOrDefault()?.ToLowerInvariant();
                bool newVal = stateArg switch { "on" => true, "off" => false, _ => !target.Removable };
                target.Removable = newVal;
                await manifest.SaveAsync();
                Out.Success($"{target.Label} marked {(newVal ? "removable - files read Disconnected (not Missing) when unplugged" : "not removable")}.");
                break;
            }
            case "lost":
            {
                // The drive died / is gone for good: forget its files (they return to "not downloaded" and
                // re-enter the backup queue) and drop the device. Explicit + --yes gated -- never inferred.
                var which = PositionalArgs(args.Skip(2)).FirstOrDefault();
                if (which is null) { Out.Usage("Usage: root lost <label|id> --yes"); return (int)Grog.Core.Cli.ExitCode.UsageError; }
                var target = vol.FindRoot(which);
                if (target is null) { Out.Error($"No device matched '{which}'."); return (int)Grog.Core.Cli.ExitCode.UsageError; }
                if (!args.Contains("--yes", StringComparer.OrdinalIgnoreCase) && !args.Contains("--confirm", StringComparer.OrdinalIgnoreCase))
                {
                    Out.Warn($"This forgets every file Grog tracked on '{target.Label}' ({target.PathHint}) - they go back to \"not downloaded\". Re-run with --yes to confirm.");
                    return (int)Grog.Core.Cli.ExitCode.UsageError;
                }
                try
                {
                    vol.RemoveRoot(target.Id, force: true);   // drops the device + marks its files not-downloaded
                    await manifest.SaveAsync();
                    Out.Success($"Marked {target.Label} as lost - its files are back in the backup queue.");
                }
                catch (Exception ex) { Out.Error(ex.Message); return (int)Grog.Core.Cli.ExitCode.Error; }
                break;
            }
            case "detach":
            {
                // The shelf: files stay recorded and count as backed up; the drive frees its slot. Not the primary.
                var which = PositionalArgs(args.Skip(2)).FirstOrDefault();
                if (which is null) { Out.Usage("Usage: root detach <label|id>"); return (int)Grog.Core.Cli.ExitCode.UsageError; }
                var target = vol.FindRoot(which);
                if (target is null) return Fail("root", Grog.Core.Cli.ExitCode.UsageError, "usage", $"No device matched '{which}'.");
                if (target.State == Grog.Core.Models.RootState.Detached)
                    return Fail("root", Grog.Core.Cli.ExitCode.Error, "already-detached", $"{target.Label} is already detached.");
                try
                {
                    vol.DetachRoot(target.Id);
                    int changed = ReplanAfterChange(ctx);
                    await manifest.SaveAsync();
                    if (jsonOut) EmitJson(new { command = "root", action = "detach", label = target.Label, id = target.Id, replanned = changed, exitCode = 0 });
                    Out.Success($"Detached {target.Label}: its files still count as backed up; nothing downloads to it until it takes a slot again ('root reattach {target.Label}').");
                }
                catch (Exception ex) { return Fail("root", Grog.Core.Cli.ExitCode.Error, "error", ex.Message); }
                break;
            }
            case "reattach":
            {
                // A detached drive back into the free slot. Two slots exist (primary + secondary); with both taken
                // the App offers a swap, which the CLI does not have.
                var which = PositionalArgs(args.Skip(2)).FirstOrDefault();
                if (which is null) { Out.Usage("Usage: root reattach <label|id>"); return (int)Grog.Core.Cli.ExitCode.UsageError; }
                var target = vol.FindRoot(which);
                if (target is null) return Fail("root", Grog.Core.Cli.ExitCode.UsageError, "usage", $"No device matched '{which}'.");
                if (target.State != Grog.Core.Models.RootState.Detached)
                    return Fail("root", Grog.Core.Cli.ExitCode.Error, "not-detached", $"{target.Label} is not detached; it already holds a slot.");
                if (vol.ActiveRoots.Count() >= 2)
                {
                    var held = string.Join(", ", vol.ActiveRoots.Select(r => r.Label));
                    return Fail("root", Grog.Core.Cli.ExitCode.Error, "no-free-slot",
                        $"No free storage slot: both are held ({held}). Detach one first ('root detach <label>'); 'root swap' is not available in the CLI (use the app to swap).");
                }
                try
                {
                    vol.ReattachRoot(target.Id);
                    int changed = ReplanAfterChange(ctx);
                    await manifest.SaveAsync();
                    if (jsonOut) EmitJson(new { command = "root", action = "reattach", label = target.Label, id = target.Id, replanned = changed, exitCode = 0 });
                    Out.Success($"{target.Label} is now the Secondary storage. Its files were already counted; the queue can download to it now.");
                }
                catch (Exception ex) { return Fail("root", Grog.Core.Cli.ExitCode.Error, "error", ex.Message); }
                break;
            }
            case "forget":
            {
                // Stop tracking a detached drive: its files count as not backed up (they re-enter the queue). Files on
                // disk are untouched. --yes gated like `root lost`.
                var which = PositionalArgs(args.Skip(2)).FirstOrDefault();
                if (which is null) { Out.Usage("Usage: root forget <label|id> --yes"); return (int)Grog.Core.Cli.ExitCode.UsageError; }
                var target = vol.FindRoot(which);
                if (target is null) return Fail("root", Grog.Core.Cli.ExitCode.UsageError, "usage", $"No device matched '{which}'.");
                if (target.Id == manifest.Current.PrimaryRootId)
                    return Fail("root", Grog.Core.Cli.ExitCode.Error, "primary", "Can't forget the primary root. Set another primary first ('root set-primary <label>').");
                int fileCount = manifest.Current.Items.SelectMany(i => i.Files).Count(f => f.RootId == target.Id && Grog.Core.Sync.BackupScope.IsPresent(f.State));
                if (!args.Contains("--yes", StringComparer.OrdinalIgnoreCase) && !args.Contains("--confirm", StringComparer.OrdinalIgnoreCase))
                {
                    Out.Usage($"root forget drops '{target.Label}' ({target.PathHint}): its {fileCount} file{(fileCount == 1 ? "" : "s")} will count as not backed up. Nothing on disk is touched. Re-run with --yes to confirm.");
                    return (int)Grog.Core.Cli.ExitCode.UsageError;
                }
                try
                {
                    vol.RemoveRoot(target.Id, force: true);
                    int changed = ReplanAfterChange(ctx);
                    await manifest.SaveAsync();
                    if (jsonOut) EmitJson(new { command = "root", action = "forget", label = target.Label, id = target.Id, files = fileCount, replanned = changed, exitCode = 0 });
                    Out.Success($"Forgot {target.Label}: {fileCount} file{(fileCount == 1 ? "" : "s")} now count as not backed up. Back up to download them again.");
                }
                catch (Exception ex) { return Fail("root", Grog.Core.Cli.ExitCode.Error, "error", ex.Message); }
                break;
            }
            default:
                Out.Usage("Usage: root [list|add <path>|remove <label>|removable <label> [on|off]|lost <label> --yes|replace <label> --with <path>|set-primary <label>|detach <label>|reattach <label>|forget <label> --yes|reset]");
                break;
        }
        return Continue;
    }
    }

    internal static async Task<int> Policy(CliContext ctx)
    {
        var args = ctx.Args;
        var manifest = ctx.Manifest;
        Grog.Core.Volumes.BackupLayout Layout() => ctx.Layout();
    {
        await manifest.LoadAsync();
        _ = Layout();
        var vol = new Grog.Core.Volumes.VolumeService(manifest);
        var sub = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))?.ToLowerInvariant();
        try
        {
            switch (sub)
            {
                case "games":
                case "extras":
                {
                    var target = PositionalArgs(args.Skip(2)).FirstOrDefault();
                    if (target is null) { Out.Usage($"Usage: policy {sub} <root label|id>"); break; }
                    var role = sub == "games" ? Grog.Core.Models.ContentRole.Games : Grog.Core.Models.ContentRole.Extras;
                    vol.SetRolePolicy(role, target);
                    await manifest.SaveAsync();
                    Out.Success($"{sub} → {target}");
                    break;
                }
                case "show":
                case null:
                {
                    Out.Heading("Routing policy:");
                    foreach (var kv in manifest.Current.Routing.RoleRoots)
                    {
                        var r = vol.FindRoot(kv.Value);
                        Console.WriteLine($"  {kv.Key,-8} → {r?.Label ?? kv.Value}");
                    }
                    break;
                }
                default:
                    Out.Usage("Usage: policy [show|games <root>|extras <root>]");
                    break;
            }
        }
        catch (Exception ex) { Out.Error(ex.Message); }
        return Continue;
    }
    }

    internal static async Task<int> Relocate(CliContext ctx)
    {
        var args = ctx.Args;
        var manifest = ctx.Manifest;
        Grog.Core.Volumes.BackupLayout Layout() => ctx.Layout();
    {
        await manifest.LoadAsync();
        var lay = Layout();
        var vol = new Grog.Core.Volumes.VolumeService(manifest);
        var rest = PositionalArgs(args.Skip(1)).ToList();
        var toRoot = GetArgValue(args, "--to");
        if (rest.Count == 0 || toRoot is null) { Out.Usage("Usage: relocate <game title|id> --to <root>"); return (int)Grog.Core.Cli.ExitCode.UsageError; }
        var gameQuery = string.Join(' ', rest);
        var g = manifest.Current.FindItem(gameQuery);
        if (g is null) { Out.Warn($"No game matched '{gameQuery}'."); return (int)Grog.Core.Cli.ExitCode.UsageError; }
        try
        {
            Out.Dim($"Relocating '{g.Title}' → {toRoot}…");
            var r = await vol.RelocateGameAsync(g, toRoot, lay);
            Out.Success($"Relocated: {r.Moved} file(s) moved, {r.Repointed} re-pointed (re-download to place).");
        }
        catch (Exception ex) { Out.Error(ex.Message); return (int)Grog.Core.Cli.ExitCode.Error; }
        return Continue;
    }
    }

    internal static async Task<int> Blacklist(CliContext ctx)
    {
        var args = ctx.Args;
        var manifest = ctx.Manifest;
    {
        await manifest.LoadAsync();
        var sub = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))?.ToLowerInvariant();
        var current = manifest.Current.Settings.GetValueOrDefault("scope.blacklist", "");
        switch (sub)
        {
            case "add":
            {
                var pat = string.Join(' ', PositionalArgs(args.Skip(2)));
                if (string.IsNullOrWhiteSpace(pat)) { Out.Usage("Usage: blacklist add <pattern>"); break; }
                var set = current.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (!set.Contains(pat, StringComparer.OrdinalIgnoreCase)) set.Add(pat);
                manifest.Mutate(m => m.Settings["scope.blacklist"] = string.Join(", ", set));   // (manifest gate 09-08)
                await manifest.SaveAsync();
                Out.Success($"Blacklisted: {pat}");
                break;
            }
            case "remove":
            {
                var pat = string.Join(' ', PositionalArgs(args.Skip(2)));
                var set = current.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(x => !string.Equals(x, pat, StringComparison.OrdinalIgnoreCase)).ToList();
                manifest.Mutate(m => m.Settings["scope.blacklist"] = string.Join(", ", set));   // (manifest gate 09-08)
                await manifest.SaveAsync();
                Out.Success($"Removed from blacklist: {pat}");
                break;
            }
            case "clear":
                manifest.Mutate(m => m.Settings.Remove("scope.blacklist"));   // (manifest gate 09-08)
                await manifest.SaveAsync();
                Out.Success("Blacklist cleared.");
                break;
            default:
                Out.Heading("Blacklist (patterns skipped on download):");
                if (string.IsNullOrWhiteSpace(current)) Out.Dim("  (empty)");
                else foreach (var b in current.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    Console.WriteLine($"  {b}");
                break;
        }
        return Continue;
    }
    }

    internal static async Task<int> Health(CliContext ctx)
    {
        var jsonOut = ctx.JsonOut;
        var manifest = ctx.Manifest;
        Grog.Core.Volumes.BackupLayout Layout() => ctx.Layout();
    {
        await manifest.LoadAsync();
        var pres = new Grog.Core.Preservation.PreservationService(manifest, Layout());
        var h = pres.Health();
        if (jsonOut)
        {
            EmitJson(new
            {
                totalItems = h.TotalItems,
                complete = h.Complete,
                partial = h.Partial,
                updatesAvailable = h.Outdated,
                notDownloaded = h.NotBackedUp,
                errors = h.CorruptItems,
                corruptFiles = h.CorruptFiles,
                missingFiles = h.MissingFiles,
                delisted = h.Delisted,
                bytesOnDisk = h.BytesOnDisk,
                roots = h.RootCoverage.Select(rc => new { label = rc.Label, online = rc.Online, files = rc.Files }),
            });
            return Continue;
        }
        Out.Heading($"Backup health -- {h.Complete}/{h.TotalItems} fully backed up");
        Console.WriteLine($"  complete .......... {h.Complete}");
        Console.WriteLine($"  partial ........... {h.Partial}");
        Console.WriteLine($"  updates available . {h.Outdated}");
        Console.WriteLine($"  not downloaded .... {h.NotBackedUp}");
        if (h.CorruptItems > 0) Out.Error($"  errors ............ {h.CorruptItems}");
        else Console.WriteLine($"  errors ............ {h.CorruptItems}");
        if (h.CorruptFiles > 0) Out.Warn($"  corrupt files ..... {h.CorruptFiles}");
        if (h.MissingFiles > 0) Out.Warn($"  missing files ..... {h.MissingFiles}");
        if (h.Delisted > 0) Out.Warn($"  delisted/at-risk .. {h.Delisted}  (run 'delisted')");
        Out.Dim("  --");
        Console.WriteLine($"  on disk ........... {ByteFormat.Size(h.BytesOnDisk)}");
        Out.Heading("  roots:");
        foreach (var rc in h.RootCoverage)
        {
            var line = $"    {rc.Label,-16} {(rc.Online ? "online" : "OFFLINE"),-8} {rc.Files} files";
            if (rc.Online) Console.WriteLine(line); else Out.Warn(line);
        }
        return Continue;
    }
    }

    internal static async Task<int> Delisted(CliContext ctx)
    {
        var jsonOut = ctx.JsonOut;
        var manifest = ctx.Manifest;
        Grog.Core.Volumes.BackupLayout Layout() => ctx.Layout();
    {
        await manifest.LoadAsync();
        var pres = new Grog.Core.Preservation.PreservationService(manifest, Layout());
        var items = pres.DelistedItems();
        if (jsonOut)
        {
            EmitJson(items.Select(i => new
            {
                id = i.GogId,
                title = i.Title,
                backedUp = i.Status == BackupStatus.Complete,
                status = i.Status.ToString(),
            }));
            return Continue;
        }
        if (items.Count == 0) { Out.Success("No delisted or at-risk products -- everything is still listed on GOG."); return Continue; }
        Out.Warn($"{items.Count} product(s) no longer listed on GOG (still owned -- make sure they're backed up):");
        foreach (var i in items)
        {
            bool done = i.Status == BackupStatus.Complete;
            var line = $"  {(done ? "[backed up]" : "[AT RISK] "),-11} {i.Title}";
            if (done) Console.WriteLine(line); else Out.Warn(line);
        }
        return Continue;
    }
    }

    internal static async Task<int> Artprobe(CliContext ctx)
    {
        var args = ctx.Args;
        var paths = ctx.Paths;
        var manifest = ctx.Manifest;
        var api = ctx.Api;
    {
        // What art GOG actually offers per product. The grid wants something square-ish; the logo is
        // wide and boxArtImage is 3:4, so dump every _links entry rather than guess at the API.
        await manifest.LoadAsync();
        var title = PositionalArgs(args.Skip(1)).FirstOrDefault();
        var picks = manifest.Current.Items
            .Where(i => title is null || i.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
            .Take(title is null ? 3 : 1)
            .ToList();
        if (picks.Count == 0) { Out.Warn("No matching product. Try 'artprobe \"Witcher\"'."); return Continue; }

        var sb = new System.Text.StringBuilder();
        foreach (var p in picks)
        {
            Out.Heading($"{p.Title} ({p.GogId})");
            sb.AppendLine($"=== {p.Title} ({p.GogId}) ===");
            var (json, err) = await api.FetchGameLinksAsync(p.GogId);
            if (json is null) { Out.Warn(err); sb.AppendLine(err); continue; }
            foreach (var line in json)
            {
                Console.WriteLine($"  {line}");
                sb.AppendLine(line);
            }
            sb.AppendLine();
        }
        var outPath = Path.Combine(paths.ConfigDir, "art-links.txt");
        await File.WriteAllTextAsync(outPath, sb.ToString());
        Out.Success($"Written to {outPath}");
        return Continue;
    }
    }

    internal static async Task<int> Extratypes(CliContext ctx)
    {
        var paths = ctx.Paths;
        var manifest = ctx.Manifest;
    {
        // What GOG actually calls each extra, so we can judge how much is landing in "Other".
        await manifest.LoadAsync();
        var extras = manifest.Current.Items
            .SelectMany(i => i.Files.Select(f => (Item: i, File: f)))
            .Where(x => x.File.Kind == FileKind.Extra)
            .ToList();
        if (extras.Count == 0) { Out.Dim("No extras in the manifest -- run 'sync --details' first."); return Continue; }

        var groups = extras
            .GroupBy(x => string.IsNullOrWhiteSpace(x.File.ExtraType) ? "(blank)" : x.File.ExtraType!)
            .OrderByDescending(g => g.Sum(x => x.File.ExpectedSizeBytes ?? 0))
            .ToList();

        Out.Heading($"Extra types ({groups.Count} distinct, {extras.Count} files):");
        foreach (var g in groups)
        {
            long sz = g.Sum(x => x.File.ExpectedSizeBytes ?? 0);
            Console.WriteLine($"  {g.Key,-24} {g.Count(),5} files  {ByteFormat.Size(sz),12}");
            foreach (var sample in g.Take(3))
                Console.WriteLine($"        e.g. {Trunc(sample.File.Name, 60)}   [{Trunc(sample.Item.Title, 30)}]");
        }

        var csv = Path.Combine(paths.ConfigDir, "extra-types.csv");
        var sb = new System.Text.StringBuilder("Type,Game,FileName,SizeBytes\n");
        foreach (var x in extras.OrderBy(x => x.File.ExtraType).ThenBy(x => x.Item.Title))
            sb.AppendLine($"\"{x.File.ExtraType}\",\"{x.Item.Title.Replace("\"", "'")}\",\"{x.File.Name.Replace("\"", "'")}\",{x.File.ExpectedSizeBytes ?? 0}");
        await File.WriteAllTextAsync(csv, sb.ToString());
        Out.Success($"Full list exported to {csv}");
        return Continue;
    }
    }

    internal static async Task<int> Serials(CliContext ctx)
    {
        var args = ctx.Args;
        var paths = ctx.Paths;
        var manifest = ctx.Manifest;
        Grog.Core.Volumes.BackupLayout Layout() => ctx.Layout();
    {
        await manifest.LoadAsync();
        var pres = new Grog.Core.Preservation.PreservationService(manifest, Layout());
        var serials = pres.SerialKeys();
        if (serials.Count == 0) { Out.Dim("No serial keys recorded for your library."); return Continue; }
        Out.Heading($"Serial keys ({serials.Count}):");
        foreach (var (title, serial) in serials)
        {
            var parts = serial.Split('\n');
            Console.WriteLine($"  {title}");
            foreach (var p in parts) Console.WriteLine($"      {p}");
        }
        if (args.Contains("--export", StringComparer.OrdinalIgnoreCase))
        {
            var p = await pres.ExportSerialsAsync(paths.ConfigDir);
            if (p is not null) Out.Success($"Exported to {p}");
        }
        return Continue;
    }
    }

    internal static async Task<int> Zipcheck(CliContext ctx)
    {
        var manifest = ctx.Manifest;
        Grog.Core.Volumes.BackupLayout Layout() => ctx.Layout();
    {
        await manifest.LoadAsync();
        var pres = new Grog.Core.Preservation.PreservationService(manifest, Layout());
        Out.Dim("Checking zip-extra integrity…");
        var r = await pres.CheckZipsAsync();
        if (r.Bad > 0)
        {
            Out.Warn($"Checked {r.Checked} zip(s): {r.Ok} ok, {r.Bad} corrupt, {r.Skipped} skipped.");
            foreach (var b in r.BadFiles) Out.Warn($"  corrupt: {b}");
            return Fail("zipcheck", Grog.Core.Cli.ExitCode.IntegrityFailed, "integrity",
                "Bad archives found. Run 'fix --corrupt' to re-download them.");   // zipcheck marks them Corrupt; 'download' skips Corrupt
        }
        else Out.Success($"Checked {r.Checked} zip(s): all ok ({r.Skipped} skipped).");
        return Continue;
    }
    }

    internal static async Task<int> Delete(CliContext ctx)
    {
        var args = ctx.Args;
        var manifest = ctx.Manifest;
        Grog.Core.Volumes.BackupLayout Layout() => ctx.Layout();
    {
        // F9: reclaim space by deleting downloaded files. Dry-run first (default); --confirm to
        // actually delete. Never touches GOG; items stay owned + re-downloadable.
        // Usage: delete <title|id|--all> [--extras-only] [--confirm]
        await manifest.LoadAsync();
        var q = string.Join(' ', PositionalArgs(args.Skip(1)));
        bool all = args.Contains("--all", StringComparer.OrdinalIgnoreCase);
        bool extrasOnly = args.Contains("--extras-only", StringComparer.OrdinalIgnoreCase);
        bool confirm = args.Contains("--confirm", StringComparer.OrdinalIgnoreCase);

        Func<Grog.Core.Models.LibraryItem, bool> filter;
        if (all) filter = _ => true;
        else if (!string.IsNullOrWhiteSpace(q))
            filter = i => i.GogId.ToString() == q || i.Title.Contains(q, StringComparison.OrdinalIgnoreCase);
        else { Out.Usage("Usage: delete <title|id|--all> [--extras-only] [--confirm]"); return Continue; }

        var svc = new Grog.Core.Storage.DeleteBackupService(manifest, Layout());
        var plan = svc.PlanFor(filter, includeInstallers: !extrasOnly, includeExtras: true);
        if (plan.Count == 0) { Out.Dim("No downloaded files match -- nothing to delete."); return Continue; }

        Out.Heading($"Would delete {plan.Count} file(s), freeing {Grog.Core.Format.ByteFormat.Size(plan.TotalBytes)}");
        foreach (var (f, path, bytes) in plan.Files.Take(20))
            Console.WriteLine($"  {Trunc(f.Name, 50),-50}  {ByteFormat.Size(bytes),9}");
        if (plan.Count > 20) Out.Dim($"  … and {plan.Count - 20} more");

        if (!confirm)
        {
            Out.Warn("\nDry run. These files would be deleted (games stay owned & re-downloadable).");
            Out.Dim("Re-run with --confirm to actually delete.");
            return Continue;
        }
        var res = await svc.ExecuteDetailedAsync(plan);
        Out.Success($"Deleted {res.Deleted} file(s), freed {Grog.Core.Format.ByteFormat.Size(res.Bytes)}. Re-download anytime.");
        if (res.Skipped > 0) Out.Dim($"  {res.Skipped} file(s) skipped: queued for download.");
        foreach (var fail in res.Failures) Out.Warn($"  {fail}");
        return Continue;
    }
    }

    internal static async Task<int> Orphans(CliContext ctx)
    {
        var args = ctx.Args;
        var jsonOut = ctx.JsonOut;
        var manifest = ctx.Manifest;
        Grog.Core.Volumes.BackupLayout Layout() => ctx.Layout();
    {
        await manifest.LoadAsync();
        var pres = new Grog.Core.Preservation.PreservationService(manifest, Layout());
        bool move = args.Contains("--move", StringComparer.OrdinalIgnoreCase);
        var r = pres.HandleOrphans(move);
        if (jsonOut)
        {
            // Full list, not the 50 the human view truncates to -- a script wants all of them.
            EmitJson(new { count = r.Orphans.Count, moved = move ? r.Moved : 0, files = r.Orphans });
            return Continue;
        }
        if (r.Orphans.Count == 0) { Out.Success("No orphan files -- every local file is accounted for."); return Continue; }
        Out.Warn($"{r.Orphans.Count} orphan file(s) (on disk, not in manifest):");
        foreach (var o in r.Orphans.Take(50)) Console.WriteLine($"  {o}");
        if (r.Orphans.Count > 50) Out.Dim($"  … and {r.Orphans.Count - 50} more");
        if (move) Out.Success($"Moved {r.Moved} to each root's .grog/orphaned/ (not deleted).");
        else Out.Dim("Dry run. Re-run with --move to relocate them to .grog/orphaned/ (never deleted).");
        return Continue;
    }
    }

    internal static async Task<int> Changes(CliContext ctx)
    {
        var manifest = ctx.Manifest;
    {
        // surface what changed / what's incomplete, using existing diff state (no API).
        await manifest.LoadAsync();
        var changes = new Grog.Core.Sync.ChangesService(manifest);
        var updates = changes.UpdatesAvailable();
        var incomplete = changes.MissingOrIncomplete();

        Out.Heading("Updates available");
        if (updates.Count == 0) Out.Dim("  none");
        else foreach (var c in updates)
            Console.WriteLine($"  {Trunc(c.Title, 50),-50}  {c.Reason}");

        Out.Heading("Missing / incomplete");
        if (incomplete.Count == 0) Out.Dim("  none -- everything is backed up");
        else foreach (var c in incomplete)
            Console.WriteLine($"  {Trunc(c.Title, 50),-50}  {c.Reason}");

        Console.WriteLine();
        Out.Dim(changes.OneLineSummary());
        Out.Dim("Run 'download --updates' to fetch updates, 'download' for everything missing.");
        return Continue;
    }
    }

    internal static async Task<int> Changelog(CliContext ctx)
    {
        var args = ctx.Args;
        var http = ctx.Http;
        var paths = ctx.Paths;
        var manifest = ctx.Manifest;
        var auth = ctx.Auth;
    {
        // fetch/show a game's GOG changelog (patch-note text). Usage: changelog <title|id>
        await manifest.LoadAsync();
        var q = string.Join(' ', PositionalArgs(args.Skip(1)));
        if (string.IsNullOrWhiteSpace(q)) { Out.Usage("Usage: changelog <game title|id>"); return Continue; }
        var g = manifest.Current.FindItem(q);
        if (g is null) { Out.Usage($"No game matched '{q}'."); return Continue; }

        var svc = new Grog.Core.Sync.ChangelogService(http, auth, paths);
        Out.Heading($"Changelog: {g.Title}");
        Out.Dim("Fetching from GOG…");
        var text = await svc.FetchAndStoreAsync(g.GogId, g.Slug, g.Title);
        if (text is null) { Out.Warn("Could not fetch changelog (network or endpoint issue)."); return Continue; }
        if (text.Length == 0) { Out.Dim("(No changelog published for this game.)"); return Continue; }
        Console.WriteLine();
        Console.WriteLine(text);
        return Continue;
    }
    }

    internal static async Task<int> Account(CliContext ctx)
    {
        var args = ctx.Args;
        var http = ctx.Http;
        var paths = ctx.Paths;
        var loginProvider = ctx.LoginProvider;
        var manifest = ctx.Manifest;
        var sessions = ctx.Sessions;
    {
        // manage multi-account list. Usage: account [list|add <id>|remove <id>]
        await manifest.LoadAsync();
        var accounts = new Grog.Core.Auth.AccountService(manifest, paths);
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "list";

        if (sub == "add")
        {
            var id = args.Length > 2 ? args[2] : "";
            if (string.IsNullOrWhiteSpace(id)) { Out.Usage("Usage: account add <id>  (a short name like 'main' or 'kids')"); return Continue; }
            // Log in for this account into its own token file, then record it.
            var acctTokenStore = new FileTokenStore(accounts.TokenPathFor(id));
            var acctAuth = new GogAuthService(http, acctTokenStore, loginProvider);
            await acctAuth.EnsureAuthenticatedAsync();
            var acctApi = new GogApiClient(http, acctAuth);
            var user = await acctApi.GetUserInfoAsync();
            var acct = await accounts.AddOrUpdateAsync(id, user.Username, user.Email);
            Out.Success($"Added account '{acct.Id}' ({user.Username}). Tokens: {accounts.TokenPathFor(id)}");
            // Merge this account's library NOW, parity with the GUI add flow.
            Out.Dim($"Merging {user.Username}'s library…");
            sessions.Invalidate(acct.Id);   // fresh token file just landed; drop any stale cached chain
            // HostOwnsReconcile: a one-account merge must not flag the rest of the shared catalog
            // as delisted or clobber the Excluded list with just this account's few.
            var mergeSync = new LibrarySyncService(acctApi, manifest) { AccountId = acct.Id, HostOwnsReconcile = true };
            var mergeProg = new ConsoleProgress();
            mergeSync.Progress += p => mergeProg.Report(p.Completed, p.Total);
            var mr = await mergeSync.RunAsync();
            mergeProg.Finish();
            Out.Success($"Merged: seen {mr.GamesSeen}, new games {mr.NewGames}, new files {mr.NewFiles}, updated {mr.UpdatedFiles}.");
        }
        else if (sub == "remove")
        {
            var id = args.Length > 2 ? args[2] : "";
            if (string.IsNullOrWhiteSpace(id)) { Out.Usage("Usage: account remove <id>"); return Continue; }
            var ok = await accounts.RemoveAsync(id);
            if (ok) Out.Success($"Removed account '{id}'. Its already-backed-up items are preserved (kept, just untagged for future syncs).");
            else Out.Warn($"No account with id '{id}'.");
        }
        else // list
        {
            var counts = accounts.ItemCountsByAccount();
            if (accounts.Accounts.Count == 0)
            {
                Out.Dim("No named accounts -- using the single default account.");
                if (counts.TryGetValue("", out var n)) Out.Dim($"  default: {n} item(s)");
                return Continue;
            }
            Out.Heading("Accounts");
            foreach (var a in accounts.Accounts)
            {
                var n = counts.TryGetValue(a.Id, out var c) ? c : 0;
                var last = a.LastSync?.ToString("yyyy-MM-dd") ?? "never";
                Console.WriteLine($"  {a.Id,-12} {Trunc(a.Username, 24),-24}  {n,4} items  last sync {last}");
            }
        }
        return Continue;
    }
    }

    internal static async Task<int> Cloudsaves(CliContext ctx)
    {
        var args = ctx.Args;
        var http = ctx.Http;
        var manifest = ctx.Manifest;
        var auth = ctx.Auth;
        var api = ctx.Api;
        var firstAccount = ctx.FirstAccount;
        string ContentRoot() => ctx.ContentRoot();
        Grog.Core.Volumes.BackupLayout Layout() => ctx.Layout();
    {
        // list/download GOG cloud saves; the master-list endpoint is unwired, so this accepts an
        // explicit --client <clientId> to exercise the confirmed list/download path end-to-end.
        // Usage: cloudsaves <title|id> --client <clientId> [--download]
        await manifest.LoadAsync();
        _ = Layout();

        // `cloudsaves list` -- account-wide discovery via the v2 containers endpoint (same Core
        // service the GUI drives). Flags which owned games have cloud saves; no --client needed.
        if (string.Equals(args.ElementAtOrDefault(1), "list", StringComparison.OrdinalIgnoreCase))
        {
            var disco = new Grog.Core.CloudSaves.CloudSaveDiscoveryService(
                new Grog.Core.CloudSaves.CloudSaveService(http, auth));
            var rows = await disco.DiscoverAsync(manifest.Current,
                async (pid, ct) => (await api.GetProductInfoAsync(pid, ct))?.Title, gate: manifest.Gate);   // (manifest gate 09-08)
            await manifest.SaveAsync();
            if (rows.Count == 0) { Out.Dim("No cloud saves found on your account."); return Continue; }
            Out.Heading($"Cloud saves: {rows.Count} game(s)");
            long grand = 0;
            foreach (var r in rows)
            {
                grand += r.SizeBytes;
                Console.WriteLine($"  {Trunc(r.Title, 46),-46}  {ByteFormat.Size(r.SizeBytes),9}  {r.GogId}");
            }
            Console.WriteLine($"\n  {rows.Count} game(s), {ByteFormat.Size(grand)} total");
            return Continue;
        }

        var q = string.Join(' ', PositionalArgs(args.Skip(1)));
        if (string.IsNullOrWhiteSpace(q)) { Out.Usage("Usage: cloudsaves list  |  cloudsaves <game title|id> --client <clientId> [--download]"); return Continue; }
        var g = manifest.Current.FindItem(q);
        if (g is null) { Out.Usage($"No game matched '{q}'."); return Continue; }

        var cloud = new Grog.Core.CloudSaves.CloudSaveService(http, auth);

        // --diag: why does list-saves come back empty for a game discovery clearly sees? Probe the
        // v1 endpoint with BOTH keys (build-meta clientId + discovery space_id) and show raw status.
        if (args.Contains("--diag", StringComparer.OrdinalIgnoreCase))
        {
            var resolved = g.CloudClientId;
            if (string.IsNullOrEmpty(resolved)) { Out.Dim("Resolving clientId from build metadata…"); resolved = await cloud.ResolveClientIdAsync(g.GogId) ?? ""; }
            Out.Heading($"Cloud-save diagnostic: {g.Title} (id {g.GogId})");
            Out.Dim($"resolved clientId : {(string.IsNullOrEmpty(resolved) ? "(none)" : resolved)}");
            Out.Dim($"cached space_id   : {(string.IsNullOrEmpty(g.CloudSpaceId) ? "(none -- run 'cloudsaves list' first)" : g.CloudSpaceId)}");
            Console.WriteLine();
            foreach (var p in await cloud.DiagnoseSavesAsync(resolved, g.CloudSpaceId))
            {
                var st = p.Status < 0 ? "ERR" : p.Status.ToString();
                Console.WriteLine($"  [{st,3}] {p.Key,-24} files={p.Count}   {p.Url}");
            }
            Console.WriteLine();
            // Mint a game-scoped token (its own clientId/clientSecret) and try with THAT.
            var diagAuth = await cloud.AuthorizeGameAsync(g.GogId);
            if (diagAuth is null) Out.Warn("  scoped-token exchange: couldn't resolve build creds or mint a token");
            else
            {
                var sv = await cloud.ListSavesAsync(diagAuth.ClientId, diagAuth.AccessToken);
                Out.Success($"  scoped token minted -> list-saves returned {sv.Count} file(s)");
            }
            return Continue;
        }

        // Mint a token scoped to the game's own clientId/clientSecret -- the per-game cloud endpoint
        // 403s the plain session token. Creds come from build metadata (no installed game needed).
        Out.Dim("Authorizing game cloud access…");
        var gauth = await cloud.AuthorizeGameAsync(g.GogId);
        if (gauth is null)
        {
            Out.Warn("Couldn't authorize cloud access for this game (no build creds, or token minting failed).");
            return Continue;
        }
        if (!string.IsNullOrEmpty(gauth.ClientId) && gauth.ClientId != g.CloudClientId) { g.CloudClientId = gauth.ClientId; await manifest.SaveAsync(); }
        var clientId = gauth.ClientId;

        Out.Heading($"Cloud saves: {g.Title}");
        var saves = await cloud.ListSavesAsync(clientId, gauth.AccessToken);
        if (saves.Count == 0) { Out.Dim("(No cloud saves found -- or this game isn't cloud-enabled.)"); return Continue; }

        long total = 0;
        foreach (var s in saves)
        {
            total += s.SizeBytes;
            var when = s.Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";
            Console.WriteLine($"  {Trunc(s.Name, 46),-46}  {ByteFormat.Size(s.SizeBytes),9}  {when}");
        }
        Console.WriteLine($"\n  {saves.Count} file(s), {ByteFormat.Size(total)} total");

        if (args.Contains("--download", StringComparer.OrdinalIgnoreCase))
        {
            // Use the dedicated cloud resolver so CLI, GUI, and the honest-status reconcile all agree
            // on <root>/Cloud Saves/<slug>/ (ResolveTargetDir routes CloudSave under Games -- wrong).
            var gameDir = Layout().ResolveCloudDir(g.Slug, out _)
                          ?? Path.Combine(ContentRoot(), "Cloud Saves", g.Slug);
            Out.Dim("Downloading save…");
            var (dir, n, expected) = await cloud.DownloadLocalSaveAsync(
                new Grog.Core.CloudSaves.CloudGame(g.GogId, g.Title, g.Slug, clientId), gameDir);
            // Record clientId + cloud-save presence for next time. The listing belongs to the bound
            // account, so it lands in that account's entry and the mirror follows (single write path).
            using (manifest.Gate.Enter())   // (manifest gate 09-08)
            {
                g.CloudClientId = clientId;
                if (firstAccount is not null)
                {
                    var entry = Grog.Core.CloudSaves.CloudSaveReconciler.Entry(g, firstAccount.Id);
                    entry.SizeBytes = total;
                    entry.Files = saves.Count;
                    Grog.Core.CloudSaves.CloudSaveReconciler.MirrorLegacy(g);
                }
            }
            await manifest.SaveAsync();

            if (n < expected)
                Out.Warn($"Saved {n} of {expected} file(s) to {dir} - local save INCOMPLETE.");
            else
                Out.Success($"Saved {n} file(s) to {dir}");
        }
        else
        {
            Out.Dim("Add --download to keep a dated local save.");
        }
        return Continue;
    }
    }

    internal static async Task<int> Cloudprobe(CliContext ctx)
    {
        var args = ctx.Args;
        var http = ctx.Http;
        var manifest = ctx.Manifest;
        var auth = ctx.Auth;
    {
        // Hidden diagnostic (research): probe GOG cloud-save endpoints to learn what works
        // against the real account. With no game, uses the first owned game for context and
        // focuses on the ACCOUNT-WIDE listing endpoints ("what's in my cloud").
        // Usage: cloudprobe [<game title|id>] [--client <clientId>]
        await manifest.LoadAsync();
        var q = string.Join(' ', PositionalArgs(args.Skip(1)));
        LibraryItem? g = string.IsNullOrWhiteSpace(q)
            ? manifest.Current.Items.FirstOrDefault()
            : manifest.Current.FindItem(q);
        if (g is null) { Out.Warn("No games in library -- run 'sync' first."); return Continue; }

        var clientId = GetArgValue(args, "--client");
        Out.Heading($"Cloud-save probe (context game: {g.Title}, id {g.GogId})");
        Out.Dim($"clientId: {clientId ?? "(unknown -- per-product deep endpoints use a placeholder)"}");
        Out.Dim("Leading with account-wide 'list all cloud saves' endpoints, using your existing token…\n");

        var probe = new Grog.Core.Api.CloudSaveProbe(http, auth);
        var steps = await probe.ProbeAsync(g.GogId, clientId);
        foreach (var s in steps)
        {
            var statusStr = s.Status < 0 ? "ERR" : s.Status.ToString();
            if (s.Status is >= 200 and < 300) { Out.Success($"  [{statusStr}] {s.Label}"); Console.WriteLine($"    {s.Url}\n    {s.ContentType}\n    {s.BodySnippet}\n"); }
            else if (s.Status is 401 or 403) { Out.Warn($"  [{statusStr}] {s.Label} (auth rejected -- may need Galaxy-client token)"); Console.WriteLine($"    {s.Url}\n"); }
            else { Out.Dim($"  [{statusStr}] {s.Label}"); Console.WriteLine($"    {s.Url}\n    {s.BodySnippet}\n"); }
        }
        Out.Dim("Report which endpoints returned 2xx (and their body shape) so the cloud-saves feature can be designed to match.");
        return Continue;
    }
    }

    internal static async Task<int> Show(CliContext ctx)
    {
        var args = ctx.Args;
        var manifest = ctx.Manifest;
    {
        await manifest.LoadAsync();
        var query = string.Join(' ', PositionalArgs(args.Skip(1)));
        if (string.IsNullOrWhiteSpace(query)) { Out.Usage("Usage: show <id or title fragment>"); return Continue; }

        var matches = manifest.Current.Items.Where(i =>
            i.GogId.ToString() == query ||
            i.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0) { Out.Usage($"No item matches \"{query}\"."); return Continue; }
        if (matches.Count > 1)
        {
            Console.WriteLine($"{matches.Count} matches -- be more specific:");
            foreach (var m in matches) Console.WriteLine($"  {m.GogId}  {m.Title}");
            return Continue;
        }

        var g = matches[0];
        Console.WriteLine($"{g.Title}  [{g.Type}]  (id {g.GogId})");
        if (!string.IsNullOrEmpty(g.SerialKey)) Console.WriteLine($"  serial: {g.SerialKey}");
        Console.WriteLine();

        void Dump(string label, IEnumerable<GameFile> files)
        {
            var arr = files.ToList();
            if (arr.Count == 0) return;
            Console.WriteLine($"  {label}:");
            foreach (var f in arr)
            {
                var size = f.ExpectedSizeBytes is { } b ? ByteFormat.Size(b) : "?";
                var meta = f.Kind == FileKind.Extra ? f.ExtraType
                    : $"{f.Os} {f.Language}".Trim();
                var ver = string.IsNullOrEmpty(f.Version) ? "" : $" v{f.Version}";
                Console.WriteLine($"    {f.Name,-42} {meta,-18} {size,8}{ver}");
            }
        }
        Dump("Installers", g.Files.Where(f => f.Kind is FileKind.Installer or FileKind.DlcInstaller));
        Dump("Extras", g.Files.Where(f => f.Kind == FileKind.Extra));
        Console.WriteLine($"\n  {g.Files.Count} file(s) total.");
        return Continue;
    }
    }

    internal static async Task<int> Renderdemo(CliContext ctx)
    {
        var args = ctx.Args;
    {
        // Hidden diagnostic (absent from help): drives DownloadRenderer with synthetic tasks so the
        // live block can be verified without a real multi-GB download.
        int lanes = int.TryParse(GetArgValue(args, "--parallel"), out var lz) ? Math.Clamp(lz, 1, 8) : 3;
        int files = int.TryParse(GetArgValue(args, "--limit"), out var lf) ? Math.Clamp(lf, lanes, 40) : 6;
        var demo = new DownloadRenderer(files, 28);
        var rng = new Random(1);   // fixed seed: same picture every run, so a visual diff is a real change
        var lane = new DownloadTask?[lanes];
        int started = 0, finished = 0;
        Out.Dim($"Render demo: {files} file(s), {lanes} lane(s). No network, nothing written.\n");
        while (finished < files)
        {
            for (int i = 0; i < lanes; i++)
            {
                if (lane[i] is null && started < files)
                {
                    lane[i] = new DownloadTask
                    {
                        File = new GameFile { Name = $"setup_demo_game_{started + 1}.exe" },
                        GameTitle = $"Demo Game {started + 1}",
                        Index = ++started,
                        BytesTotal = 200L * 1024 * 1024 * (1 + started % 4),
                        State = DownloadTaskState.Active,
                    };
                    demo.Update(lane[i]!);
                }
                if (lane[i] is not { } t) continue;

                t.BytesReceived = Math.Min(t.BytesTotal!.Value,
                                           t.BytesReceived + (long)(rng.NextDouble() * 40 * 1024 * 1024));
                if (t.BytesReceived >= t.BytesTotal)
                {
                    t.State = t.Index % 5 == 0 ? DownloadTaskState.Failed : DownloadTaskState.Completed;
                    if (t.State == DownloadTaskState.Failed) t.Error = "demo failure";
                    demo.Update(t);
                    lane[i] = null;
                    finished++;
                }
                else demo.Update(t);
            }
            await Task.Delay(120);
        }
        demo.Finish();
        Out.Success($"Render demo done: {demo.CompletedCount} completed, {demo.FailedCount} failed.");
        return Continue;
    }
    }

    internal static async Task<int> Runs(CliContext ctx)
    {
        var command = ctx.Command;
        var paths = ctx.Paths;
        var jsonOut = ctx.JsonOut;
        var manifest = ctx.Manifest;
    {
        // The discoverable answer to "what is Grog doing / what did it just do?". Reads the journal
        // files only -- no manifest, no network -- so it is safe to poll on a timer and works while a
        // download is mid-flight in another process.
        var runsDir = Path.Combine(paths.ConfigDir, "runs");
        var currentPath = Path.Combine(runsDir, "current.json");
        if (!File.Exists(currentPath))
        {
            if (jsonOut) EmitJson(new { state = "none", note = "No run has been recorded yet." });
            else Out.Dim("No runs recorded yet. Run a download; the journal is written as it goes.");
            return Continue;
        }

        var raw = await File.ReadAllTextAsync(currentPath);
        if (jsonOut) { Console.Out.WriteLine(raw); return Continue; }   // pass through verbatim -- already the doc

        using var cur = System.Text.Json.JsonDocument.Parse(raw);
        var root = cur.RootElement;
        string Get(string n) => root.TryGetProperty(n, out var v) ? v.ToString() : "";
        var state = Get("state");
        Out.Heading($"Last run: {Get("command")} - {state}");
        Console.WriteLine($"  started ........... {Get("startedAt")}");
        Console.WriteLine($"  updated ........... {Get("updatedAt")}");
        Console.WriteLine($"  completed ......... {Get("completed")} / {Get("total")}");
        var failedTxt = Get("failed");
        if (failedTxt is not "0" and not "") Out.Warn($"  failed ............ {failedTxt}");
        else Console.WriteLine($"  failed ............ 0");
        if (Get("currentFile") is { Length: > 0 } cf) Console.WriteLine($"  last file ......... {Trunc(cf, 60)}");
        Console.WriteLine($"  event log ......... {Path.Combine(runsDir, Get("eventsFile"))}");
        if (state == "running")
            Out.Dim("  (state stays 'running' if the process was killed - compare 'updated' to now.)");
        return Continue;
    }
    }

    internal static async Task<int> Report(CliContext ctx)
    {
        var jsonOut = ctx.JsonOut;
        var manifest = ctx.Manifest;
    {
        await manifest.LoadAsync();
        var items = manifest.Current.Items;
        int Count(BackupStatus s) => items.Count(i => i.Status == s);
        int TypeCount(ProductType t) => items.Count(i => i.Type == t);
        if (jsonOut)
        {
            EmitJson(new
            {
                items = items.Count,
                byType = new
                {
                    game = TypeCount(ProductType.Game),
                    mod = TypeCount(ProductType.Mod),
                    pack = TypeCount(ProductType.Pack),
                    dlc = TypeCount(ProductType.Dlc),
                    movie = TypeCount(ProductType.Movie),
                },
                byStatus = new
                {
                    complete = Count(BackupStatus.Complete),
                    partial = Count(BackupStatus.Partial),
                    updatesAvailable = Count(BackupStatus.Outdated),
                    notDownloaded = Count(BackupStatus.NotBackedUp),
                    errors = Count(BackupStatus.Corrupt),
                },
                estimatedTotalBytes = items.SelectMany(i => i.Files).Sum(f => f.ExpectedSizeBytes ?? 0),
                lastSyncCompleted = manifest.Current.LastSyncCompleted,
            });
            return Continue;
        }
        Out.Heading($"Library report ({items.Count} items)");
        Console.WriteLine($"  games ............. {TypeCount(ProductType.Game)}");
        Console.WriteLine($"  mods .............. {TypeCount(ProductType.Mod)}");
        Console.WriteLine($"  packs ............. {TypeCount(ProductType.Pack)}");
        Console.WriteLine($"  dlc ............... {TypeCount(ProductType.Dlc)}");
        Console.WriteLine($"  movies ............ {TypeCount(ProductType.Movie)}");
        Out.Dim("  --");
        if (Count(BackupStatus.Complete) > 0) Out.Success($"  complete .......... {Count(BackupStatus.Complete)}");
        else Console.WriteLine($"  complete .......... {Count(BackupStatus.Complete)}");
        Console.WriteLine($"  partial ........... {Count(BackupStatus.Partial)}");
        Console.WriteLine($"  updates available . {Count(BackupStatus.Outdated)}");
        Console.WriteLine($"  not downloaded .... {Count(BackupStatus.NotBackedUp)}");
        if (Count(BackupStatus.Corrupt) > 0) Out.Error($"  errors ............ {Count(BackupStatus.Corrupt)}");
        else Console.WriteLine($"  errors ............ {Count(BackupStatus.Corrupt)}");
        long bytes = items.SelectMany(i => i.Files).Sum(f => f.ExpectedSizeBytes ?? 0);
        Console.WriteLine($"  est. total size ... {ByteFormat.Size(bytes)}");
        if (manifest.Current.LastSyncCompleted is { } t)
            Console.WriteLine($"  last sync ......... {t.LocalDateTime}");
        return Continue;
    }
    }

    internal static async Task<int> Reorg(CliContext ctx)
    {
        var args = ctx.Args;
        var paths = ctx.Paths;
        var manifest = ctx.Manifest;
        Grog.Core.Volumes.BackupLayout Layout() => ctx.Layout();
    {
        // CLI parity for the layout mover: headless, param-driven, no prompts. Runs to completion;
        // resumable if a prior run was interrupted. Same-drive renames for a layout change.
        await manifest.LoadAsync();
        var lay = Layout();
        var sub = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))?.ToLowerInvariant() ?? "status";
        var journal = new Grog.Core.Volumes.ReorgJournalStore(paths.ConfigDir);
        var runner = new Grog.Core.Volumes.ReorgRunner(manifest, journal);
        bool reorgVerbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        void Trace(Grog.Core.Volumes.ReorgProgress p)
        {
            if (p.CurrentFileFraction >= 1) Console.WriteLine($"  [{p.FilesDone}/{p.FilesTotal}] {p.CurrentToRel}");
        }

        switch (sub)
        {
            case "status":
            {
                var pending = runner.LoadPending();
                if (pending is not null)
                {
                    Out.Heading($"Reorg in progress ({pending.Reason}): {pending.Settled}/{pending.Total} file(s) done.");
                    Console.WriteLine("  Run 'grogcli reorg resume' to finish it.");
                }
                else
                {
                    var plan = Grog.Core.Volumes.ReorgPlanner.ForLayoutChange(manifest.Current, lay);
                    if (plan.Total == 0) Out.Success("Backups already match the current layout. Nothing to reorganize.");
                    else Out.Heading($"{plan.Total} file(s) are not in the current layout. Run 'grogcli reorg run'.");
                }
                break;
            }
            case "run":
            {
                var plan = Grog.Core.Volumes.ReorgPlanner.ForLayoutChange(manifest.Current, lay);
                if (plan.Total == 0) { Out.Success("Nothing to reorganize."); break; }
                Out.Heading($"Reorganizing {plan.Total} file(s) to the current layout…");
                if (reorgVerbose) runner.Progress += Trace;
                var result = await runner.RunAsync(plan, new Grog.Core.Volumes.ReorgControl());
                ReportReorg(result);
                await manifest.SaveAsync();
                break;
            }
            case "resume":
            {
                var pending = runner.LoadPending();
                if (pending is null) { Out.Success("No interrupted reorg to resume."); break; }
                Out.Heading($"Resuming reorg: {pending.Settled}/{pending.Total} already done…");
                if (reorgVerbose) runner.Progress += Trace;
                var result = await runner.RunAsync(pending, new Grog.Core.Volumes.ReorgControl());
                ReportReorg(result);
                await manifest.SaveAsync();
                break;
            }
            default:
                Out.Usage("Usage: reorg [status|run|resume] [--verbose]");
                break;
        }
        return Continue;
    }
    }

    internal static async Task<int> MarkMissing(CliContext ctx)
    {
        var args = ctx.Args;
        var manifest = ctx.Manifest;
    {
        // Manually mark a product's backed-up files as no longer present (lost/corrupted drive).
        // Keeps their recorded location; a later download restores them. Usage: mark-missing <game|id>
        await manifest.LoadAsync();
        var q = string.Join(' ', PositionalArgs(args.Skip(1)));
        if (string.IsNullOrWhiteSpace(q)) { Out.Usage("Usage: mark-missing <game title|id>"); return Continue; }
        var item = manifest.Current.FindItem(q);
        if (item is null) { Out.Usage($"No product matched '{q}'."); return Continue; }
        var n = Grog.Core.Sync.ManualBackupState.MarkProduct(manifest.Current, item.GogId, FileState.Missing);
        await manifest.SaveAsync();
        if (n > 0) Out.Success($"Marked {n} file(s) of '{item.Title}' as not backed up (folder kept).");
        else Out.Heading($"'{item.Title}' had no backed-up files to mark.");
        return Continue;
    }
    }

    internal static async Task<int> Backup(CliContext ctx)
    {
        var args = ctx.Args;
        var manifest = ctx.Manifest;
        bool Has(string f) => args.Contains(f, StringComparer.OrdinalIgnoreCase);
        // Scheduled/unattended entry point: resolve + preview the plan (--show-config), fail loud if
        // unconfigured, then run scan->select->place->download->verify through BackupRun.
        // Usage: backup [--to <path>] [--skip-extras] [--parallel N]
        //   [--no-verify] [--largest N] [--show-config|--dry-run] [--non-interactive]
        await manifest.LoadAsync();

        // Declarative devices: --primary / --secondary state the topology. Reconcile them (create /
        // adopt / promote / re-point a moved drive) BEFORE resolving the plan. Absence never removes a
        // device; ambiguity fails fast. --to stays as a back-compat alias for --primary.
        string? primaryArg = GetArgValue(args, "--primary") ?? GetArgValue(args, "--to");
        string? secondaryArg = GetArgValue(args, "--secondary");
        if (primaryArg is not null || secondaryArg is not null)
        {
            var rec = Grog.Core.Cli.DeviceReconciler.Reconcile(manifest, primaryArg, secondaryArg, Has("--moved"));
            foreach (var n in rec.Notes) Out.Dim(n);
            if (!rec.Ok)
            {
                Console.Error.WriteLine($"error: {rec.Error}");
                return (int)Grog.Core.Cli.ExitCode.UsageError;
            }
            await manifest.SaveAsync();
            ctx.ResetLayout();   // roots changed -> rebuild the layout against them
        }

        _ = ctx.Layout();
        string? configuredLoc = manifest.Current.PrimaryRootId is { } pid
            ? manifest.Current.Roots.FirstOrDefault(r => r.Id == pid)?.PathHint
            : null;
        var plan = Grog.Core.Cli.BackupConfigResolver.Resolve(
            primaryArg,
            configuredLoc,
            Has("--skip-extras") ? false : (bool?)null,
            manifest.Current.ExtrasLayout,
            int.TryParse(GetArgValue(args, "--parallel"), out var bp) ? bp : (int?)null,
            Has("--no-verify") ? false : (bool?)null);

        Out.Heading("Backup plan:");
        foreach (var s in plan.Settings)
            Out.Info($"  {s.Name,-24} {s.Value,-36} [{s.Source}]");   // Silent-gated: --json stdout is ONE document

        if (!plan.Configured)
            return Fail("backup", Grog.Core.Cli.ExitCode.NotConfigured, "not-configured", plan.NotConfiguredReason!);

        if (Has("--show-config") || Has("--dry-run"))
        {
            Out.Dim("Dry run -- nothing was changed.");
            return Continue;
        }

        // --- Execute. Exit codes: no account signed in -> 4; nothing fits -> 7; failed or corrupt/missing
        //     after verify -> 8; files waiting for a disconnected drive -> 10; stopped or paused -> 1; everything intact -> 0.
        // The queue's Paused fact (set by the App's Pause or `grogcli pause`) holds a scheduled run off entirely; the
        // JSON envelope says "paused" so a watcher can tell it from a failure. --ignore-pause runs anyway.
        bool ignorePause = Has("--ignore-pause");
        if (manifest.Current.Downloads.Paused && !ignorePause)
            return Fail("backup", Grog.Core.Cli.ExitCode.Error, "paused", "Downloads are paused (grogcli resume to continue)",
                new Dictionary<string, object?> { ["reason"] = "paused" });
        using var backupCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; backupCts.Cancel(); Console.WriteLine("\nStopping…"); };

        // (New items 09-09) backup --new: restrict the run to the items flagged New, the same way --updates
        // narrows to superseded files. The set is taken from the manifest AS IT STANDS -- this run's own scan
        // has not happened yet, so anything it adds is next run's --new, which is what "the new ones I can
        // see" means to someone typing it.
        System.Collections.Generic.IReadOnlySet<long>? onlyNewIds = null;
        if (Has("--new"))
        {
            onlyNewIds = Grog.Core.Sync.NewItems.Flagged(manifest.Current).Select(i => i.GogId).ToHashSet();
            if (onlyNewIds.Count == 0) { Out.Warn("Nothing is flagged New -- nothing to back up."); return Continue; }
            Out.Dim($"Restricted to {onlyNewIds.Count} item(s) flagged New.");
        }

        Out.Dim("Syncing library…");
        var host = new ConsoleBackupHost(false, false, null);
        var options = new Grog.Core.Runs.BackupRunOptions(
            Scan: Grog.Core.Runs.ScanPolicy.Always,
            OnlyGameIds: onlyNewIds,
            RunScope: BuildScope(args, manifest.Current),
            IgnoreSavedScope: Has("--ignore-saved-scope"),
            IgnorePause: ignorePause,
            VerifyAfter: !Has("--no-verify"),
            FixedConcurrency: bp >= 1 ? Math.Clamp(bp, 1, 8) : manifest.Current.Transfer.FixedConcurrency ?? 3,   // (S2.3) the library's setting unless --parallel
            DeviceMonitor: bp >= 1 ? null : new Grog.Core.Download.DeviceWriteMonitor(),
            BytesPerSecondLimit: manifest.Current.Transfer.BytesPerSecondLimit,
            KeepLargest: Has("--largest") ? (int.TryParse(GetArgValue(args, "--largest"), out var nl) && nl > 0 ? nl : 1) : null,
            JournalCommand: "backup");
        var run = new Grog.Core.Runs.BackupRun(manifest, ctx.Api, ctx.Http, ctx.Sessions, ctx.Layout(), ctx.ContentRoot(), ctx.Paths.ConfigDir, host);
        var r = await run.RunAsync(options, backupCts.Token);
        host.FinishScan();

        if (r.Error is Grog.Core.Auth.AuthExpiredException)
        {
            // Every account signed out: nothing can be fetched, and a cron run must say so LOUDLY
            // (log + stderr + exit 4) rather than "complete" against a stale catalog.
            return Fail("backup", Grog.Core.Cli.ExitCode.AuthExpired, "auth",
                "Backup skipped: no account is signed in. Run 'grog login' or 'grog account add', then re-run.");
        }
        if (r.Error is { } err)
            return Fail("backup", Grog.Core.Cli.CliExit.ForException(err), "error", Grog.Core.Api.GogError.Describe(err));
        if (r.Canceled)
            return Fail("backup", Grog.Core.Cli.ExitCode.Error, "canceled", $"Backup stopped: {r.Completed} downloaded, {r.NotLanded} failed. Re-run to resume.");
        if (r.NothingFits)
            return Fail("backup", Grog.Core.Cli.ExitCode.InsufficientSpace, "space",
                $"Won't fit: {r.HeldNoSpace} file(s), {Grog.Core.Format.ByteFormat.Size(r.HeldNoSpaceBytes)}, have no room on any storage. Free space or add a device.");
        if (r.Queued == 0) Out.Dim("Nothing to download; everything already present.");

        var v = r.Verify;
        // The after-run pass is size-only (hash re-verification is out of scope), so "verified" here means
        // "checked and intact": hash-verified earlier plus size-confirmed now. Old code reported the hash count alone (0).
        int checkedOk = (v?.Verified ?? 0) + (v?.SizeOnlyOk ?? 0);
        if (r.JournalPath is not null && !ctx.JsonOut) Out.Dim($"Run log: {r.JournalPath}");
        var envelope = new Dictionary<string, object?> { ["driveOffline"] = r.DriveOffline };
        if (r.NotLanded > 0 || (v is not null && (v.Corrupt > 0 || v.Missing > 0)))
        {
            return Fail("backup", Grog.Core.Cli.ExitCode.IntegrityFailed, "integrity",
                $"Backup finished with problems: {r.NotLanded} failed, {v?.Corrupt ?? 0} corrupt, {v?.Missing ?? 0} missing. Re-run to repair.", envelope);
        }
        if (r.HeldNoSpace > 0)
        {
            // What fit was fetched; what did not is still queued, and a nightly must not call that complete.
            return Fail("backup", Grog.Core.Cli.ExitCode.InsufficientSpace, "space",
                $"Backup incomplete: {r.Completed} downloaded, {r.HeldNoSpace} file(s) ({Grog.Core.Format.ByteFormat.Size(r.HeldNoSpaceBytes)}) have no room and stay queued. Free space or add a device.", envelope);
        }
        if (r.DriveOffline > 0)
        {
            // The queue keeps what a disconnected drive owns; a nightly must not call that complete.
            return Fail("backup", Grog.Core.Cli.ExitCode.StorageOffline, "storage-offline",
                $"{r.DriveOffline} file(s) are waiting for a disconnected drive and stay queued. Reconnect it and re-run.", envelope);
        }
        // The documented nightly recipe is `--silent --json backup`: it must leave ONE document behind.
        if (ctx.JsonOut) EmitJson(new { command = "backup", downloaded = r.Completed, unavailable = r.Refused,
            verified = checkedOk, corrupt = v?.Corrupt ?? 0, missing = v?.Missing ?? 0, driveOffline = r.DriveOffline, runLog = r.JournalPath, exitCode = 0 });
        Out.Success($"Backup complete: {r.Completed} downloaded this run, {checkedOk} verified, 0 corrupt/missing.");
        return Continue;
    }

    internal static async Task<int> Help(CliContext ctx)
    {
        var command = ctx.Command;
        var silent = ctx.Silent;
        var manifest = ctx.Manifest;
        var auth = ctx.Auth;
        await Task.CompletedTask;   // keeps the verb table uniform; this verb has no awaits
        // Bare `grog` or `grog help` -> help, exit 0. An unrecognized command -> stderr + exit 2, so
        // a mistyped command is never silently reported as success.
        if (command is not ("help" or "--help" or "-h" or "?"))
        {
            // Only IMPLEMENTED verbs may be suggested.
            var cmdSuggest = NearestFlag(command, new[] { "login","logout","whoami","status","refresh","sync","list","show","backup","download","verify","report","health","serials","cloudsaves","relocate","import","root","policy","reorg","delete","changes","changelog","account","resolve","orphans","delisted","blacklist","zipcheck","dates","pause","resume","scope","version" });
            Out.Usage($"Unknown command '{command}'." + (cmdSuggest is not null ? $" Did you mean '{cmdSuggest}'?" : "") + " Run 'grog help' for usage.");
        }
        Console.WriteLine("""
            Grog CLI -- back up your GOG library.

            Usage:
              grogcli login             Sign in with username/password (never stored)
              grogcli login --browser   Sign in via the system browser instead
              grogcli whoami            Show the signed-in account and owned product count ('status' works too)
                                        (whoami/refresh/logout take --account <id> for a named account)
              grogcli refresh           Force a token refresh (keepalive for scheduled runs)
              grogcli logout            Remove stored tokens
              grogcli sync              Fetch library details into the local manifest
              grogcli list              List games in the manifest with status
              grogcli report            Summary counts and estimated total size
              grogcli resolve           Name owned products that have no direct downloads
              grogcli show <id|title>   Dump all installers + extras for one game
              grogcli download [flt]    Download missing/updated. Scope flags:
                                          --id --title --updates --parallel N --limit-mbps --debug --largest [N]
                                          --installers-only --extras-only --skip-extras --skip-installers --no-dlc
                                          --extra-type <t[,t2]>  --os <a[,b]> [--os-fallback]  --lang <a[,b]>
                                          --type <game|mod|pack|dlc|movie>  --title-regex <rx>  --blacklist <s[,s]>
                                          --ignore-saved-scope (lift the GUI-saved scope; it is the base filter)
              grogcli blacklist [add|remove|clear]   Persistent download blacklist patterns
              grogcli health            Backup health summary (complete/corrupt/delisted/roots)
              grogcli delisted          Products no longer on GOG (still owned -- back them up)
              grogcli serials [--export]  Show/export GOG serial keys
              grogcli dates [--force]   Fill in acquisition dates from GOG order history ('sync' already does)
              grogcli zipcheck          Verify zip-extra integrity (flags corrupt archives)
              grogcli orphans [--move]  Find files on disk not in the manifest (move → .grog/orphaned)
              grogcli verify [--full]   Check local files vs manifest (size, or --full MD5)
              grogcli runs              What the last/current run is doing (survives a crash)
              grogcli import <dir>      Adopt an existing backup folder (--mode, --depth, --verify-checksums
                                        fetches GOG's checksum for adopted files that have none on record)
              grogcli root [list|add|remove|removable|lost|replace|set-primary|purpose|mode|reset]  Manage backup storage
              grogcli root detach <label>              Shelve a drive: files still count as backed up, slot freed
              grogcli root reattach <label>            A detached drive back into the free slot (no swap in the CLI)
              grogcli root forget <label> --yes        Stop tracking a drive: its files count as not backed up
              grogcli pause | resume                   Hold / release the download queue ('backup' honours it)
              grogcli scope [show|set]                 The saved scope (base filter): --os <list> --lang <list>
                                                         [--installers-only|--with-extras] [--games|--no-games]
              grogcli policy [show|games|extras|game]  Route content to drives
              grogcli relocate <game> --to <root>      Move a game's files to another drive
              grogcli reorg [status|run|resume]        Reorganize backups to the current layout (resumable)
              grogcli cloudsaves list                  List account cloud saves (auto-discovery)
              grogcli cloudsaves <game> [--download]   Show/save one game's cloud local save
              grogcli mark-missing <game>              Mark a product's files as no longer backed up (lost drive)
              grogcli backup [flags]                   Scheduled run: sync -> fit-check -> download -> verify
                                                         --primary=<path> [--secondary=<path>] [--moved]
                                                         --skip-extras --parallel N --no-verify
                                                         --ignore-saved-scope (lift the GUI-saved scope)
                                                         --ignore-pause (run even while downloads are paused)
                                                         --show-config|--dry-run (preview only)

            Devices (declarative): --primary=<path> / --secondary=<path> state where backups live; Grog
              creates/adopts/promotes them and re-points a moved drive (--moved). Omitting a device never
              removes it. Retire one explicitly: grogcli root lost <label> --yes  (or root removable <l>).

            Global flags (any command):
              --silent            Scheduled/headless: no stdout or progress; failures still go to stderr
                                  and are appended to <config>/logs/grog.log. Never prompts.
              --json              Machine-readable output. Supported: whoami, list, report, health, verify,
                                  root list, orphans, delisted, runs, download, backup, pause, resume, scope,
                                  root detach/reattach/forget. Suppresses all human chatter.
              --yes               Confirm a destructive action without prompting.
              --version           Print the CLI version ('grogcli version' works too).
              --root <path>       Use a specific state/backup root (else GROG_BACKUP_ROOT, else exe dir).

            Global flags may go before OR after the command: both `grogcli --json report` and
              `grogcli report --json` work. (A multi-word value placed before the command needs the
              --name=value form: grogcli --title="Quest for Glory" download.)

            Exit codes: 0 ok · 1 error · 2 usage · 3 not-configured · 4 auth-expired ·
              5 login-blocked (captcha/2FA) · 6 network · 7 out-of-space (or files held for space)
              8 integrity-failed (corrupt/missing, or files that did not land) · 9 app-running (writer verb
              refused while Grog is open, or another grogcli holds the profile) · 10 storage-offline (files
              waiting for a disconnected drive stay queued) · 1 also = stopped (Ctrl-C), or downloads paused
              (backup refused to start: kind "paused", reason "paused" in the --json error; 'resume' or
              --ignore-pause clears it)

            WATCHING A RUN (automation). Three ways, cheapest first:
              1. Exit code       Every command exits per the table above. Enough for pass/fail alerting.
              2. grogcli --json runs        The live summary: state, completed/total, failed, last file.
                                 Reads one small file. Safe to poll on a timer WHILE a download runs in
                                 another process, and it is still there after the run ends.
              3. The event log   <config>/runs/run-<stamp>-<command>.jsonl - one JSON object per line,
                                 appended as each file finishes, flushed to disk immediately. Tail it for
                                 live progress, or read it AFTER a crash to see exactly what did land.
                                 Events: run.started, file.completed, file.failed, run.finished, plus the
                                 uncounted file.canceled / file.unavailable / file.skipped.

              A run that was killed outright (power loss, task manager) leaves a journal with NO
              run.finished line and a 'current.json' still saying "running" - that is how you detect it.
              Compare its updatedAt to the clock. Everything downloaded before the kill is still recorded,
              and the manifest is checkpointed every few seconds so Grog does not forget that work either.
              Under --json/--silent the progress pulse (starting/completed lines, a 10 s heartbeat) goes to
              STDERR, so a quiet run is never a silent one and stdout stays one document.

              Failures under --json are written to STDERR as one JSON object
              ({error, command, kind, message, exitCode}); stdout carries only the success document.
              So: parse stdout always, read stderr only when the exit code is non-zero.
            """);
        return Continue;
    }

    // ---- shared helpers (were static local functions of Program.cs) ----
// Emit a value as a single JSON document to stdout (for --json). Uses the original stdout even under
// --silent so the machine-readable output always lands.
    internal static void EmitJson(object value)
        => Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(value,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

    // A failure as data, on STDERR -- stdout under --json is reserved for the success document, so a watcher can
    // parse stdout unconditionally and read stderr only when the exit code is non-zero.
    /// <summary>THE non-zero exit path for a verb: under --json the failure is a JSON document on stderr (the
    /// docs/CLI.md contract: "read stderr only when the exit code is non-zero"), otherwise a red line - and
    /// never Out.Warn, which --silent swallows entirely. Returns the code so call sites read `return Fail(...)`.</summary>
    internal static int Fail(string command, Grog.Core.Cli.ExitCode code, string kind, string message,
                             IReadOnlyDictionary<string, object?>? extra = null)
    {
        if (CliText.JsonMode) EmitJsonError(command, kind, message, (int)code, extra);
        else Out.Error(message);
        return (int)code;
    }

    /// <summary><paramref name="extra"/> adds verb-specific fields (backup's driveOffline) after the fixed ones.</summary>
    internal static void EmitJsonError(string command, string kind, string message, int exitCode,
                                       IReadOnlyDictionary<string, object?>? extra = null)
    {
        var doc = new Dictionary<string, object?>
            { ["error"] = true, ["command"] = command, ["kind"] = kind, ["message"] = message, ["exitCode"] = exitCode };
        if (extra is not null) foreach (var (k, val) in extra) doc[k] = val;
        Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(doc,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    internal static char Symbol(BackupStatus s) => s switch
    {
        BackupStatus.Complete => '+',
        BackupStatus.Partial => '~',
        BackupStatus.Outdated => '^',
        BackupStatus.Corrupt => '!',
        BackupStatus.NotBackedUp => '-',
        _ => '?',
    };

    internal static string Trunc(string s, int n) => CliText.Trunc(s, n);

    // Builds a DownloadScope from CLI flags, layered over any saved defaults in the manifest.
    //   --installers-only / --extras-only / --skip-extras / --skip-installers
    //   --extra-type <t[,t2]>   (soundtrack, artbook, manual, ...)
    //   --os <a[,b]> [--os-fallback]   --lang <a[,b]>
    //   --type <game|mod|pack|dlc|movie>   --title-regex <rx>
    //   --no-dlc   --blacklist <s[,s2]>
    /// <summary>The manifest's saved scope as the ONE shared predicate (<see cref="Grog.Core.Sync.Scope"/>),
    /// or null when there is nothing to apply: no scope was ever seeded (pre-scope manifest, GUI never run)
    /// or the user lifted it with --ignore-saved-scope. Applied as a BASE filter under the per-run
    /// DownloadScope flags, so a GUI-configured narrowing is honored by `grog backup` from cron on any OS.</summary>
    internal static Grog.Core.Sync.Scope? SavedScope(string[] args, Grog.Core.Manifest.LibraryManifest m)
        => args.Contains("--ignore-saved-scope", StringComparer.OrdinalIgnoreCase) ? null : m.Scope?.ToScope();

    internal static Grog.Core.Download.DownloadScope BuildScope(string[] args, Grog.Core.Manifest.LibraryManifest m)
    {
        var scope = new Grog.Core.Download.DownloadScope();

        bool Has(string f) => args.Contains(f, StringComparer.OrdinalIgnoreCase);
        string? Val(string f) => GetArgValue(args, f);
        static IEnumerable<string> Split(string? v) =>
            (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (Has("--installers-only")) { scope.IncludeExtras = false; scope.IncludePatches = false; }
        if (Has("--extras-only")) { scope.IncludeInstallers = false; scope.IncludePatches = false; scope.IncludeDlcInstallers = false; }
        if (Has("--skip-extras")) scope.IncludeExtras = false;
        if (Has("--skip-installers")) scope.IncludeInstallers = false;
        if (Has("--no-dlc")) scope.IncludeDlcInstallers = false;

        foreach (var t in Split(Val("--extra-type"))) scope.ExtraTypes.Add(t);

        foreach (var o in Split(Val("--os"))) scope.OsPriority.Add(o);
        if (Has("--os-fallback")) scope.OsFallback = true;
        foreach (var l in Split(Val("--lang"))) scope.Languages.Add(l);

        foreach (var ty in Split(Val("--type")))
            if (Enum.TryParse<Grog.Core.Models.ProductType>(ty, ignoreCase: true, out var pt)) scope.Types.Add(pt);
        scope.TitleRegex = Val("--title-regex");

        foreach (var b in Split(Val("--blacklist"))) scope.Blacklist.Add(b);

        // Persisted defaults: fall back to manifest.Settings["scope.blacklist"] etc. if no flag given.
        if (scope.Blacklist.Count == 0 && m.Settings.TryGetValue("scope.blacklist", out var bl))
            foreach (var b in Split(bl)) scope.Blacklist.Add(b);

        return scope;
    }

    internal static void ReportReorg(Grog.Core.Volumes.ReorgResult r)
    {
        var msg = $"Reorganized {r.Moved} file(s)"
            + (r.Skipped > 0 ? $", {r.Skipped} skipped" : "")
            + (r.Failed > 0 ? $", {r.Failed} FAILED" : "") + ".";
        if (r.Failed > 0) Out.Error(msg); else Out.Success(msg);
    }

    // Closest known flag to a mistyped one, for "did you mean?" -- nearest by Levenshtein distance, but only
    // when it's actually close (<=3 edits, or half the length), so we don't suggest nonsense.
    internal static string? NearestFlag(string typed, System.Collections.Generic.IEnumerable<string> known)
    {
        string? best = null; int bestD = int.MaxValue;
        foreach (var k in known)
        {
            int d = Levenshtein(typed.ToLowerInvariant(), k.ToLowerInvariant());
            if (d < bestD) { bestD = d; best = k; }
        }
        return bestD <= Math.Max(3, typed.Length / 2) ? best : null;

        static int Levenshtein(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                                       d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }
    }

    internal static string? GetArgValue(string[] args, string name)
    {
        // Preferred form: --name=value (one token, unambiguous -- no greedy multi-word capture). Works for
        // paths with spaces when quoted by the shell (--primary="C:\My Games\Grog").
        foreach (var a in args)
            if (a.Length > name.Length + 1 && a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return a[(name.Length + 1)..];

        // Legacy form: --name value [more words], capturing up to the next --flag so multi-word values like
        // --title Quest of the Avatar read as one value.
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                var parts = new List<string>();
                for (int j = i + 1; j < args.Length && !args[j].StartsWith("--"); j++)
                    parts.Add(args[j]);
                return parts.Count > 0 ? string.Join(' ', parts) : null;
            }
        return null;
    }

    // Returns only positional tokens: drops --flags and, for value-taking flags, their values.
    internal static System.Collections.Generic.IEnumerable<string> PositionalArgs(System.Collections.Generic.IEnumerable<string> args)
    {
        // Every flag that takes a VALUE must be listed, or a value-flag placed before the positional gets its
        // value mistaken for the positional (e.g. `import --mode adopt /path` targeting a folder named "adopt").
        string[] valuedFlags = { "--root", "--title", "--id", "--os", "--lang", "--parallel", "--limit-mbps", "--type", "--label", "--with", "--to", "--role", "--extra-type", "--title-regex", "--blacklist", "--client", "--mode", "--depth", "--keep", "--account", "--game" };
        var list = args.ToList();
        var result = new List<string>();
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a.StartsWith("--"))
            {
                if (valuedFlags.Contains(a, StringComparer.OrdinalIgnoreCase))
                    while (i + 1 < list.Count && !list[i + 1].StartsWith("--")) i++; // skip value(s)
                continue;
            }
            result.Add(a);
        }
        return result;
    }
}

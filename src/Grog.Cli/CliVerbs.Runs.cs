// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Format;
using Grog.Core.Runs;

namespace Grog.Cli;

/// <summary>Verbs that are thin shells over the Core orchestrators (S2.1, 09-08): the sequence lives in
/// <c>Grog.Core.Runs</c>, the verb parses flags, renders the result and picks the exit code.</summary>
internal static partial class CliVerbs
{
    /// <summary>
    /// <c>grogcli fix [--failed|--corrupt|--missing|--game &lt;id&gt;] [--dry-run] [--parallel N]</c>: re-queue
    /// the problem files at the front of the queue (strikes cleared, fresh copies) and drain the queue
    /// through <see cref="BackupRun"/>, exactly as the App's Fix / Try again do. No flag = corrupt + missing
    /// (the Overview "Fix It"). <c>--dry-run</c> lists what would be re-queued and changes nothing.
    /// Exit codes: nothing to fix -> 0; queued but a fetch failed or verify found problems -> 8;
    /// no account -> 4; nothing fits -> 7; stopped -> 1.
    /// </summary>
    internal static async Task<int> Fix(CliContext ctx)
    {
        var args = ctx.Args;
        var manifest = ctx.Manifest;
        bool Has(string f) => args.Contains(f, StringComparer.OrdinalIgnoreCase);
        await manifest.LoadAsync();

        // ---- selector ----
        FixKind kinds = FixKind.None;
        if (Has("--failed")) kinds |= FixKind.FailedDownloads;
        if (Has("--corrupt")) kinds |= FixKind.CorruptFiles;
        if (Has("--missing")) kinds |= FixKind.MissingFiles;
        FixSelector selector;
        if (GetArgValue(args, "--game") is { } gameArg)
        {
            if (!long.TryParse(gameArg, out var gogId))
            {
                Out.Usage("Usage: fix [--failed|--corrupt|--missing|--game <gogId>] [--dry-run]");
                return (int)Grog.Core.Cli.ExitCode.UsageError;
            }
            if (manifest.Current.ItemById(gogId) is null)
                return Fail("fix", Grog.Core.Cli.ExitCode.UsageError, "usage", $"No library item has id {gogId}. Run 'list' to find it.");
            selector = FixSelector.Game(gogId);
        }
        else selector = new FixSelector(kinds == FixKind.None ? FixKind.CorruptFiles | FixKind.MissingFiles : kinds);

        // ---- what it touches (read-only) ----
        var affected = FixRun.Select(manifest, selector);
        if (affected.Count == 0)
        {
            if (ctx.JsonOut) EmitJson(new { command = "fix", requeued = 0, downloaded = 0, exitCode = 0 });
            Out.Success("Nothing to fix - no files match.");
            return Continue;
        }
        long bytes = affected.Sum(a => a.File.ExpectedSizeBytes ?? 0);
        var label = selector.Kinds switch
        {
            FixKind.Game => $"file(s) of {affected[0].Item.Title}",
            FixKind.FailedDownloads => "failed download(s)",
            FixKind.CorruptFiles => "corrupt file(s)",
            FixKind.MissingFiles => "missing file(s)",
            _ => "problem file(s)",
        };
        Out.Heading($"{affected.Count} {label}, {ByteFormat.Size(bytes)}:");
        foreach (var (item, f) in affected)
            Out.Info($"  {Trunc(item.Title, 40),-40} {Trunc(f.Name, 40),-40} {f.State}");

        if (Has("--dry-run"))
        {
            if (ctx.JsonOut) EmitJson(new
            {
                command = "fix", dryRun = true, wouldRequeue = affected.Count, bytes,
                files = affected.Select(a => new { gogId = a.Item.GogId, title = a.Item.Title, fileKey = a.File.FileKey, name = a.File.Name, state = a.File.State.ToString() }),
                exitCode = 0,
            });
            Out.Dim("Dry run -- nothing was changed.");
            return Continue;
        }

        // ---- re-queue (saved inside) ----
        var fix = await FixRun.RequeueAsync(manifest, selector);
        Out.Dim($"Re-queued {fix.Count} file(s) at the top of the queue. Fetching…");

        // ---- drain the persisted queue, the way `backup` runs, minus the scan (the queue is the selection) ----
        if (string.IsNullOrWhiteSpace(ctx.ContentRoot()))
            return Fail("fix", Grog.Core.Cli.ExitCode.NotConfigured, "not-configured",
                "Files are queued but no backup location is configured. Run 'backup --primary=<path>' once, then 'fix' again.");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("\nStopping…"); };
        int bp = int.TryParse(GetArgValue(args, "--parallel"), out var p) ? p : 0;
        var host = new ConsoleBackupHost(false, false, null);
        var options = new BackupRunOptions(
            Scan: ScanPolicy.Never,
            FromPersistedQueue: true,
            VerifyAfter: !Has("--no-verify"),
            FixedConcurrency: bp >= 1 ? Math.Clamp(bp, 1, 8) : manifest.Current.Transfer.FixedConcurrency ?? 3,   // (S2.3)
            DeviceMonitor: bp >= 1 ? null : new Grog.Core.Download.DeviceWriteMonitor(),
            BytesPerSecondLimit: manifest.Current.Transfer.BytesPerSecondLimit,
            JournalCommand: "fix");
        var run = new BackupRun(manifest, ctx.Api, ctx.Http, ctx.Sessions, ctx.Layout(), ctx.ContentRoot(), ctx.Paths.ConfigDir, host);
        var r = await run.RunAsync(options, cts.Token);
        host.FinishScan();

        if (r.Error is Grog.Core.Auth.AuthExpiredException)
            return Fail("fix", Grog.Core.Cli.ExitCode.AuthExpired, "auth",
                "Files are re-queued but nothing was fetched: no account is signed in. Run 'grog login', then 'backup'.");
        if (r.Error is { } err)
            return Fail("fix", Grog.Core.Cli.CliExit.ForException(err), "error", Grog.Core.Api.GogError.Describe(err));
        if (r.Canceled)
            return Fail("fix", Grog.Core.Cli.ExitCode.Error, "canceled", $"Fix stopped: {r.Completed} downloaded, {r.NotLanded} failed. Run 'backup' to resume.");
        if (r.NothingFits)
            return Fail("fix", Grog.Core.Cli.ExitCode.InsufficientSpace, "space",
                $"Won't fit: {r.HeldNoSpace} file(s), {ByteFormat.Size(r.HeldNoSpaceBytes)}, have no room on any storage. Free space or add a device.");
        if (r.JournalPath is not null && !ctx.JsonOut) Out.Dim($"Run log: {r.JournalPath}");
        var v = r.Verify;
        if (r.NotLanded > 0 || (v is not null && (v.Corrupt > 0 || v.Missing > 0)))
            return Fail("fix", Grog.Core.Cli.ExitCode.IntegrityFailed, "integrity",
                $"Fix finished with problems: {r.NotLanded} failed, {v?.Corrupt ?? 0} corrupt, {v?.Missing ?? 0} missing. Re-run to repair.");
        if (r.HeldNoSpace > 0)
            return Fail("fix", Grog.Core.Cli.ExitCode.InsufficientSpace, "space",
                $"Fix incomplete: {r.Completed} downloaded, {r.HeldNoSpace} file(s) ({ByteFormat.Size(r.HeldNoSpaceBytes)}) have no room and stay queued.");
        if (ctx.JsonOut) EmitJson(new { command = "fix", requeued = fix.Count, downloaded = r.Completed, unavailable = r.Refused,
            corrupt = v?.Corrupt ?? 0, missing = v?.Missing ?? 0, runLog = r.JournalPath, exitCode = 0 });
        Out.Success($"Fixed: {r.Completed} of {fix.Count} file(s) fetched fresh.");
        return Continue;
    }

    /// <summary>
    /// <c>grogcli dates [--force]</c>: fill in acquisition dates from GOG's order history through Core's
    /// <see cref="PurchaseDatesRun"/>. Normally there is nothing to do -- an interactive <c>sync</c> already
    /// runs this pass, and once a complete walk has answered for every product the pass makes no request at
    /// all -- so the verb exists to catch up a library that predates the feature and to state plainly how
    /// many products GOG's history simply does not cover. <c>--force</c> re-opens every settled answer and
    /// walks every page again, for when GOG's history is believed to have changed.
    /// Exit codes: no account -> 4; otherwise 0, including "nothing to date".
    /// </summary>
    internal static async Task<int> Dates(CliContext ctx)
    {
        var manifest = ctx.Manifest;
        await manifest.LoadAsync();
        bool force = ctx.Args.Contains("--force", StringComparer.OrdinalIgnoreCase);

        int total = manifest.Current.Items.Count;
        if (total == 0)
        {
            if (ctx.JsonOut) EmitJson(new { command = "dates", dated = 0, undated = 0, settled = 0, requests = 0, exitCode = 0 });
            Out.Success("Nothing to date - the library is empty. Run 'sync' first.");
            return Continue;
        }

        // --force re-opens the settled answers FIRST, so the run that follows sees them as unanswered and
        // walks every page again. Without this the run would correctly decide there was nothing to ask.
        if (force)
        {
            int reopened = 0;
            manifest.Mutate(m => reopened = PurchaseDatesRun.Reopen(m));   // (manifest gate 09-08)
            if (reopened > 0) Out.Dim($"--force: re-asking about {reopened} item(s) previously answered 'no order record'.");
        }

        int unanswered = PurchaseDatesRun.UnansweredCount(manifest.Current);
        if (unanswered == 0)
        {
            int already = PurchaseDatesRun.SettledCount(manifest.Current);
            if (ctx.JsonOut) EmitJson(new { command = "dates", dated = 0, undated = PurchaseDatesRun.UndatedCount(manifest.Current),
                                            settled = already, requests = 0, exitCode = 0 });
            Out.Success($"Nothing to ask: {total - already} item(s) dated, {already} answered 'no order record'.");
            return Continue;
        }

        try
        {
            // Every connected account's order history, each on its own client (sweep 2 #9).
            var r = manifest.Current.Accounts.Count == 0
                ? await PurchaseDatesRun.RunAsync(manifest, ctx.Api, CancellationToken.None)
                : await PurchaseDatesRun.RunForAccountsAsync(manifest, PurchaseDatesRun.OrdersOf(ctx.Sessions), CancellationToken.None);
            int undated = PurchaseDatesRun.UndatedCount(manifest.Current);
            int settled = PurchaseDatesRun.SettledCount(manifest.Current);

            if (ctx.JsonOut) EmitJson(new { command = "dates", dated = r.Dated, undated, settled,
                                            requests = r.Requests, backfill = r.Backfilled, exitCode = 0 });
            Out.Success(r.Dated == 0
                ? $"No new dates: order history covers none of the {unanswered} item(s) asked about."
                : $"Dated {r.Dated} item(s) from order history ({r.Requests} request(s)).");
            // Not a warning: GOG's order history genuinely does not cover Connect imports, bundled DLC or
            // pre-order grants. Saying it is SETTLED matters more than saying it is missing -- it is why
            // this will not be asked again, and why the next scan costs nothing.
            if (r.Settled > 0)
                Out.Dim($"{r.Settled} item(s) are not in GOG's order history (Connect imports, DLC bundled "
                      + "under a base game, pre-order grants). Answered - they will not be asked about again.");
            else if (undated > 0 && settled == undated)
                Out.Dim($"{undated} item(s) have no date and are already answered; nothing further to ask.");
            return Continue;
        }
        catch (Grog.Core.Auth.AuthExpiredException)
        {
            return Fail("dates", Grog.Core.Cli.ExitCode.AuthExpired, "auth",
                "Order history needs a signed-in account. Run 'grog login', then 'dates'.");
        }
        catch (Exception ex)
        {
            return Fail("dates", Grog.Core.Cli.CliExit.ForException(ex), "error", Grog.Core.Api.GogError.Describe(ex));
        }
    }
}

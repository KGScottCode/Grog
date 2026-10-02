// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Format;
using Grog.Core.Runs;

namespace Grog.Cli;

/// <summary>Cloud-save and layout verbs that are thin shells over <see cref="CloudSaveRun"/> and
/// <see cref="LayoutMigrationRun"/> (S2.1, 09-08): the verb parses flags, renders the result and picks the
/// exit code; the sequence -- per-account sessions, dated archive, retention, bookkeeping, journaled moves --
/// is Core's and shared with the App.</summary>
internal static partial class CliVerbs
{
    /// <summary>Parses <c>--keep N</c> (0 = keep every local save). Null on a malformed value.</summary>
    private static int? ParseKeep(string[] args, out bool given, int libraryDefault = 0)
    {
        var raw = GetArgValue(args, "--keep");
        given = raw is not null;
        if (raw is null) return libraryDefault;   // (S2.3) the library's retention setting unless --keep
        return int.TryParse(raw, out var n) && n >= 0 ? n : null;
    }

    /// <summary>Pre-flight for cloud verbs: a backup location (the archive lives under it) and a session that can
    /// authenticate WITHOUT an interactive prompt. Null = fine; else the exit code already reported.</summary>
    private static async Task<int?> CloudPreflightAsync(CliContext ctx, string command)
    {
        if (string.IsNullOrWhiteSpace(ctx.ContentRoot()))
            return Fail(command, Grog.Core.Cli.ExitCode.NotConfigured, "not-configured",
                "No backup location is configured. Run 'backup --primary=<path>' once, then re-run this.");
        var accounts = ctx.Manifest.Current.Accounts;
        if (accounts.Count > 0)
        {
            if (!accounts.Any(a => ctx.Sessions.IsConnected(a.Id)))
                return Fail(command, Grog.Core.Cli.ExitCode.AuthExpired, "auth",
                    "No account is signed in. Open the Grog app and connect your GOG account (or run 'grog login'), then re-run this.");
            return null;
        }
        // Legacy single session: the token file is the cheap probe; a missing one would otherwise send
        // EnsureAuthenticatedAsync into a blocking console login and look like a hang.
        if (await ctx.TokenStore.LoadAsync() is null)
            return Fail(command, Grog.Core.Cli.ExitCode.AuthExpired, "auth",
                "Not logged in. Open the Grog app and connect your GOG account (or run 'grog login'), then re-run this.");
        return null;
    }

    /// <summary>
    /// <c>grogcli cloudsaves --all [--keep N] [--account &lt;id|name&gt;] [--force]</c>: back up every game GOG's
    /// discovery confirmed has cloud saves, per account, into dated local saves -- the App's Download All. Games
    /// already up to date are skipped unless <c>--force</c>. <c>--keep N</c> then keeps only the N newest local
    /// saves per game and account (0 = keep all). Exit codes: all landed -> 0; a game failed -> 8; stopped -> 1;
    /// no session -> 4; no backup location -> 3.
    /// </summary>
    internal static async Task<int> CloudsavesAll(CliContext ctx)
    {
        var args = ctx.Args;
        var manifest = ctx.Manifest;
        bool Has(string f) => args.Contains(f, StringComparer.OrdinalIgnoreCase);
        await manifest.LoadAsync();
        _ = ctx.Layout();

        var keep = ParseKeep(args, out _, ctx.Manifest.Current.Transfer.KeepCloudSaves);
        if (keep is null) { Out.Usage("Usage: cloudsaves --all [--keep N] [--account <id|name>] [--force]"); return (int)Grog.Core.Cli.ExitCode.UsageError; }
        if (await CloudPreflightAsync(ctx, "cloudsaves") is { } pre) return pre;

        var selection = CloudSaveSelection.All;
        if (GetArgValue(args, "--account") is { } acct)
        {
            var a = manifest.Current.Accounts.FirstOrDefault(x =>
                string.Equals(x.Id, acct, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Username, acct, StringComparison.OrdinalIgnoreCase));
            if (a is null)
                return Fail("cloudsaves", Grog.Core.Cli.ExitCode.UsageError, "usage", $"No registered account matched '{acct}'.");
            selection = CloudSaveSelection.ForAccount(a.Id);
        }

        var host = new ConsoleBackupHost(false, false, null);
        var run = new CloudSaveRun(manifest, ctx.Http, ctx.Auth, ctx.Sessions, ctx.ContentRoot(), host);
        var targets = run.Targets(selection);
        if (targets.Count == 0)
        {
            if (ctx.JsonOut) EmitJson(new { command = "cloudsaves", backedUp = 0, skipped = 0, failed = 0, pruned = 0, exitCode = 0 });
            Out.Dim("No cloud saves known yet. Run 'cloudsaves list' first to look.");
            return Continue;
        }
        Out.Heading($"Backing up cloud saves for {targets.Count} game(s)" +
                    (keep > 0 ? $", keeping the newest {keep} per game…" : "…"));

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("\nStopping…"); };
        var r = await run.DownloadAsync(selection, keep.Value, cts.Token, skipUpToDate: !Has("--force"));

        foreach (var e in r.Entries)
        {
            var mark = e.Skipped ? "=" : e.Ok ? (e.Partial ? "~" : "+") : "!";
            var note = e.Skipped ? "up to date" : e.Ok ? $"{e.FilesWritten}/{e.FilesExpected} file(s)" + (e.Pruned > 0 ? $", {e.Pruned} old pruned" : "")
                     : e.Error ?? "GOG refused (0 files)";
            Out.Info($"  {mark} {Trunc(e.Title, 46),-46} {note}");
        }
        if (ctx.JsonOut) EmitJson(new
        {
            command = "cloudsaves", backedUp = r.Ok, skipped = r.Skipped, failed = r.Failed, pruned = r.Pruned, canceled = r.Canceled,
            entries = r.Entries.Select(e => new { gogId = e.GogId, title = e.Title, accountId = e.AccountId, files = e.FilesWritten,
                expected = e.FilesExpected, dir = e.LocalSaveDir, pruned = e.Pruned, skipped = e.Skipped, error = e.Error }),
            exitCode = r.Canceled ? 1 : r.Failed > 0 ? 8 : 0,
        });
        if (r.Canceled)
            return Fail("cloudsaves", Grog.Core.Cli.ExitCode.Error, "canceled", $"Stopped - backed up {r.Ok} game(s).");
        if (r.Failed > 0)
            return Fail("cloudsaves", Grog.Core.Cli.ExitCode.IntegrityFailed, "integrity",
                $"Backed up {r.Ok} game(s), {r.Failed} failed (not marked as backed up). Re-run to retry.");
        Out.Success($"Backed up {r.Ok} game(s)" + (r.Skipped > 0 ? $", {r.Skipped} already up to date" : "") +
                    (r.Pruned > 0 ? $", {r.Pruned} older local save(s) removed." : "."));
        return Continue;
    }

    /// <summary>
    /// <c>grogcli cloudsaves prune --keep N</c>: retention only -- keep the N newest local saves of every
    /// (game, account) archive, delete the rest, re-derive the bookkeeping from what is left. Nothing is
    /// downloaded. <c>--keep</c> is required (0 would mean "keep everything" and do nothing).
    /// </summary>
    internal static async Task<int> CloudsavesPrune(CliContext ctx)
    {
        var args = ctx.Args;
        var manifest = ctx.Manifest;
        await manifest.LoadAsync();
        _ = ctx.Layout();

        var keep = ParseKeep(args, out var given);
        if (keep is null || !given || keep <= 0)
        {
            Out.Usage("Usage: cloudsaves prune --keep N   (N >= 1: local saves to keep per game and account)");
            return (int)Grog.Core.Cli.ExitCode.UsageError;
        }
        if (string.IsNullOrWhiteSpace(ctx.ContentRoot()))
            return Fail("cloudsaves", Grog.Core.Cli.ExitCode.NotConfigured, "not-configured",
                "No backup location is configured. Run 'backup --primary=<path>' once, then re-run this.");

        var host = new ConsoleBackupHost(false, false, null);
        var run = new CloudSaveRun(manifest, ctx.Http, ctx.Auth, ctx.Sessions, ctx.ContentRoot(), host);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("\nStopping…"); };
        CloudSavePruneResult r;
        try { r = await run.PruneAsync(keep.Value, cts.Token); }
        catch (OperationCanceledException) { return Fail("cloudsaves", Grog.Core.Cli.ExitCode.Error, "canceled", "Stopped."); }

        if (ctx.JsonOut) EmitJson(new
        {
            command = "cloudsaves", keep = keep.Value, deleted = r.Deleted, games = r.Touched,
            entries = r.Entries.Where(e => e.Deleted > 0).Select(e => new { gogId = e.GogId, title = e.Title, accountId = e.AccountId, deleted = e.Deleted, remaining = e.Remaining }),
            exitCode = 0,
        });
        if (r.Deleted == 0) Out.Success($"Nothing to prune - no game has more than {keep} local save(s).");
        else Out.Success($"Removed {r.Deleted} older local save(s) across {r.Touched} game(s); the newest {keep} per game were kept.");
        return Continue;
    }

    /// <summary>
    /// <c>grogcli reorg layout [--dry-run] [--verbose]</c>: the App's layout-migration prompt, headless. Plans
    /// what is out of place for the current layout (only files actually on disk are reported, and nothing is
    /// offered while an unfinished reorg journal owns the files -- run 'reorg resume'), then moves them with
    /// the journaled runner. Exit codes: nothing to do / all moved -> 0; a move failed -> 8; paused (a drive
    /// went away) or stopped -> 1; a needed drive is offline -> 3.
    /// </summary>
    internal static async Task<int> ReorgLayout(CliContext ctx)
    {
        var args = ctx.Args;
        var manifest = ctx.Manifest;
        bool Has(string f) => args.Contains(f, StringComparer.OrdinalIgnoreCase);
        await manifest.LoadAsync();
        _ = ctx.Layout();
        if (string.IsNullOrWhiteSpace(ctx.ContentRoot()))
            return Fail("reorg", Grog.Core.Cli.ExitCode.NotConfigured, "not-configured",
                "No backup location is configured. Run 'backup --primary=<path>' once, then re-run this.");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("\nStopping…"); };

        var journal = new Grog.Core.Volumes.ReorgJournalStore(ctx.Paths.ConfigDir);
        if (journal.Load() is { } pending && !pending.IsComplete)
        {
            if (ctx.JsonOut) EmitJson(new { command = "reorg", pending = true, done = pending.Settled, total = pending.Total, exitCode = 0 });
            Out.Warn($"A reorganize is already in progress ({pending.Settled}/{pending.Total} file(s) done). Run 'reorg resume' to finish it first.");
            return Continue;
        }

        var plan = await LayoutMigrationRun.PlanAsync(manifest, ctx.ContentRoot(), cts.Token, ctx.Paths.ConfigDir);
        if (plan is null)
        {
            if (ctx.JsonOut) EmitJson(new { command = "reorg", moves = 0, exitCode = 0 });
            Out.Success("Backups already match the current layout. Nothing to reorganize.");
            return Continue;
        }
        if (plan.HasUnavailableDrives)
            return Fail("reorg", Grog.Core.Cli.ExitCode.NotConfigured, "drive-offline",
                $"Can't move files: {string.Join(", ", plan.UnavailableDrives)} is not connected. Reconnect it and try again.");

        Out.Heading($"{plan.Count} file(s) across {plan.Games} game(s), {ByteFormat.Size(plan.Bytes)}, are not in the current layout:");
        foreach (var mv in plan.Moves.Take(Has("--verbose") ? int.MaxValue : 20))
            Out.Info($"  {Trunc(mv.Title, 32),-32} {mv.FromRel}  ->  {mv.ToRel}");
        if (!Has("--verbose") && plan.Count > 20) Out.Dim($"  … and {plan.Count - 20} more (--verbose lists all).");

        if (Has("--dry-run"))
        {
            if (ctx.JsonOut) EmitJson(new
            {
                command = "reorg", dryRun = true, moves = plan.Count, games = plan.Games, bytes = plan.Bytes,
                files = plan.Moves.Select(m => new { gogId = m.GogId, title = m.Title, from = m.FromRel, to = m.ToRel }),
                exitCode = 0,
            });
            Out.Dim("Dry run -- nothing was moved.");
            return Continue;
        }

        Out.Dim("Moving… (same-drive renames; nothing is deleted). Press Ctrl-C to stop; 'reorg resume' finishes later.");
        var host = new ConsoleBackupHost(false, false, null);
        void Trace(Grog.Core.Volumes.ReorgProgress p)
        {
            if (p.CurrentFileFraction >= 1) Out.Info($"  [{p.FilesDone}/{p.FilesTotal}] {p.CurrentToRel}");
        }
        var r = await LayoutMigrationRun.RunAsync(manifest, ctx.ContentRoot(), plan, host, Has("--verbose") ? (Action<Grog.Core.Volumes.ReorgProgress>)Trace : null,
                                                  cts.Token, configDir: ctx.Paths.ConfigDir);

        if (r.Error is { } err)
            return Fail("reorg", Grog.Core.Cli.CliExit.ForException(err), "error", $"Reorganize failed: {err.Message}. The plan is journaled; run 'reorg resume'.");
        var line = $"{r.Moved} moved" + (r.Skipped > 0 ? $", {r.Skipped} skipped (not on disk; paths updated)" : "") + (r.Failed > 0 ? $", {r.Failed} failed" : "");
        if (ctx.JsonOut) EmitJson(new
        {
            command = "reorg", outcome = r.Outcome.ToString(), moved = r.Moved, skipped = r.Skipped, failed = r.Failed,
            canceled = r.Canceled, pauseReason = r.PauseReason,
            exitCode = r.Canceled || r.Outcome == Grog.Core.Volumes.ReorgOutcome.Paused ? 1 : r.Failed > 0 ? 8 : 0,
        });
        if (r.Canceled)
            return Fail("reorg", Grog.Core.Cli.ExitCode.Error, "canceled", $"Stopped: {line}. Run 'reorg resume' to finish.");
        if (r.Outcome == Grog.Core.Volumes.ReorgOutcome.Paused)
            return Fail("reorg", Grog.Core.Cli.ExitCode.Error, "paused", $"Paused: {r.PauseReason ?? "interrupted"}. {line}. Run 'reorg resume' once the drive is back.");
        if (r.Failed > 0)
            return Fail("reorg", Grog.Core.Cli.ExitCode.IntegrityFailed, "integrity", $"Finished with problems: {line}. Run 'reorg resume' to retry the failed moves.");
        Out.Success($"Reorganize complete: {line}.");
        return Continue;
    }
}

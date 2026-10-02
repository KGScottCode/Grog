// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
//
// The queue's Paused fact from the command line (pause / resume), the saved scope (scope show / set), and the
// one re-plan every storage slot change ends with -- the same steps the App's Storage page takes.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grog.Core.Manifest;

namespace Grog.Cli;

internal static partial class CliVerbs
{
    /// <summary>`pause`: set the manifest's Paused flag. `backup` refuses to start until `resume` (or --ignore-pause).</summary>
    internal static Task<int> Pause(CliContext ctx) => SetPaused(ctx, true);

    /// <summary>`resume`: clear the manifest's Paused flag. Nothing downloads here; the next `backup` does.</summary>
    internal static Task<int> Resume(CliContext ctx) => SetPaused(ctx, false);

    private static async Task<int> SetPaused(CliContext ctx, bool paused)
    {
        var manifest = ctx.Manifest;
        await manifest.LoadAsync();
        // Same field the App's SetDownloadPaused writes: LibraryManifest.Downloads.Paused, under the gate.
        manifest.Mutate(m => m.Downloads.Paused = paused);
        await manifest.SaveAsync();
        var command = paused ? "pause" : "resume";
        if (ctx.JsonOut) EmitJson(new { command, paused, exitCode = 0 });
        Out.Success(paused ? "Downloads paused. `backup` will not start until `resume`." : "Downloads resumed.");
        return Continue;
    }

    /// <summary>`scope [show]` prints the saved scope; `scope set` writes it (the same manifest field the App's
    /// Settings page writes) and re-plans the queue so files the new scope excludes leave it.</summary>
    internal static async Task<int> Scope(CliContext ctx)
    {
        var args = ctx.Args;
        var manifest = ctx.Manifest;
        await manifest.LoadAsync();
        var sub = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))?.ToLowerInvariant();
        bool Has(string f) => args.Contains(f, StringComparer.OrdinalIgnoreCase);
        static List<string> Split(string? v) =>
            (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(s => s.ToLowerInvariant()).Distinct().ToList();

        switch (sub)
        {
            case null:
            case "show":
                ShowScope(ctx, manifest.Current.Scope);
                return Continue;
            case "set":
            {
                bool anyChange = Has("--os") || Has("--lang") || Has("--installers-only") || Has("--with-extras") || Has("--games") || Has("--no-games");
                if (!anyChange)
                {
                    Out.Usage("Usage: scope set [--os <a[,b]>] [--lang <a[,b]>] [--installers-only|--with-extras] [--games|--no-games]");
                    return (int)Grog.Core.Cli.ExitCode.UsageError;
                }
                if (Has("--installers-only") && Has("--with-extras"))
                    return Fail("scope", Grog.Core.Cli.ExitCode.UsageError, "usage", "--installers-only and --with-extras exclude each other.");
                if (Has("--games") && Has("--no-games"))
                    return Fail("scope", Grog.Core.Cli.ExitCode.UsageError, "usage", "--games and --no-games exclude each other.");
                var os = Has("--os") ? Split(GetArgValue(args, "--os")) : null;
                var lang = Has("--lang") ? Split(GetArgValue(args, "--lang")) : null;
                if (os is not null && os.Any(p => p is not ("windows" or "mac" or "linux")))
                    return Fail("scope", Grog.Core.Cli.ExitCode.UsageError, "usage", "--os takes windows, mac, linux (comma-separated). 'all' clears the filter.");

                // "all" on either axis stores an empty list: no narrowing, matching the App. Core is the one writer.
                Grog.Core.Sync.SavedScope.Set(manifest,
                    includeGames: Has("--games") ? true : Has("--no-games") ? false : null,
                    includeExtras: Has("--with-extras") ? true : Has("--installers-only") ? false : null,
                    languages: lang is null ? null : lang.Contains("all") ? new List<string>() : lang,
                    platforms: os is null ? null : os.Contains("all") ? new List<string>() : os);
                // Queued files the new scope excludes leave the queue (the same Core prune the App runs).
                int pruned = Grog.Core.Sync.SavedScope.PruneQueuedOutOfScope(manifest).Count;
                int changed = ReplanAfterChange(ctx);
                await manifest.SaveAsync();
                if (ctx.JsonOut) EmitJson(new { command = "scope", scope = ScopeDoc(manifest.Current.Scope), pruned, replanned = changed, exitCode = 0 });
                else
                {
                    Out.Success("Saved scope updated." + (pruned > 0 ? $" {pruned} queued file(s) left the queue." : ""));
                    ShowScope(ctx, manifest.Current.Scope);
                }
                return Continue;
            }
            default:
                Out.Usage("Usage: scope [show|set --os <list> --lang <list> [--installers-only|--with-extras] [--games|--no-games]]");
                return (int)Grog.Core.Cli.ExitCode.UsageError;
        }
    }

    private static object ScopeDoc(Grog.Core.Sync.ScopeSettings? s) => s is null
        ? new { saved = false }
        : new { saved = true, games = s.IncludeGames, extras = s.IncludeExtras, os = s.Platforms, lang = s.Languages,
                extraLang = s.ExtraLanguagesChosen ? s.ExtraLanguages : null };

    private static void ShowScope(CliContext ctx, Grog.Core.Sync.ScopeSettings? s)
    {
        if (ctx.JsonOut) { EmitJson(new { command = "scope", scope = ScopeDoc(s), exitCode = 0 }); return; }
        if (s is null) { Out.Info("No saved scope: every file is in scope. Set one with 'scope set' or in the app."); return; }
        static string ListOrAll(List<string>? l) => l is { Count: > 0 } ? string.Join(",", l) : "all";
        Out.Heading("Saved scope (the base filter every backup/download run applies):");
        Out.Info($"  games      {(s.IncludeGames ? "yes" : "no")}");
        Out.Info($"  extras     {(s.IncludeExtras ? "yes" : "no")}");
        Out.Info($"  os         {ListOrAll(s.Platforms)}");
        Out.Info($"  lang       {ListOrAll(s.Languages)}");
        if (s.ExtraLanguagesChosen) Out.Info($"  extra lang {ListOrAll(s.ExtraLanguages)}");
        Out.Dim("Per-run flags (--os, --lang, --installers-only ...) narrow further; --ignore-saved-scope lifts this.");
    }

    /// <summary>The re-plan every slot or scope change ends with (the App's AfterSlotChangeAsync): rebuild the
    /// layout against the roots as they stand, probe the devices, and re-decide every queued file's target and
    /// fit. No engine: the CLI has no live run while a writer verb holds the profile. Returns the changed count.</summary>
    internal static int ReplanAfterChange(CliContext ctx)
    {
        var manifest = ctx.Manifest;
        ctx.ResetLayout();
        var layout = ctx.Layout();
        var spaces = new Grog.Core.Volumes.VolumeService(manifest).DeviceSpaces(layout);
        int changed = Grog.Core.Runs.BackupQueueBuilder.ReplanWholeQueue(manifest, spaces, engine: null);
        if (changed > 0) Out.Dim($"Queue re-planned: {changed} file(s) re-targeted.");
        return changed;
    }
}

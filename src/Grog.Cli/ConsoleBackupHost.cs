// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Linq;
using Grog.Core.Download;
using Grog.Core.Models;
using Grog.Core.Runs;
using Grog.Core.Sync;

namespace Grog.Cli;

/// <summary>
/// The CLI's face on a <see cref="BackupRun"/>: a spinner per account scan, the live per-file block for the
/// download, and dim log lines. Nothing here decides WHAT happens -- selection, placement, settlement, verify
/// and the outcome are Core's; this class only draws them. The App's host does the same with a ViewModel.
/// </summary>
internal sealed class ConsoleBackupHost : NullBackupHost
{
    private readonly bool _scanDebug;
    private readonly bool _scanDetails;
    private readonly string? _failuresDir;
    private ConsoleProgress? _progress;
    private DownloadRenderer? _render;

    public ConsoleBackupHost(bool scanDebug, bool scanDetails, string? failuresDir)
    {
        _scanDebug = scanDebug; _scanDetails = scanDetails; _failuresDir = failuresDir;
    }

    /// <summary>The renderer of the last engine run, for the console summary and --json failure list.</summary>
    public DownloadRenderer? Render => _render;

    /// <summary>--debug: trace the engine's resolve/fetch steps to the console.</summary>
    public bool EngineDebug { get; init; }

    public override void ConfigureScan(LibrarySyncService svc, GrogAccount? account, int index, int count)
    {
        if (_scanDebug) svc.DumpFailuresToDirectory = _failuresDir;
        svc.FetchProductDetails = _scanDetails;
        _progress?.Finish();
        var p = _progress = new ConsoleProgress();
        svc.Progress += x => p.Report(x.Completed, x.Total);
    }

    public void FinishScan() { _progress?.Finish(); _progress = null; }

    public override void EngineReady(DownloadEngine engine)
    {
        FinishScan();
        var snapshot = engine.Snapshot.ToList();
        // Size the name column to the longest known label (name + OS/lang), capped in the renderer.
        int nameCol = 16;
        foreach (var t in snapshot)
        {
            var meta = string.Join(", ", new[] { t.File.Os, t.File.Language }.Where(s => !string.IsNullOrEmpty(s)));
            var lbl = meta.Length > 0 ? $"{t.File.Name} ({meta})" : t.File.Name;
            if (lbl.Length > nameCol) nameCol = lbl.Length;
        }
        Out.Dim($"Downloading {snapshot.Count} file(s), {engine.MaxConcurrent} in parallel. Into {engine.BackupRoot}");
        Out.Dim("Press Ctrl-C to stop; partial files resume next run.\n");
        if (EngineDebug) engine.DebugTrace = msg => Console.WriteLine($"    [dbg] {msg}");
        var r = _render = new DownloadRenderer(snapshot.Count, nameCol);
        engine.TaskChanged += r.Update;
    }

    public override void EngineDone(DownloadEngine engine) => _render?.Finish();

    public override void Log(string message, bool isError = false)
    {
        // A failure mid-scan must not leave a spinner printing "completed" under the error line.
        if (isError) { FinishScan(); Out.Warn(message); } else Out.Dim(message);
    }
}

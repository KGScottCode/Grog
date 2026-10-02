// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using Grog.Core.Download;
using Grog.Core.Models;
using Grog.Core.Sync;

namespace Grog.Core.Runs;

/// <summary>
/// The presentation seams of a backup run: what a window or a console does while <see cref="BackupRun"/>
/// makes the decisions. Nothing here decides anything; a host that ignores every call still gets a correct
/// run (see <see cref="NullBackupHost"/>).
/// </summary>
public interface IBackupHost
{
    /// <summary>Dress a scan service before it runs (progress hooks, art hook). <paramref name="account"/>
    /// is null on a legacy single-session library; index/count number the pass for a title.</summary>
    void ConfigureScan(LibrarySyncService svc, GrogAccount? account, int index, int count);

    /// <summary>Placement is done: the tasks the engine will attempt (each carries its TargetRootId, so a host
    /// can say what lands where) and how many wait for space. Fires before <see cref="EngineReady"/>, and also
    /// when nothing at all fits (then <paramref name="tasks"/> is empty).</summary>
    void Placed(IReadOnlyList<DownloadTask> tasks, int held, long heldBytes);

    /// <summary>The engine exists and its queue is loaded. The App keeps it as the live engine the queue
    /// pane reads and reorders, applies its queue sort and Auto concurrency here; the CLI attaches its
    /// renderer. Called BEFORE the run starts.</summary>
    void EngineReady(DownloadEngine engine);

    /// <summary>The run is over and the engine will not move again. A host that pauses (App) may keep the
    /// reference for its frozen readouts; this is the moment to drop it otherwise.</summary>
    void EngineDone(DownloadEngine engine);

    /// <summary>Worker count for this engine, or null to keep <see cref="BackupRunOptions.FixedConcurrency"/>
    /// / the engine default. A host that sizes from the sorted queue head (the App's Auto advisor) does that
    /// in <see cref="EngineReady"/> instead, after its sort.</summary>
    int? PickConcurrency(DownloadEngine engine);

    /// <summary>One task reached a terminal state and its manifest record has been settled (Core did that).
    /// Hosts tally, log, feed meters. Called on the engine's thread, coalesced or not by the caller.</summary>
    void TaskSettled(DownloadTask task, DownloadSettlement.Outcome outcome);

    void Log(string message, bool isError = false);
}

/// <summary>A host that does nothing: tests, and the baseline every real host extends.</summary>
public class NullBackupHost : IBackupHost
{
    public virtual void ConfigureScan(LibrarySyncService svc, GrogAccount? account, int index, int count) { }
    public virtual void Placed(IReadOnlyList<DownloadTask> tasks, int held, long heldBytes) { }
    public virtual void EngineReady(DownloadEngine engine) { }
    public virtual void EngineDone(DownloadEngine engine) { }
    public virtual int? PickConcurrency(DownloadEngine engine) => null;
    public virtual void TaskSettled(DownloadTask task, DownloadSettlement.Outcome outcome) { }
    public virtual void Log(string message, bool isError = false) { }
}

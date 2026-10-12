// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using Grog.Core.Download;
using Grog.Core.Models;
using Grog.Core.Sync;

namespace Grog.Core.Runs;

public interface IBackupHost
{
    void ConfigureScan(LibrarySyncService svc, GrogAccount? account, int index, int count);

    void Placed(IReadOnlyList<DownloadTask> tasks, int held, long heldBytes);

    void EngineReady(DownloadEngine engine);

    void EngineDone(DownloadEngine engine);

    int? PickConcurrency(DownloadEngine engine);

    void TaskSettled(DownloadTask task, DownloadSettlement.Outcome outcome);

    void Log(string message, bool isError = false);
}

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

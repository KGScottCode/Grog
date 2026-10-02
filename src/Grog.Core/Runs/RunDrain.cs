// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Linq;
using Grog.Core.Download;

namespace Grog.Core.Runs;

/// <summary>Where "Finish current files" stands for the Back-up run.</summary>
public enum DrainState
{
    /// <summary>Nothing asked: the run works the queue.</summary>
    None,
    /// <summary>The files transferring finish; nothing new starts.</summary>
    Finishing,
    /// <summary>The finish ran to its end with files still queued: the queue is paused for that reason.</summary>
    PausedAfterFinish,
}

/// <summary>(09-19, one state since 09-22) "Finish current files": the clean stop. It applies to the Back-up run in
/// flight only (the per-file executor never drains); a request made before that run's engine exists is applied
/// the moment it does. One state instead of the seven flags it grew from; the host only reads it.</summary>
public sealed class RunDrain
{
    private readonly object _gate = new();
    private DownloadEngine? _engine;
    private bool _runLive;
    // (09-25, owner) "Finish current files" from a pause: armed before the Resume starts its run, applied to that run.
    private System.Collections.Generic.IReadOnlySet<(long, string)>? _armedOnly, _only;

    /// <summary>Arm a finish of ONLY these files (the ones in progress) for the run the next Resume starts.</summary>
    public void ArmFinishOnly(System.Collections.Generic.IReadOnlySet<(long, string)> files) { lock (_gate) _armedOnly = files; }
    /// <summary>Drop an arm no run consumed.</summary>
    public void Disarm() { lock (_gate) _armedOnly = null; }

    public DrainState State { get; private set; }
    public bool Finishing => State == DrainState.Finishing;
    public bool PausedAfterFinish => State == DrainState.PausedAfterFinish;

    /// <summary>A Back-up run is live and not already finishing: the request can be made.</summary>
    public bool CanRequest { get { lock (_gate) return _runLive && State != DrainState.Finishing; } }

    /// <summary>Files transferring right now (what "Finishing N files" counts).</summary>
    public int InFlight { get { var e = _engine; return e?.Snapshot.Count(t => t.State == DownloadTaskState.Active) ?? 0; } }

    /// <summary>Ask the live run to start nothing new. False when there is no live run or it is already finishing.</summary>
    public bool Request()
    {
        lock (_gate)
        {
            if (!_runLive || State == DrainState.Finishing) return false;
            State = DrainState.Finishing;
            if (_engine is { } e) e.StartNothingNew = true;
            return true;
        }
    }

    /// <summary>(09-22, owner) "Keep downloading": take the request back while the run is live. Before the engine
    /// exists it is just forgotten; after, the engine starts taking files again. False = too late (the run ended).</summary>
    public bool Cancel()
    {
        lock (_gate)
        {
            if (State != DrainState.Finishing || !_runLive) return false;
            if (_engine is { } e)
            {
                e.OnlyStart = null;   // a finish from a pause: the whole queue is open again
                if (!e.StartNewAgain()) return false;
            }
            _only = null;
            State = DrainState.None;
            return true;
        }
    }

    /// <summary>The run's engine now exists: a request made in the pre-engine window applies to it.</summary>
    public void EngineReady(DownloadEngine engine)
    {
        lock (_gate)
        {
            _engine = engine;
            if (State != DrainState.Finishing) return;
            if (_only is { } only) engine.OnlyStart = only;   // from a pause: the files in progress may still start
            else engine.StartNothingNew = true;
        }
    }

    /// <summary>A Back-up run starts: whatever was asked of the last one is over.</summary>
    public void RunBegan()
    {
        lock (_gate)
        {
            _runLive = true; _engine = null;
            _only = _armedOnly; _armedOnly = null;
            State = _only is null ? DrainState.None : DrainState.Finishing;
        }
    }

    /// <summary>The run is over. PausedAfterFinish stays (the reason line reads it); a finish that did not end in a
    /// pause (nothing to do, an error, the queue emptied) is over with its run.</summary>
    public void RunOver() { lock (_gate) { _runLive = false; _engine = null; _only = null; _armedOnly = null; if (State == DrainState.Finishing) State = DrainState.None; } }

    /// <summary>The finish ran to its end with files still queued: the queue pauses for that reason.</summary>
    public void EndedPaused() { lock (_gate) State = DrainState.PausedAfterFinish; }

    /// <summary>Forget the request (resumed, queue empty, or the user paused over it).</summary>
    public void Reset() { lock (_gate) State = DrainState.None; }
}

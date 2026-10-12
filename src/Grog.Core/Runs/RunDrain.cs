// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Linq;
using Grog.Core.Download;

namespace Grog.Core.Runs;

public enum DrainState
{
    None,
    Finishing,
    PausedAfterFinish,
}

public sealed class RunDrain
{
    private readonly object _gate = new();
    private DownloadEngine? _engine;
    private bool _runLive;

    private System.Collections.Generic.IReadOnlySet<(long, string)>? _armedOnly, _only;

    public void ArmFinishOnly(System.Collections.Generic.IReadOnlySet<(long, string)> files) { lock (_gate) _armedOnly = files; }
    public void Disarm() { lock (_gate) _armedOnly = null; }

    public DrainState State { get; private set; }
    public bool Finishing => State == DrainState.Finishing;
    public bool PausedAfterFinish => State == DrainState.PausedAfterFinish;

    public bool CanRequest { get { lock (_gate) return _runLive && State != DrainState.Finishing; } }

    public int InFlight { get { var e = _engine; return e?.Snapshot.Count(t => t.State == DownloadTaskState.Active) ?? 0; } }

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

    public bool Cancel()
    {
        lock (_gate)
        {
            if (State != DrainState.Finishing || !_runLive) return false;
            if (_engine is { } e)
            {
                e.OnlyStart = null;   // a finish from a pause
                if (!e.StartNewAgain()) return false;
            }
            _only = null;
            State = DrainState.None;
            return true;
        }
    }

    public void EngineReady(DownloadEngine engine)
    {
        lock (_gate)
        {
            _engine = engine;
            if (State != DrainState.Finishing) return;
            if (_only is { } only) engine.OnlyStart = only;   // from a pause
            else engine.StartNothingNew = true;
        }
    }

    public void RunBegan()
    {
        lock (_gate)
        {
            _runLive = true; _engine = null;
            _only = _armedOnly; _armedOnly = null;
            State = _only is null ? DrainState.None : DrainState.Finishing;
        }
    }

    public void RunOver() { lock (_gate) { _runLive = false; _engine = null; _only = null; _armedOnly = null; if (State == DrainState.Finishing) State = DrainState.None; } }

    public void EndedPaused() { lock (_gate) State = DrainState.PausedAfterFinish; }

    public void Reset() { lock (_gate) State = DrainState.None; }
}

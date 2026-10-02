// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later

namespace Grog.Core.Volumes;

/// <summary>Cooperative pause/cancel, checked between files (never mid-file, so state stays consistent).
/// Pause leaves the journal in place to resume later; cancel stops and leaves the job on disk marked
/// Canceled, so the user can still revert the moves already made or knowingly accept them as orphans.</summary>
public sealed class ReorgControl
{
    private volatile bool _paused;
    private volatile bool _canceled;
    public void Pause() => _paused = true;
    public void Resume() => _paused = false;
    public void Cancel() => _canceled = true;
    public bool IsPaused => _paused;
    public bool IsCanceled => _canceled;
}

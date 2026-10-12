// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later

namespace Grog.Core.Volumes;

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

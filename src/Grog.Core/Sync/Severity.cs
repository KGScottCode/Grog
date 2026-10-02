// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Sync;

/// <summary>
/// HOW BAD IS IT, decided ONCE: every surface that colors a status uses this shared grading, never a
/// hand-rolled copy. Severity is a property of the FINDING, not the pane drawing it; the App maps a
/// severity to a brush in exactly one place.
/// </summary>
public enum Severity
{
    /// <summary>Nothing to say: passing, or not yet knowable. Green is the assumed state and is never
    /// reported, and "we have not looked" is not a finding.</summary>
    Quiet,
    /// <summary>Work outstanding. Nothing is broken and nothing was lost; there is something left to do.</summary>
    Attention,
    /// <summary>A real fault: files gone from disk, or a folder that should be present and isn't.</summary>
    Fault,
}


// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core;

/// <summary>Process-wide state the test runner clears between test classes, so no class inherits another's.
/// Internal: the suite reaches it through InternalsVisibleTo; the app never calls it.</summary>
internal static class TestHooks
{
    /// <summary>Every process-static cache in Core back to its start-of-process state.</summary>
    internal static void ResetProcessState()
    {
        Platform.OsKeyring.ResetAvailabilityCache();
        Volumes.DriveWaits.Clear();   // process-static by design (see DriveWaits); a class that marked a root must not leak it
    }
}

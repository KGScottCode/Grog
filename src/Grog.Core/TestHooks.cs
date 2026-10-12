// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core;

internal static class TestHooks
{
    internal static void ResetProcessState()
    {
        Platform.OsKeyring.ResetAvailabilityCache();
        Volumes.DriveWaits.Clear();   // process-static by design (see DriveWaits); a class that marked a root must not leak it
    }
}

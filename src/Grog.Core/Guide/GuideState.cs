// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;

namespace Grog.Core.Guide;

/// <summary>
/// What the guide is allowed to know about the app. Deliberately small: the guide READS state to decide
/// when a stop's work is finished, and never owns it.
///
/// <para>Note what this is NOT used for any more: computing which stop the user is on. That is the cursor's
/// job. State only answers "is this stop's work done?", which lets the cursor skip ahead when the user does
/// something out of order, without ever pulling the cursor backwards.</para>
/// </summary>
public readonly record struct GuideState(
    bool HasAccount,
    bool HasLibrary,
    bool HasBackedUpAnything,
    string CurrentView,
    bool IsScanning = false,
    bool RunStarted = false,
    bool ShowingItemCount = false,
    // ScanRan: a scan actually ran during this tour. Tells "the summary has not appeared YET" apart from
    // "there will never be a summary", which is the difference between holding the tour and trapping it.
    // ItemCountSeen: the scan summary has been on screen at least once this tour.
    bool ScanRan = false,
    bool ItemCountSeen = false,
    IReadOnlySet<string>? Acked = null,
    // The demo file the back-up stop points at is on disk. Distinct from HasBackedUpAnything: a library that
    // already holds files from earlier runs must not make the stop claim "that file is backed up" while the
    // freshly picked file still shows its download arrow (owner 09-02).
    bool FirstFileBackedUp = false,
    // The Cloud Saves page has rows, so its Download-all button (the cloud stop's usual target) is on screen.
    bool HasCloudSaves = false,
    // A demo file exists for the back-up stop: something in scope, not on disk, and not already in flight.
    // Without one the stop has nothing to point at and is skipped, not shown with a ring around nothing.
    bool HasDemoFile = false)
{
    /// <summary>Has the user dismissed this stop with Next? For an Optional stop the acknowledgement IS the
    /// completion fact, because its work may never happen on its own.</summary>
    public bool Ack(string key) => Acked is not null && Acked.Contains(key);
}

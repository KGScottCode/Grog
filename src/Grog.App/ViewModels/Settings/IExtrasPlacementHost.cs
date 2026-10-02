// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using Grog.Core.Runs;

namespace Grog.App.ViewModels;

/// <summary>
/// What <see cref="ExtrasPlacementEditor"/> needs from the shell, and nothing else (09-11).
///
/// The editor took the concrete MainWindowViewModel, which made the one card that carries a real bug fix
/// the one card that could not be tested: constructing the root view model means constructing the app.
/// This is the third host interface in the codebase for exactly that reason (see IScheduleHost, IGuideHost)
/// and it follows their shape - the shell implements it, a test fakes it, and the surface stays small
/// enough that widening it is a visible decision rather than a drift.
/// </summary>
public interface IExtrasPlacementHost
{
    /// <summary>The manifest store, or null before one is open.</summary>
    Grog.Core.Manifest.JsonManifestStore? Store { get; }

    /// <summary>The primary backup root on disk. Empty when no location is configured.</summary>
    string BackupRoot { get; }

    /// <summary>Cancellation for the session's background work; null outside a session.</summary>
    System.Threading.CancellationToken SessionToken { get; }

    /// <summary>Every configured backup location, primary first. Only the id and label are used: the card
    /// asks "is there a second place" and "what is it called".</summary>
    IReadOnlyList<(string? RootId, string? Label)> Locations { get; }

    /// <summary>Extras are in the backup scope, which is what shows the card at all. Out of scope the card is
    /// hidden, and a hidden card must hold no staging (sweep 2 #29).</summary>
    bool ExtrasInScope { get; }

    /// <summary>True while the Storage page is holding an unanswered move question.</summary>
    bool MigratePromptOpen { get; }

    /// <summary>Re-raise the folder preview, which reads this card's STAGED values. Called on every
    /// staged change, so it must not touch the disk.</summary>
    void RaiseFolderPreview();

    /// <summary>Commit landed: re-derive the settings page's policy state and re-read the locations.</summary>
    void AfterCommit();

    /// <summary>Hand the planned move job to whoever owns the move prompt.</summary>
    void OfferPlan(LayoutMigrationPlan plan);

    /// <summary>Navigate, bypassing the leave guard (the guard is what asked).</summary>
    void LeaveTo(string? view);

    void Log(string message, bool isError = false);
}

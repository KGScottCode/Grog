// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using Grog.Core.Runs;

namespace Grog.App.ViewModels;

public interface IExtrasPlacementHost
{
    Grog.Core.Manifest.JsonManifestStore? Store { get; }

    string BackupRoot { get; }

    System.Threading.CancellationToken SessionToken { get; }

    IReadOnlyList<(string? RootId, string? Label)> Locations { get; }

    bool ExtrasInScope { get; }

    bool MigratePromptOpen { get; }

    void RaiseFolderPreview();

    void AfterCommit();

    void OfferPlan(LayoutMigrationPlan plan);

    void LeaveTo(string? view);

    void Log(string message, bool isError = false);
}

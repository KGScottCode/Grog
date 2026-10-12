// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Sync;

public sealed class ChangesService
{
    private readonly IManifestStore _manifest;

    public ChangesService(IManifestStore manifest) => _manifest = manifest;

    public sealed record ChangeRow(long GogId, string Title, string Reason);

    public IReadOnlyList<ChangeRow> UpdatesAvailable()
        => _manifest.Current.Items
            .Where(i => i.Status == BackupStatus.Outdated
                        || i.Files.Any(f => f.State == FileState.Outdated))
            .OrderBy(i => i.Title)
            .Select(i => new ChangeRow(i.GogId, i.Title, "update available"))
            .ToList();

    public IReadOnlyList<ChangeRow> MissingOrIncomplete()
        => _manifest.Current.Items
            .Where(i => i.Status is BackupStatus.NotBackedUp or BackupStatus.Partial or BackupStatus.Corrupt)
            .OrderBy(i => i.Title)
            .Select(i => new ChangeRow(i.GogId, i.Title, i.Status switch
            {
                BackupStatus.NotBackedUp => "nothing backed up",
                BackupStatus.Partial => "partial backup",
                BackupStatus.Corrupt => "corrupt/missing files",
                _ => "incomplete",
            }))
            .ToList();

    public string OneLineSummary()
    {
        int updated = UpdatesAvailable().Count;
        int incomplete = MissingOrIncomplete().Count;
        int delisted = _manifest.Current.Items.Count(i => i.IsDelisted);
        return $"{updated} with updates, {incomplete} incomplete, {delisted} delisted";
    }
}

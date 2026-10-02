// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Sync;

/// <summary>
/// Surfaces "what changed" from state the engine already computes. Two views: updates available (what a
/// `download --updates` run would fetch) and missing/incomplete (not fully backed up). Presentation only;
/// no read/unread state.
/// </summary>
public sealed class ChangesService
{
    private readonly IManifestStore _manifest;

    public ChangesService(IManifestStore manifest) => _manifest = manifest;

    public sealed record ChangeRow(long GogId, string Title, string Reason);

    /// <summary>Games with an update available (complete-but-outdated, or any file flagged newer).</summary>
    public IReadOnlyList<ChangeRow> UpdatesAvailable()
        => _manifest.Current.Items
            .Where(i => i.Status == BackupStatus.Outdated
                        || i.Files.Any(f => f.State == FileState.Outdated))
            .OrderBy(i => i.Title)
            .Select(i => new ChangeRow(i.GogId, i.Title, "update available"))
            .ToList();

    /// <summary>Games not fully backed up -- the "what don't I have" view the user most wants.</summary>
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

    /// <summary>A one-line summary suitable for the end of a sync run:
    /// "12 updated, 3 incomplete, 0 delisted".</summary>
    public string OneLineSummary()
    {
        int updated = UpdatesAvailable().Count;
        int incomplete = MissingOrIncomplete().Count;
        int delisted = _manifest.Current.Items.Count(i => i.IsDelisted);
        return $"{updated} with updates, {incomplete} incomplete, {delisted} delisted";
    }
}

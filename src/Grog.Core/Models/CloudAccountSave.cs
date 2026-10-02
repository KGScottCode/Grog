// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Serialization;

namespace Grog.Core.Models;

/// <summary>One account's cloud-save container for one game (server-side facts from GOG's v2
/// containers list, keyed by the owning account). Saves are per-account state and never merge.</summary>
public sealed class CloudAccountSave
{
    public string AccountId { get; set; } = "";
    public long SizeBytes { get; set; }
    public int Files { get; set; }
    public DateTimeOffset? UpdatedUtc { get; set; }
    public string SpaceId { get; set; } = "";

    // Per-account local backup bookkeeping; each account archives in its own folder (the per-game folder
    // for the tokens.json slot, accounts/<id>/ for the rest). The per-item fields mirror the first entry.
    public DateTimeOffset? LastBackup { get; set; }
    public int LocalSaveCount { get; set; }
    public long BackedUpSize { get; set; }
    public int BackedUpFiles { get; set; }
    public DateTimeOffset? BackedUpChangeUtc { get; set; }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Serialization;

namespace Grog.Core.Models;

public sealed class CloudAccountSave
{
    public string AccountId { get; set; } = "";
    public long SizeBytes { get; set; }
    public int Files { get; set; }
    public DateTimeOffset? UpdatedUtc { get; set; }
    public string SpaceId { get; set; } = "";

    public DateTimeOffset? LastBackup { get; set; }
    public int LocalSaveCount { get; set; }
    public long BackedUpSize { get; set; }
    public int BackedUpFiles { get; set; }
    public DateTimeOffset? BackedUpChangeUtc { get; set; }
}

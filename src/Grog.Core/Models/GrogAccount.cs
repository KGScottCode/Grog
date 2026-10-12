// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Serialization;

namespace Grog.Core.Models;

public sealed class GrogAccount
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string Login { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSync { get; set; }
    public string CloudSavesFolder { get; set; } = "";
}

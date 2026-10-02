// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Serialization;

namespace Grog.Core.Models;

/// <summary>A GOG account Grog syncs from. Multi-account merges all accounts' catalogs into one
/// library; each account keeps its own token store. Volumes/policies/scope stay global.</summary>
public sealed class GrogAccount
{
    /// <summary>Stable local id for this account (used to tag items and name the token file).</summary>
    public string Id { get; set; } = "";
    /// <summary>GOG username, for display.</summary>
    public string Username { get; set; } = "";
    /// <summary>GOG account email/login, for display.</summary>
    public string Login { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSync { get; set; }
    /// <summary>Per-account cloud-saves folder override. Empty = the default: &lt;cloud base&gt;/&lt;account
    /// name&gt;. Set, it fully replaces that account's area -- its game archives live directly inside.</summary>
    public string CloudSavesFolder { get; set; } = "";
}

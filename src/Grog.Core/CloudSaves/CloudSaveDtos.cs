// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;

namespace Grog.Core.CloudSaves;

/// <summary>A game that has cloud saves, with the Galaxy clientId its storage is keyed by.</summary>
public sealed record CloudGame(long GogId, string Title, string Slug, string ClientId);

/// <summary>One save file as GOG's per-game listing reports it.</summary>
public sealed record CloudSaveFile(string Name, long SizeBytes, DateTimeOffset? Modified, string DownloadUrl);

/// <summary>A game's cloud-storage clientId plus an access token SCOPED to that game -- what the
/// per-game endpoint actually accepts (the plain session token gets 403).</summary>
public sealed record GameCloudAuth(string ClientId, string AccessToken);

/// <summary>Diagnostic for the empty-list-saves case: GETs the v1 endpoint with both the build-meta clientId
/// and the discovery space_id, reporting raw HTTP status + parsed count for each key.</summary>
public sealed record SaveProbe(string Key, string Url, int Status, int Count);

/// <summary>One entry of GOG's master containers list (GET cloudstorage.gog.com/v2/users/{userId}/containers):
/// every game with cloud saves, by product id, with size/file-count/space_id. Main session Bearer token only.</summary>
public sealed record CloudContainer(long ProductId, long SizeBytes, int Files, string SpaceId, long QuotaBytes,
    DateTimeOffset? UpdatedUtc = null);

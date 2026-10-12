// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;

namespace Grog.Core.CloudSaves;

public sealed record CloudGame(long GogId, string Title, string Slug, string ClientId);

public sealed record CloudSaveFile(string Name, long SizeBytes, DateTimeOffset? Modified, string DownloadUrl);

public sealed record GameCloudAuth(string ClientId, string AccessToken);

public sealed record SaveProbe(string Key, string Url, int Status, int Count);

public sealed record CloudContainer(long ProductId, long SizeBytes, int Files, string SpaceId, long QuotaBytes,
    DateTimeOffset? UpdatedUtc = null);

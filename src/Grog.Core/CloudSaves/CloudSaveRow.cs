// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.CloudSaves;

/// <summary>A cloud-save game as shown to the user: title + cached snapshot bookkeeping.</summary>
public sealed record CloudSaveRow(long GogId, string Title, string ClientId, long SizeBytes,
    DateTimeOffset? LastBackup, int SnapshotCount, long BackedUpSize,
    int Files = 0, DateTimeOffset? LatestChangeUtc = null, int BackedUpFiles = 0, DateTimeOffset? BackedUpChangeUtc = null);

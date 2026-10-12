// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Api;

public sealed record ResolvedDownload(string FinalUrl, string FileName, long? ContentLength, string? ChecksumXmlUrl);

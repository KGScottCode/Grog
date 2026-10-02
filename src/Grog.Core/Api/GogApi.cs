// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
//
// Endpoint knowledge is drawn from the community "Unofficial GOG API" documentation
// (https://gogapidocs.readthedocs.io) -- endpoints and JSON shapes are facts of the
// service; all code here is original to Grog.
namespace Grog.Core.Api;

/// <summary>Where a download actually resolved to: GOG hands out a redirect, and this is what it landed on --
/// the final URL, the real filename, its size, and the sidecar checksum XML when one exists.</summary>
public sealed record ResolvedDownload(string FinalUrl, string FileName, long? ContentLength, string? ChecksumXmlUrl);

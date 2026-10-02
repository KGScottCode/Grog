// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Models;

/// <summary>
/// THE platform vocabulary. GOG's file records carry lowercase tokens ("windows", "mac"/"osx", "linux");
/// every surface that shows one goes through here, so a scope label, a chip and a badge can never drift
/// ("Mac" beside "macOS" was live until 09-02).
/// </summary>
public static class PlatformNames
{
    /// <summary>Proper name: "Windows" / "macOS" / "Linux"; an unknown token is returned as given.</summary>
    public static string Label(string? key) => key?.ToLowerInvariant() switch
    {
        "windows"                 => "Windows",
        "mac" or "osx" or "macos" => "macOS",
        "linux"                   => "Linux",
        _                         => key ?? "",
    };

    /// <summary>Badge form for a tight chip: "WIN" / "MAC" / "LINUX"; empty when unknown.</summary>
    public static string Badge(string? key) => key?.ToLowerInvariant() switch
    {
        "windows"                 => "WIN",
        "mac" or "osx" or "macos" => "MAC",
        "linux"                   => "LINUX",
        _                         => "",
    };
}

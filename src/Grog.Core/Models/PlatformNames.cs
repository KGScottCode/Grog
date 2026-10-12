// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Models;

public static class PlatformNames
{
    public static string Label(string? key) => key?.ToLowerInvariant() switch
    {
        "windows"                 => "Windows",
        "mac" or "osx" or "macos" => "macOS",
        "linux"                   => "Linux",
        _                         => key ?? "",
    };

    public static string Badge(string? key) => key?.ToLowerInvariant() switch
    {
        "windows"                 => "WIN",
        "mac" or "osx" or "macos" => "MAC",
        "linux"                   => "LINUX",
        _                         => "",
    };
}

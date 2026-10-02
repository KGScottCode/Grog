// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;

namespace Grog.Core.Cli;

/// <summary>Resolves a `backup` run's settings in tier order: flag, then saved config, then declared default,
/// reporting each value with its source. The backup location is never defaulted: absent means "not configured"
/// and the caller must fail loud rather than guess a drive.</summary>
public static class BackupConfigResolver
{
    /// <summary>One resolved setting and its provenance (flag / config / default).</summary>
    public sealed record Setting(string Name, string Value, string Source);

    public sealed record BackupPlan(bool Configured, string? NotConfiguredReason, IReadOnlyList<Setting> Settings);

    public static BackupPlan Resolve(
        string? locationFromFlag,
        string? configuredLocation,
        bool? includeExtrasFlag,
        Models.ExtrasPlacement configuredExtrasLayout,
        int? concurrencyFlag,
        bool? verifyFlag)
    {
        var settings = new List<Setting>();

        // Location: flag > saved; NEVER defaulted. Absent -> not configured.
        var location = locationFromFlag ?? configuredLocation;
        bool configured = !string.IsNullOrWhiteSpace(location);
        settings.Add(new Setting("folder", location ?? "(not set)",
            locationFromFlag is not null ? "flag" : configuredLocation is not null ? "config" : "unset"));

        bool includeExtras = includeExtrasFlag ?? true;   // declared default: back up everything you own
        settings.Add(new Setting("scope", includeExtras ? "games + extras" : "games only",
            includeExtrasFlag is not null ? "flag" : "default"));

        // Layout comes from saved setup (the manifest); the default there is extras with each game.
        settings.Add(new Setting("layout", configuredExtrasLayout switch
        {
            Models.ExtrasPlacement.WithGame => "extras with each game",
            Models.ExtrasPlacement.SeparateByGame => "separate Extras folder, by game",
            _ => "separate Extras folder, by type",
        }, "config"));

        int concurrency = concurrencyFlag ?? 3;   // declared default
        settings.Add(new Setting("concurrency", concurrency.ToString(),
            concurrencyFlag is not null ? "flag" : "default"));

        bool verify = verifyFlag ?? true;   // declared default: verify after download
        settings.Add(new Setting("verify-after-download", verify ? "on" : "off",
            verifyFlag is not null ? "flag" : "default"));

        return new BackupPlan(
            configured,
            configured ? null : "No backup folder is set. Pass --primary=<path> (Grog never guesses a location).",
            settings);
    }
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Volumes;

namespace Grog.Core.Cli;

/// <summary>Turns the CLI's declarative device arguments (<c>--primary</c>/<c>--secondary</c>) into manifest
/// state. Contract: stated paths are made so (create/adopt/promote); an omitted argument never removes
/// anything; an ambiguous primary fails fast rather than guess (<c>--moved</c> re-points a moved drive).
/// Mutates the in-memory manifest; the caller saves.</summary>
public static class DeviceReconciler
{
    /// <param name="Ok">False -> the caller prints <see cref="Error"/> to stderr and exits non-zero
    /// (usage error) without running the backup.</param>
    public sealed record Result(bool Ok, string? Error, IReadOnlyList<string> Notes);

    public static Result Reconcile(JsonManifestStore manifest, string? primaryPath, string? secondaryPath, bool moved)
    {
        var notes = new List<string>();
        var vol = new VolumeService(manifest);
        var m = manifest.Current;

        // ---------- PRIMARY ----------
        if (!string.IsNullOrWhiteSpace(primaryPath))
        {
            string full;
            try { full = Path.GetFullPath(primaryPath); }
            catch (Exception ex) { return new Result(false, $"--primary path is invalid: {ex.Message}", notes); }

            var atPath = m.Roots.FirstOrDefault(r => SamePath(r.PathHint, full));
            var currentPrimary = m.PrimaryRootId is { } pid ? m.Roots.FirstOrDefault(r => r.Id == pid) : null;

            if (atPath is not null)
            {
                // A device already lives here -- just make sure it's the primary.
                if (m.PrimaryRootId != atPath.Id)
                {
                    vol.SetPrimary(atPath.Id);
                    notes.Add($"Primary set to the existing device at {full}.");
                }
            }
            else if (currentPrimary is null)
            {
                // Fresh: no primary yet -> create it here. Unambiguous (nothing to conflict with).
                try
                {
                    var r = vol.AddRoot(full);
                    vol.SetPrimary(r.Id);
                    notes.Add($"Primary device created at {full}.");
                }
                catch (Exception ex) { return new Result(false, ex.Message, notes); }
            }
            else if (moved)
            {
                // User asserts the primary drive moved (e.g. drive letter changed): re-point in place so
                // its files stay valid (same device Id, new path).
                try
                {
                    vol.ReplaceRoot(currentPrimary.Id, full);
                    notes.Add($"Primary re-pointed from {currentPrimary.PathHint} to {full} (moved).");
                }
                catch (Exception ex) { return new Result(false, ex.Message, notes); }
            }
            else
            {
                // Ambiguous -- refuse to guess.
                return new Result(false,
                    $"--primary={full} isn't the current primary ({currentPrimary.PathHint}), and no device is "
                    + "registered there. If that drive MOVED, re-run with --moved to re-point it in place. To make "
                    + "a different device the primary, add it and run `root set-primary`.", notes);
            }
        }

        // ---------- SECONDARY (additive / adopt; never removes) ----------
        if (!string.IsNullOrWhiteSpace(secondaryPath))
        {
            string full;
            try { full = Path.GetFullPath(secondaryPath); }
            catch (Exception ex) { return new Result(false, $"--secondary path is invalid: {ex.Message}", notes); }

            if (SamePath(GetPrimaryPath(m), full))
                return new Result(false, "--secondary is the same folder as --primary. Use two different folders.", notes);

            var atPath = m.Roots.FirstOrDefault(r => SamePath(r.PathHint, full));
            if (atPath is null)
            {
                try
                {
                    vol.AddRoot(full);   // adopt as a secondary device
                    notes.Add($"Secondary device added at {full}.");
                }
                catch (Exception ex) { return new Result(false, ex.Message, notes); }
            }
            // else: already a known device -- leave its role as-is (declarative, non-destructive).
        }

        return new Result(true, null, notes);
    }

    private static string? GetPrimaryPath(LibraryManifest m)
        => m.PrimaryRootId is { } pid ? m.Roots.FirstOrDefault(r => r.Id == pid)?.PathHint : null;

    private static bool SamePath(string? a, string b)
    {
        if (string.IsNullOrEmpty(a)) return false;
        try { return string.Equals(Path.GetFullPath(a), b, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}

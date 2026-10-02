// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Grog.Core.Storage;

/// <summary>Produces the first-run candidate backup locations, each with free space. Kinds mirror the
/// onboarding radios: Documents (default), BesideExe (portable), OtherDrive (largest non-system), Custom.</summary>
public static class BackupLocationSuggester
{
    public const string FolderName = "GOG_Library";

    /// <summary>Auto-applied first-run default root holding Games, Extras and Cloud Saves.
    /// PORTABLE first: with a Data folder beside the exe, the default lands INSIDE the install folder, so
    /// the app, its state and its backups move as one thing -- pointing a portable copy at Documents would
    /// leave the library behind on every machine it visits. Otherwise Windows uses
    /// Documents\My Games\GOG_Library (composed under MyDocuments; "My Games" has no SpecialFolder);
    /// elsewhere the folder lands directly in Documents.</summary>
    public static string DefaultRoot()
    {
        if (GrogPaths.PortableInstallDir() is { } install) return Path.Combine(install, FolderName);
        return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", FolderName)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), FolderName);
    }

    public enum Kind { Documents, BesideExe, OtherDrive, Custom }

    public sealed record Option(Kind Kind, string Label, string Path, long? FreeBytes, bool IsDefault)
    {
    }

    /// <summary>Builds the ordered option list; Documents is default unless a much larger secondary drive exists.</summary>
    public static IReadOnlyList<Option> Suggest()
    {
        var list = new List<Option>();

        // 1) Documents\My Games\GOG_Library (the auto-applied default)
        var docs = DefaultRoot();
        list.Add(new Option(Kind.Documents, "Documents folder", docs, FreeFor(docs), IsDefault: false));

        // 2) Beside the exe (portable)
        var beside = Path.Combine(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), FolderName);
        list.Add(new Option(Kind.BesideExe, "Next to the Grog app", beside, FreeFor(beside), IsDefault: false));

        // 3) Largest non-system drive, if any
        var other = LargestSecondaryDrive();
        if (other is not null)
        {
            var otherPath = Path.Combine(other.RootDirectory.FullName, FolderName);
            list.Add(new Option(Kind.OtherDrive,
                $"{other.Name.TrimEnd('\\')} drive", otherPath, other.AvailableFreeSpace, IsDefault: false));
        }

        // 4) Custom (path filled in by the UI)
        list.Add(new Option(Kind.Custom, "Choose a folder…", "", null, IsDefault: false));

        // Default: Documents, unless a secondary drive has meaningfully more room (>=2x and >=256 GB).
        var docsOpt = list[0];
        var otherOpt = list.FirstOrDefault(o => o.Kind == Kind.OtherDrive);
        int defaultIdx = 0;
        if (otherOpt is not null && docsOpt.FreeBytes is { } df && otherOpt.FreeBytes is { } of
            && of >= df * 2 && of >= 256L * 1024 * 1024 * 1024)
            defaultIdx = list.IndexOf(otherOpt);

        for (int i = 0; i < list.Count; i++)
            if (i == defaultIdx) list[i] = list[i] with { IsDefault = true };

        return list;
    }

    private static DriveInfo? LargestSecondaryDrive()
    {
        try
        {
            // DriveType is reliable on Windows (keep the Fixed-only rule there); on Linux it isn't
            // (/boot/efi reports Removable), so fall back to real-volume detection by filesystem format.
            var systemRoot = DriveResolver.MountRootOf(Environment.GetFolderPath(Environment.SpecialFolder.System))
                             ?? DriveResolver.MountRootOf(AppContext.BaseDirectory);
            return DriveInfo.GetDrives()
                .Where(DriveResolver.IsRealVolume)
                .Where(d => !OperatingSystem.IsWindows() || d.DriveType == DriveType.Fixed)
                .Where(d => !string.Equals(d.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.AvailableFreeSpace)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static long? FreeFor(string path) => DriveResolver.FreeBytes(path);
}

// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
// Row and support types the views bind to; shares the MainWindowViewModel namespace on purpose.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Models;
using Grog.Core.Download;
using Grog.Core.Sync;
using Grog.Core.Format;

namespace Grog.App.ViewModels;

/// <summary>Brush arithmetic shared by the row types: a palette brush at a given alpha.</summary>
internal static class RowBrushes
{
    public static IBrush Translucent(IBrush b, byte alpha)
    {
        var c = ((b as ISolidColorBrush)?.Color) ?? Colors.Gray;
        return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
    }
}

public static class SeverityBrush
{
    /// <summary>THE ONLY PLACE a severity becomes a color. Add a surface, call this; never re-derive.</summary>
    public static IBrush For(Grog.Core.Sync.Severity s) => s switch
    {
        Grog.Core.Sync.Severity.Fault     => Palette.ErrorRed,
        Grog.Core.Sync.Severity.Attention => Palette.AccentAmber,
        _                                            => Palette.InkPrimary,
    };

    /// <summary>The log's severities through the same single mapping. Info draws DIM rather than primary:
    /// a log line at rest is chrome, not a value the user is reading.</summary>
    public static IBrush For(LogSeverity s) => s switch
    {
        LogSeverity.Error => For(Grog.Core.Sync.Severity.Fault),
        LogSeverity.Warn  => For(Grog.Core.Sync.Severity.Attention),
        _                 => Palette.InkDim,
    };
}

/// <summary>THE STATUS VOCABULARY: one word, one glyph and one color per file condition, for the whole app.
/// GREEN = all-clear, AMBER = a gap you can close, RED = wrong/gone bytes, ORANGE = safely unplugged, DIM = nothing to do.</summary>
public static class ConditionVocabulary
{
    public static string Word(Grog.Core.Sync.FileCondition c) => c switch
    {
        Grog.Core.Sync.FileCondition.Present or Grog.Core.Sync.FileCondition.Verified => "Backed Up",
        Grog.Core.Sync.FileCondition.Detached => "Disconnected",   // removable drive unplugged -> safe
        Grog.Core.Sync.FileCondition.Missing  => "Missing",
        Grog.Core.Sync.FileCondition.Corrupt  => "Corrupt",
        Grog.Core.Sync.FileCondition.Outdated => "Update",
        Grog.Core.Sync.FileCondition.Partial  => "Partial",
        _ => "Not Downloaded",
    };

    /// <summary>The game-level word: same vocabulary as the file condition (a game with a corrupt file reads
    /// "Corrupt", not a different word for the same red). Zero in-scope files derives Complete ("nothing to
    /// do"), but the grid must not claim "Backed Up" with no files, so that case says what it is.</summary>
    public static string Word(BackupStatus s, bool notInScope) => s switch
    {
        BackupStatus.Complete when notInScope => "Not in Scope",
        BackupStatus.Complete    => "Backed Up",
        BackupStatus.Partial     => "Partial",
        BackupStatus.Outdated    => "Update",
        BackupStatus.Missing     => "Missing",
        BackupStatus.Corrupt     => "Corrupt",
        BackupStatus.NotBackedUp => "Not Downloaded",
        _                        => "--",
    };

    public static string Glyph(string word) => word switch
    {
        "Backed Up"      => "\u2713",   // check
        "Update"         => "\u2191",   // up arrow - a gap you can close
        "Corrupt"        => "\u25B2",   // triangle - bytes are wrong
        "Missing"        => "\u2715",   // cross - bytes are gone
        "Disconnected"   => "\u23CF",   // eject - drive unplugged, bytes are fine
        "Verifying"      => "\u27F3",   // cycle
        "Moving"         => "\u2192",
        "Unavailable"    => "\u2014",   // em dash - GOG will not serve it; never an alarm
        "Not Downloaded" => "\u25CB",   // hollow circle - nothing done yet
        _                => "\u25CB",
    };

    public static IBrush Brush(string word) => word switch
    {
        "Backed Up"                       => Palette.SuccessGreen,
        "Update" or "Partial"             => Palette.AccentAmber,
        "Corrupt" or "Missing"            => Palette.ErrorRed,
        "Disconnected"                    => Palette.WarnOrange,
        "Moving"                          => Palette.AccentAmber,
        _                                 => Palette.InkMuted,   // dim: nothing is wrong
    };
}

public sealed record ExtraTypeSegment(string Name, IBrush Brush)
{
    public bool HasName => !string.IsNullOrEmpty(Name);
    /// <summary>Proportional width when the segment is drawn in a star-sized grid.</summary>
    public Avalonia.Controls.GridLength Col { get; init; } = new(1, Avalonia.Controls.GridUnitType.Star);
    /// <summary>Bytes of this type actually backed up -- the legend entry doubles as the breakdown. BYTES, not
    /// files: the segment widths are byte-weighted and the legend must share the bar's unit.</summary>
    public long DoneBytes { get; init; }
    public string DoneBytesText => Grog.Core.Format.ByteFormat.Size(DoneBytes);
    // ---------- Content-type colors: ONE source of truth ----------
    // Every surface resolves a content type's color HERE; hues are fixed per bucket (NOT by position) so they
    // stay stable as buckets drop in and out of scope. "Game" has NO fixed hue: it borrows the live theme accent.
    private static SolidColorBrush Br(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
    private static readonly Dictionary<string, SolidColorBrush> ByKey = new(StringComparer.OrdinalIgnoreCase)
    {
        // CONTENT types: muted editorial hues so the bright PLATFORM family below reads as a separate axis.
        // Spread around the wheel, clear of the amber that means "Game"; each RGB channel a multiple of 8.
        ["Mod"]           = Br(96, 168, 160),    // muted teal -- software, distinct from the accent that means "Game"
        ["Soundtracks"]   = Br(112, 144, 208),   // dusty blue
        ["Manuals"]       = Br(184, 176, 88),    // olive gold
        // Videos is NOT here -- it's theme-aware (muted violet default / amber GOG), resolved via Palette.TypeVideos.
        ["Art"]           = Br(128, 176, 112),   // sage green
        ["Localizations"] = Br(112, 192, 184),   // soft cyan
        ["Alt Versions"]  = Br(208, 136, 160),   // dusty rose
        ["Movie"]         = Br(184, 120, 176),   // muted magenta -- legacy GOG movies
        ["Old versions"]  = Br(144, 128, 112),   // warm taupe: distinct, rarely-used; on disk, never part of completeness
        ["Other"]         = Br(136, 144, 160),   // gray: the catch-all reads as neutral
        // PLATFORMS: their OWN bright family (a separate axis -- every platform file is a game installer for a
        // different OS). Brighter/more saturated than the content hues on purpose, so an OS stands apart.
        ["Windows"]       = Br(48, 152, 255),    // Azure blue
        ["macOS"]         = Br(48, 208, 104),    // green
        ["Linux"]         = Br(248, 200, 24),    // Tux yellow
    };
    private static readonly SolidColorBrush Fallback = Br(136, 144, 160);

    /// <summary>The app-wide SHORT label for a content type: folds the classifier's long bucket names and
    /// synonyms onto the canonical keys, so every surface names (and colors) a type the same way.</summary>
    public static string Label(string key) => Canonical(key);
    private static string Canonical(string key) => key switch
    {
        "Games"                  => "Game",
        Grog.Core.Sync.ExtraClassifier.Video or Grog.Core.Sync.ExtraClassifier.AlternateVersions
            or Grog.Core.Sync.ExtraClassifier.GuidesAndDocs or Grog.Core.Sync.ExtraClassifier.LevelEditors
            or Grog.Core.Sync.ExtraClassifier.AudioOther or Grog.Core.Sync.ExtraClassifier.AddOns
                                 => Grog.Core.Sync.ExtraClassifier.ShortLabel(key),
        // Platform synonyms -> canonical keys, so the OS string resolves to a color + a properly-cased label.
        "windows" or "linux" or "mac" or "osx" or "macos" => PlatformNames.Label(key),
        _                        => key,
    };

    /// <summary>The color for a content type, stable regardless of what else is on screen. "Game" returns
    /// the live theme accent brush (so it re-tints with the theme); every other type is a fixed hue.</summary>
    public static IBrush ColorFor(string key)
    {
        var k = Canonical(key);
        // Game + Videos borrow live (theme-swapped) brushes; every other type is a fixed hue.
        if (string.Equals(k, "Game", StringComparison.OrdinalIgnoreCase)) return Palette.AccentAmber;
        if (string.Equals(k, "Videos", StringComparison.OrdinalIgnoreCase)) return Palette.TypeVideos;
        return ByKey.TryGetValue(k, out var b) ? b : Fallback;
    }
}

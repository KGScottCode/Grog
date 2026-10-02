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

/// <summary>One language the library actually contains, with what it costs.</summary>
public partial class LanguageOption : ObservableObject
{
    public LanguageOption(string name, int fileCount, long bytes, bool isChecked, bool isPrimary = false, bool isLocked = false)
    { Name = name; FileCount = fileCount; Bytes = bytes; _isChecked = isChecked; IsPrimary = isPrimary; IsLocked = isLocked; }

    public string Name { get; }
    public int FileCount { get; }
    public long Bytes { get; }
    /// <summary>Not a choice: files with no language tag are always kept. Checked, disabled, and stated
    /// with its cost so the flyout's entries sum to the side's total.</summary>
    public bool IsLocked { get; }
    public bool IsEnabled => !IsLocked;
    /// <summary>What adding this language costs. Framed as a price, not a loss: with one language seeded,
    /// the others are additions you opt into. The locked entry is not an addition, so no "+".</summary>
    public string SizeText => IsLocked
        ? $"{Grog.Core.Format.ByteFormat.Size(Bytes)} · {FileCount} files"
        : $"+{Grog.Core.Format.ByteFormat.Size(Bytes)} · {FileCount} files";
    public System.Action? Changed { get; init; }

    /// <summary>The seeded language (yours). Always shown; the rest hide behind "Show other languages".</summary>
    public bool IsPrimary { get; }
    private bool _othersShown;
    public bool IsVisible => IsPrimary || _othersShown;
    public void RaiseVisible(bool othersShown)
    {
        _othersShown = othersShown;
        OnPropertyChanged(nameof(IsVisible));
    }

    [ObservableProperty] private bool _isChecked;
    partial void OnIsCheckedChanged(bool value) { OnPropertyChanged(nameof(ItemOpacity)); Changed?.Invoke(); }
    /// <summary>The full language list is shown expanded; the unselected "extras you can opt into" read dimmed,
    /// and selecting one brightens it. Primary (seeded) language starts checked, so it's bright by default.</summary>
    public double ItemOpacity => IsLocked ? 0.55 : IsChecked ? 1.0 : 0.5;
}

public partial class FilterOption : ObservableObject
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    /// <summary>Legend color square shown beside the label in a dropdown (extras types); null = no square.</summary>
    public IBrush? Swatch { get; init; }
    public bool HasSwatch => Swatch is not null;
    public Action? Changed { get; init; }

    [ObservableProperty] private bool _isChecked = true;
    private bool _suppress;
    partial void OnIsCheckedChanged(bool value) { if (!_suppress) Changed?.Invoke(); }

    /// <summary>Set without firing Changed (caller re-applies the filter once for a batch).</summary>
    public void SetCheckedQuiet(bool value) { _suppress = true; IsChecked = value; _suppress = false; }
}

/// <summary>Small vector glyphs (MDI, 24x24 viewbox) giving each filter chip a face for its content type,
/// rendered white inside the chip's colored tile.</summary>
public static class ChipIcons
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Game"] = "M7.97,16L5,19C4.67,19.3 4.23,19.5 3.75,19.5A1.75,1.75 0 0,1 2,17.75V17.5L3,10.12C3.21,7.81 5.14,6 7.5,6H16.5C18.86,6 20.79,7.81 21,10.12L22,17.5V17.75A1.75,1.75 0 0,1 20.25,19.5C19.77,19.5 19.33,19.3 19,19L16.03,16H7.97M7,8V10H5V11H7V13H8V11H10V10H8V8H7M16.5,8A0.75,0.75 0 0,0 15.75,8.75A0.75,0.75 0 0,0 16.5,9.5A0.75,0.75 0 0,0 17.25,8.75A0.75,0.75 0 0,0 16.5,8M14.75,9.75A0.75,0.75 0 0,0 14,10.5A0.75,0.75 0 0,0 14.75,11.25A0.75,0.75 0 0,0 15.5,10.5A0.75,0.75 0 0,0 14.75,9.75M18.25,9.75A0.75,0.75 0 0,0 17.5,10.5A0.75,0.75 0 0,0 18.25,11.25A0.75,0.75 0 0,0 19,10.5A0.75,0.75 0 0,0 18.25,9.75Z",
        ["Mod"] = "M20.5,11H19V7C19,5.89 18.1,5 17,5H13V3.5A2.5,2.5 0 0,0 10.5,1A2.5,2.5 0 0,0 8,3.5V5H4A2,2 0 0,0 2,7V10.8H3.5C5,10.8 6.2,12 6.2,13.5C6.2,15 5,16.2 3.5,16.2H2V20A2,2 0 0,0 4,22H7.8V20.5C7.8,19 9,17.8 10.5,17.8C12,17.8 13.2,19 13.2,20.5V22H17A2,2 0 0,0 19,20V16H20.5A2.5,2.5 0 0,0 23,13.5A2.5,2.5 0 0,0 20.5,11Z",
        ["Soundtracks"] = "M12,3V13.55C11.41,13.21 10.73,13 10,13A3,3 0 0,0 7,16A3,3 0 0,0 10,19A3,3 0 0,0 13,16V7H17V3H12Z",
        ["Manuals"] = "M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M18,20H6V4H13V9H18V20Z",
        ["Videos"] = "M18,4L20,8H17L15,4H13L15,8H12L10,4H8L10,8H7L5,4H4A2,2 0 0,0 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V4H18Z",
        ["Art"] = "M17.5,12A1.5,1.5 0 0,1 16,10.5A1.5,1.5 0 0,1 17.5,9A1.5,1.5 0 0,1 19,10.5A1.5,1.5 0 0,1 17.5,12M14.5,8A1.5,1.5 0 0,1 13,6.5A1.5,1.5 0 0,1 14.5,5A1.5,1.5 0 0,1 16,6.5A1.5,1.5 0 0,1 14.5,8M9.5,8A1.5,1.5 0 0,1 8,6.5A1.5,1.5 0 0,1 9.5,5A1.5,1.5 0 0,1 11,6.5A1.5,1.5 0 0,1 9.5,8M6.5,12A1.5,1.5 0 0,1 5,10.5A1.5,1.5 0 0,1 6.5,9A1.5,1.5 0 0,1 8,10.5A1.5,1.5 0 0,1 6.5,12M12,3A9,9 0 0,0 3,12A9,9 0 0,0 12,21A1.5,1.5 0 0,0 13.5,19.5C13.5,19.11 13.35,18.76 13.11,18.5C12.88,18.23 12.73,17.88 12.73,17.5A1.5,1.5 0 0,1 14.23,16H16A5,5 0 0,0 21,11C21,6.58 16.97,3 12,3Z",
        ["Other"] = "M16,12A2,2 0 0,1 18,10A2,2 0 0,1 20,12A2,2 0 0,1 18,14A2,2 0 0,1 16,12M10,12A2,2 0 0,1 12,10A2,2 0 0,1 14,12A2,2 0 0,1 12,14A2,2 0 0,1 10,12M4,12A2,2 0 0,1 6,10A2,2 0 0,1 8,12A2,2 0 0,1 6,14A2,2 0 0,1 4,12Z",
        ["Localizations"] = "M12.87,15.07L10.33,12.56L10.36,12.53C12.1,10.59 13.34,8.36 14.07,6H17V4H10V2H8V4H1V6H12.17C11.5,7.92 10.44,9.75 9,11.35C8.07,10.32 7.3,9.19 6.69,8H4.69C5.42,9.63 6.42,11.17 7.67,12.56L2.58,17.58L4,19L9,14L12.11,17.11L12.87,15.07M18.5,10H16.5L12,22H14L15.12,19H19.87L21,22H23L18.5,10M15.88,17L17.5,12.67L19.12,17H15.88Z",
        ["Alt Versions"] = "M19,21H8V7H19M19,5H8A2,2 0 0,0 6,7V21A2,2 0 0,0 8,23H19A2,2 0 0,0 21,21V7A2,2 0 0,0 19,5M16,1H4A2,2 0 0,0 2,3V17H4V3H16V1Z",
    };

    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Geometry? For(string key)
    {
        if (key is null) return null;
        if (Cache.TryGetValue(key, out var g)) return g;
        if (!Paths.TryGetValue(key, out var p)) return null;
        g = Geometry.Parse(p);
        Cache[key] = g;
        return g;
    }
}

public partial class FilterChip : ObservableObject
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public IBrush? Swatch { get; init; }            // the color square (Game = live accent, extras = fixed hue)
    public Geometry? Icon { get; init; }            // white glyph shown inside the colored tile (per content type)
    public Action? Changed { get; init; }
    /// <summary>Per-chip callback carrying the chip itself: a device grid needs to know WHICH type toggled,
    /// and its options are rebuilt with the inventory, so this one is settable rather than init-only.</summary>
    public Action<FilterChip>? Changed2 { get; set; }
    [ObservableProperty] private bool _isActive = true;   // Model A: chips start lit (= everything shown)
    // Conditional chips (Localizations, Alt versions) hide when their content isn't in scope; most chips stay true.
    [ObservableProperty] private bool _isVisible = true;
    private bool _suppress;
    partial void OnIsActiveChanged(bool value)
    {
        RaiseColors();
        if (_suppress) return;
        Changed?.Invoke();
        Changed2?.Invoke(this);
    }
    public void SetActiveQuiet(bool value) { _suppress = true; IsActive = value; _suppress = false; }

    // Fill + text are derived LIVE from the swatch's current color (not frozen at construction), so the
    // Game chip -- whose swatch IS the shared accent brush -- re-tints with the theme.
    private (byte R, byte G, byte B)? Rgb => Swatch is ISolidColorBrush s ? (s.Color.R, s.Color.G, s.Color.B) : null;

    // An active chip takes its OWN color (Soundtracks = blue, Manuals = lime, Game = the accent); inactive
    // is a neutral outline so at rest the bar is calm.
    public IBrush ChipBackground => IsActive && Rgb is { } c ? new SolidColorBrush(Color.FromArgb(0x2E, c.R, c.G, c.B)) : Brushes.Transparent;
    public IBrush ChipBorder => IsActive ? (Swatch ?? Palette.StrokeStronger) : Palette.StrokeStronger;
    public IBrush ChipForeground => IsActive && Rgb is { } c ? new SolidColorBrush(Color.FromArgb(0xFF, c.R, c.G, c.B)) : Palette.InkMuted;

    /// <summary>Re-raise the derived color properties (after IsActive flips, or after a theme swap re-tints
    /// the accent brush the Game chip points at).</summary>
    public void RaiseColors()
    {
        OnPropertyChanged(nameof(ChipBackground));
        OnPropertyChanged(nameof(ChipBorder));
        OnPropertyChanged(nameof(ChipForeground));
    }
}

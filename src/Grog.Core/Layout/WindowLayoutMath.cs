// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Layout;

/// <summary>
/// Pure, UI-agnostic arithmetic for validating persisted window/grid layout on restore.
/// Kept in Core (no Avalonia dependency) so the decisions are unit-testable; the App layer
/// captures/applies the actual controls and delegates every judgment call to here.
/// </summary>
public static class WindowLayoutMath
{
    /// <summary>Floor for a restored window so a corrupt/tiny saved size can't produce an unusable shell.</summary>
    public const double MinWindowWidth = 900;
    public const double MinWindowHeight = 560;

    /// <summary>Sentinel stored for a star-sized (proportional) column so restore leaves it star.</summary>
    public const double StarColumn = -1.0;

    /// <summary>
    /// Clamp a restored window dimension. Invalid saved values (NaN, non-positive, or absurdly large)
    /// fall back to the design default; otherwise the value is floored to <paramref name="min"/>.
    /// </summary>
    public static double ClampDimension(double saved, double min, double fallback)
    {
        if (double.IsNaN(saved) || double.IsInfinity(saved) || saved <= 0 || saved > 100_000)
            return fallback;
        return saved < min ? min : saved;
    }

    /// <summary>
    /// A saved column-width array only applies when it's non-empty and its length matches the
    /// current column count (guards against columns being added/removed between versions).
    /// </summary>
    public static bool ColumnWidthsApply(double[]? saved, int columnCount)
        => saved is not null && saved.Length > 0 && saved.Length == columnCount;

    /// <summary>A single saved column width is applied only when it's a real positive pixel value
    /// (star sentinel and non-positive values are left to the XAML-defined width).</summary>
    public static bool ShouldApplyColumnWidth(double saved)
        => saved > 0 && !double.IsNaN(saved) && !double.IsInfinity(saved);
}

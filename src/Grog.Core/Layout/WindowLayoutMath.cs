// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Layout;

public static class WindowLayoutMath
{
    public const double MinWindowWidth = 900;
    public const double MinWindowHeight = 560;

    public const double StarColumn = -1.0;

    public static double ClampDimension(double saved, double min, double fallback)
    {
        if (double.IsNaN(saved) || double.IsInfinity(saved) || saved <= 0 || saved > 100_000)
            return fallback;
        return saved < min ? min : saved;
    }

    public static bool ColumnWidthsApply(double[]? saved, int columnCount)
        => saved is not null && saved.Length > 0 && saved.Length == columnCount;

    public static bool ShouldApplyColumnWidth(double saved)
        => saved > 0 && !double.IsNaN(saved) && !double.IsInfinity(saved);
}

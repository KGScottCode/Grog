// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.Core.Layout;
using Grog.Core.Tests.Framework;

public class LayoutMathTests
{
    [Test]
    void ClampDimension_FloorsBelowMin()
    {
        Assert.True(WindowLayoutMath.ClampDimension(400, 900, 1180) == 900, "below min → min");
    }

    [Test]
    void ClampDimension_KeepsValidSize()
    {
        Assert.True(WindowLayoutMath.ClampDimension(1400, 900, 1180) == 1400, "valid size unchanged");
    }

    [Test]
    void ClampDimension_RejectsGarbage()
    {
        Assert.True(WindowLayoutMath.ClampDimension(double.NaN, 900, 1180) == 1180, "NaN → fallback");
        Assert.True(WindowLayoutMath.ClampDimension(0, 900, 1180) == 1180, "zero → fallback");
        Assert.True(WindowLayoutMath.ClampDimension(-50, 900, 1180) == 1180, "negative → fallback");
        Assert.True(WindowLayoutMath.ClampDimension(200_000, 900, 1180) == 1180, "absurd → fallback");
    }

    [Test]
    void ColumnWidthsApply_OnlyWhenCountMatches()
    {
        Assert.False(WindowLayoutMath.ColumnWidthsApply(null, 5), "null → no");
        Assert.False(WindowLayoutMath.ColumnWidthsApply(new double[0], 5), "empty → no");
        Assert.False(WindowLayoutMath.ColumnWidthsApply(new double[] { 1, 2, 3 }, 5), "wrong length → no");
        Assert.True(WindowLayoutMath.ColumnWidthsApply(new double[] { 1, 2, 3, 4, 5 }, 5), "match → yes");
    }

    [Test]
    void ShouldApplyColumnWidth_SkipsStarAndJunk()
    {
        Assert.False(WindowLayoutMath.ShouldApplyColumnWidth(WindowLayoutMath.StarColumn), "star sentinel → skip");
        Assert.False(WindowLayoutMath.ShouldApplyColumnWidth(0), "zero → skip");
        Assert.True(WindowLayoutMath.ShouldApplyColumnWidth(120), "real pixels → apply");
    }
}

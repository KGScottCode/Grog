using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Grog.App;

/// <summary>Resolves named palette brushes from Styles/Palette.axaml for code-behind use, so C# status colors
/// reference the same tokens as the views instead of hardcoded hex. Color = meaning; single source for both.</summary>
public static class Palette
{
    // Application.Current is null under unit tests (no running Avalonia app); fall back to Transparent
    // there, since nothing is being painted.
    private static IBrush B(string key)
        => Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush br
            ? br : Brushes.Transparent;

    public static IBrush AccentAmber => B(nameof(AccentAmber));
    public static IBrush AccentAmberSoft => B(nameof(AccentAmberSoft));
    public static IBrush AccentAmberDeep => B(nameof(AccentAmberDeep));
    public static IBrush AccentOrange => B(nameof(AccentOrange));
    public static IBrush SuccessGreen => B(nameof(SuccessGreen));
    public static IBrush SuccessGreenVivid => B(nameof(SuccessGreenVivid));
    public static IBrush ErrorRed => B(nameof(ErrorRed));
    public static IBrush ErrorRedBright => B(nameof(ErrorRedBright));
    public static IBrush WarnOrange => B(nameof(WarnOrange));
    public static IBrush SurfaceWarnDark2 => B(nameof(SurfaceWarnDark2));
    public static IBrush TypeVideos => B(nameof(TypeVideos));
    public static IBrush InkPrimary => B(nameof(InkPrimary));
    public static IBrush InkDim => B(nameof(InkDim));
    public static IBrush InkMuted => B(nameof(InkMuted));
    public static IBrush StrokeStronger => B(nameof(StrokeStronger));
    public static IBrush StrokeSubtle => B(nameof(StrokeSubtle));
    public static IBrush SurfaceErrorDark2 => B(nameof(SurfaceErrorDark2));
    public static IBrush SurfaceStroke => B(nameof(SurfaceStroke));
}

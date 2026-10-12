// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace Grog.App;

public enum GrogTheme { Default, OdeToGog, Tavern }

public static class ThemeService
{
    private static readonly (string Key, string Amber, string Purple, string Tavern)[] Accents =
    {
        ("AccentAmber",        "#D9A441",   "#A874FF",   "#E2A63B"),  // main accent (Tavern: candle gold)
        ("AccentAmberDeep",    "#D29922",   "#8B4FE0",   "#C88E1E"),
        ("AccentAmberHover",   "#E4B458",   "#B98CFF",   "#EDBB55"),
        ("AccentOrange",       "#DB6D28",   "#B95FE6",   "#D06A20"),  // warm secondary
        ("AccentAmberSoft",    "#C29A54",   "#9B7BC7",   "#C79B45"),
        ("SurfaceAmberSoft",   "#24D9A441", "#24A874FF", "#24E2A63B"),  // accent @ ~14% alpha (fills)
        ("StrokeAmberSoft",    "#52D9A441", "#52A874FF", "#52E2A63B"),  // accent @ ~32% alpha (strokes)
        ("SurfaceAmberDark",   "#241F17",   "#1E1A29",   "#1E1810"),  // accent-tinted dark surfaces
        ("SurfaceAmberDark2",  "#2E2A1C",   "#272233",   "#272113"),
        ("SurfaceAmberDark3",  "#2A2419",   "#241E30",   "#231D11"),
        ("SurfaceAmberDark4",  "#3A2E14",   "#2E2440",   "#33270E"),
        ("SurfaceAmberSelect", "#211A0F",   "#1B1630",   "#1B150A"),  // selected accent-card fill
        ("NavActiveTint",      "#D9A441",   "#A874FF",   "#E2A63B"),
        ("TypeVideos",         "#9B7BE0",   "#E0A33C",   "#9B7BE0"),
        ("ToggleSwitchFillOn",                "#D9A441",   "#A874FF",   "#E2A63B"),
        ("ToggleSwitchFillOnPointerOver",     "#E4B458",   "#B98CFF",   "#EDBB55"),
        ("ToggleSwitchFillOnPressed",         "#D29922",   "#8B4FE0",   "#C88E1E"),
        ("ToggleSwitchFillOnDisabled",        "#52D9A441", "#52A874FF", "#52E2A63B"),
        ("ToggleSwitchStrokeOn",              "#D9A441",   "#A874FF",   "#E2A63B"),
        ("ToggleSwitchStrokeOnPointerOver",   "#E4B458",   "#B98CFF",   "#EDBB55"),
        ("ToggleSwitchStrokeOnPressed",       "#D29922",   "#8B4FE0",   "#C88E1E"),
        ("ToggleSwitchStrokeOnDisabled",      "#52D9A441", "#52A874FF", "#52E2A63B"),
    };

    // The NEUTRAL ladder
    private static readonly (string Key, string Stock, string Tavern)[] Neutrals =
    {
        ("SurfaceSunken",      "#0F1318", "#050505"),
        ("SurfaceBg",          "#14181D", "#0A0A0A"),
        ("SurfaceBgAlt",       "#141A20", "#0C0C0C"),
        ("SurfaceNeutralDark", "#1A1A1A", "#131313"),
        ("SurfaceCard",        "#181D23", "#161616"),
        ("SurfacePanelAlt",    "#191F26", "#171717"),
        ("SurfacePanel",       "#1B2027", "#181818"),
        ("SurfaceSection",     "#1C222A", "#1A1A1A"),
        ("SurfaceRaisedAlt",   "#1E242B", "#1C1C1C"),
        ("SurfaceRaised",      "#20262E", "#1E1E1E"),
        ("SurfaceRaisedAlt2",  "#232A32", "#212121"),
        ("SurfaceRaisedAlt3",  "#262D36", "#242424"),
        ("SurfaceStroke",      "#2B333D", "#303030"),
        ("StrokeAlt",          "#33404E", "#3B3B3B"),
        ("StrokeSubtle",       "#3A424D", "#404040"),
        ("StrokeStrong",       "#3D4854", "#484848"),
        ("StrokeStronger",     "#4A525C", "#525252"),
        ("StrokeStrongest",    "#4E5763", "#595959"),
        ("InkPrimary",         "#E8E0D8", "#ECE3D2"),
        ("InkBright",          "#C6CBD0", "#C9C4B8"),
        ("InkDim",             "#9AA1A8", "#A29B8E"),
        ("InkFaint",           "#8A9096", "#8F887C"),
        ("InkMuted",           "#6E747A", "#736D63"),
        ("InkMutedDim",        "#5C6268", "#605A51"),
        ("ToggleSwitchKnobFillOn",            "#14181D", "#0A0A0A"),
        ("ToggleSwitchKnobFillOnPointerOver", "#14181D", "#0A0A0A"),
        ("ToggleSwitchKnobFillOnPressed",     "#14181D", "#0A0A0A"),
        ("ToggleSwitchKnobFillOnDisabled",    "#5C6268", "#605A51"),
        ("ScrimBg",            "#D414181D", "#D40A0A0A"),
        ("ScrimBgStrong",      "#EE14181D", "#EE0A0A0A"),
        ("ScrimSunken",        "#E60F1216", "#E6060606"),
        ("ScrimSunkenSoft",    "#CC0F1318", "#CC060606"),
    };

    private static readonly (string Key, string[] Stock, string[] Tavern)[] NeutralGradients =
    {
        ("CanvasGlow",      new[] { "#161B21", "#161B21" },            new[] { "#0A0A0A", "#0A0A0A" }),
        ("RailSurface",     new[] { "#171C22", "#12161B" },            new[] { "#0C0C0C", "#070707" }),
        ("CardSurface",     new[] { "#191E25", "#191E25" },            new[] { "#171717", "#171717" }),
        ("PlotRecess",      new[] { "#12171C", "#12171C", "#12171C" }, new[] { "#090909", "#090909", "#090909" }),
        ("SectionFadeOut",  new[] { "#1C222A", "#001C222A" },          new[] { "#1A1A1A", "#001A1A1A" }),
        ("SectionFadeLong", new[] { "#1C222A", "#001C222A" },          new[] { "#1A1A1A", "#001A1A1A" }),
    };

    public static GrogTheme Current { get; private set; } = GrogTheme.Default;

    public static event Action<GrogTheme>? Changed;

    public static void Apply(GrogTheme theme)
    {
        Current = theme;
        var app = Application.Current;
        if (app is not null)
        {
            foreach (var (key, amber, purple, tavern) in Accents)
                SetBrush(app, key, theme switch { GrogTheme.OdeToGog => purple, GrogTheme.Tavern => tavern, _ => amber });

            foreach (var (key, stock, tavern) in Neutrals)
                SetBrush(app, key, theme == GrogTheme.Tavern ? tavern : stock);

            foreach (var (key, stock, tavern) in NeutralGradients)
                SetStops(app, key, theme == GrogTheme.Tavern ? tavern : stock);

            // Brand-tile gradient
            SetStops(app, "BrandTileBrush", theme switch
            {
                GrogTheme.OdeToGog => new[] { "#9E67F2", "#5A2CAE" },
                GrogTheme.Tavern   => new[] { "#EDBB55", "#C88E1E" },
                _                  => new[] { "#E4B458", "#D29922" },
            });

            // Section-header rule
            SetStops(app, "AmberRuleFade", theme switch
            {
                GrogTheme.OdeToGog => new[] { "#99A874FF", "#00A874FF" },
                GrogTheme.Tavern   => new[] { "#99E2A63B", "#00E2A63B" },
                _                  => new[] { "#99D9A441", "#00D9A441" },
            });

            // Primary-account left edge
            SetStops(app, "AmberEdgeFade", theme switch
            {
                GrogTheme.OdeToGog => new[] { "#CCA874FF", "#00A874FF" },
                GrogTheme.Tavern   => new[] { "#CCE2A63B", "#00E2A63B" },
                _                  => new[] { "#CCD9A441", "#00D9A441" },
            });

            // Rail brand glow
            SetStops(app, "RailBrandGlow", theme switch
            {
                GrogTheme.OdeToGog => new[] { "#5CA874FF", "#1EA874FF", "#00A874FF" },
                GrogTheme.Tavern   => new[] { "#5CE2A63B", "#1EE2A63B", "#00E2A63B" },
                _                  => new[] { "#5CD9A441", "#1ED9A441", "#00D9A441" },
            });

            // Speed-graph trace
            SetStops(app, "SpeedTraceFill", theme switch
            {
                GrogTheme.OdeToGog => new[] { "#C7A874FF", "#1AA874FF" },
                GrogTheme.Tavern   => new[] { "#C7E2A63B", "#1AE2A63B" },
                _                  => new[] { "#C7D9A441", "#1AD9A441" },
            });
        }
        Changed?.Invoke(theme);
    }

    private static void SetBrush(Application app, string key, string hex)
    {
        if (app.TryGetResource(key, app.ActualThemeVariant, out var res) && res is SolidColorBrush brush)
            brush.Color = Color.Parse(hex);
    }

    private static void SetStops(Application app, string key, string[] hexes)
    {
        if (!app.TryGetResource(key, app.ActualThemeVariant, out var res) || res is not GradientBrush g) return;
        var n = Math.Min(g.GradientStops.Count, hexes.Length);
        for (var i = 0; i < n; i++)
            g.GradientStops[i].Color = Color.Parse(hexes[i]);
    }
}

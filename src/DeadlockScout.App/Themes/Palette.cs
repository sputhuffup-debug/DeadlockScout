using System.Windows;
using System.Windows.Media;
using DeadlockScout.Core.Settings;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace DeadlockScout.App.Themes;

/// <summary>
/// The two colours that mean ahead/behind and good/bad everywhere a number is coloured (pace delta, gate, phase chips,
/// trends, wins and losses, the affordable line, coaching), in one place. <c>overlay.palette</c> picks them: the usual
/// green and red, or blue and orange (Okabe–Ito), which stay apart for red–green colour blindness. Applied once at
/// start-up, before any window is built, so the theme's <c>Good</c>/<c>Bad</c> brushes and every converter agree.
/// </summary>
public static class Palette
{
    public static readonly Color GoodNormal = Color.FromRgb(0x7F, 0xE0, 0x8A), BadNormal = Color.FromRgb(0xF0, 0x7A, 0x6E);
    public static readonly Color GoodBlue = Color.FromRgb(0x56, 0xB4, 0xE9), BadOrange = Color.FromRgb(0xF0, 0x8A, 0x3C);

    public static OverlayPalette Current { get; private set; } = OverlayPalette.Normal;
    public static Color GoodColor => Current == OverlayPalette.ColourBlind ? GoodBlue : GoodNormal;
    public static Color BadColor => Current == OverlayPalette.ColourBlind ? BadOrange : BadNormal;
    public static Brush Good { get; private set; } = Frozen(GoodNormal);
    public static Brush Bad { get; private set; } = Frozen(BadNormal);
    /// <summary>The bad colour at 20% for chip backgrounds (the counter-item chip).</summary>
    public static Brush BadSoft { get; private set; } = Frozen(Color.FromArgb(0x33, BadNormal.R, BadNormal.G, BadNormal.B));

    /// <summary>Picks the palette and puts its brushes in <paramref name="resources"/> (the app's) as <c>Good</c>, <c>Bad</c> and <c>BadSoft</c>.</summary>
    public static void Apply(OverlayPalette palette, ResourceDictionary? resources)
    {
        Current = palette;
        Good = Frozen(GoodColor);
        Bad = Frozen(BadColor);
        BadSoft = Frozen(Color.FromArgb(0x33, BadColor.R, BadColor.G, BadColor.B));
        if (resources is null) return;
        resources["Good"] = Good;
        resources["Bad"] = Bad;
        resources["BadSoft"] = BadSoft;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

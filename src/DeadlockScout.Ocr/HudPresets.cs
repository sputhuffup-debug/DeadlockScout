using DeadlockScout.Ocr.Abstractions;

namespace DeadlockScout.Ocr;

/// <summary>The preset boxes for one game window, and whether they were measured at its aspect ratio.</summary>
public sealed record HudPreset(HudRegion Souls, HudRegion Timer, bool Measured);

/// <summary>
/// Starting boxes for the souls count and the match clock, so a first setup is one click and a look at the preview. The
/// 16:9 boxes are the owner's, drawn on a 2560×1440 game (Nov 5, 2026) and kept as pixels of that window. Other aspect
/// ratios assume the HUD scales with the window's height, the souls count keeping its distance from the left edge and
/// the clock its place at the centre. UNVERIFIED for anything but 16:9: <see cref="HudPreset.Measured"/> says so, and
/// the tool asks for a look at the preview either way. Pure, so tested.
/// </summary>
public static class HudPresets
{
    private const double RefHeight = 1440, RefWidth = 2560;
    // The owner's boxes at 2560×1440, in pixels: the souls count (from the soul icon, covering both positions the count
    // takes with and without haunted souls) and the clock.
    private const double SoulsX = 143, SoulsY = 1168, SoulsW = 181, SoulsH = 72;
    private const double TimerX = 1249, TimerY = 33, TimerW = 59, TimerH = 21;

    /// <summary>The presets for a game window of <paramref name="width"/> × <paramref name="height"/> pixels; null for an empty one.</summary>
    public static HudPreset? For(int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        double aspect = (double)width / height, refAspect = RefWidth / RefHeight;
        // Horizontal sizes in units of the window's height, then as fractions of its width.
        double ToX(double px) => px / RefHeight / aspect;
        var souls = new HudRegion(ToX(SoulsX), SoulsY / RefHeight, ToX(SoulsW), SoulsH / RefHeight);
        double timerCentre = 0.5 + ToX(TimerX + TimerW / 2 - RefWidth / 2);
        var timer = new HudRegion(timerCentre - ToX(TimerW) / 2, TimerY / RefHeight, ToX(TimerW), TimerH / RefHeight);
        return new HudPreset(souls, timer, Math.Abs(aspect - refAspect) < 0.01);
    }
}

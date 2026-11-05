using DeadlockScout.Core.Settings;
using DeadlockScout.Ocr.Abstractions;

namespace DeadlockScout.Ocr;

/// <summary>Where a HUD element is pinned when the window changes shape.</summary>
public enum HudAnchor
{
    /// <summary>Keeps its distance from the left edge (the souls count).</summary>
    Left,
    /// <summary>Keeps its distance from the centre (the match clock).</summary>
    Centre,
}

/// <summary>
/// Carries boxes drawn on one window to a window of another shape. The rule the presets use: the HUD scales with the
/// window's height, so heights and vertical places stay the same fractions, while horizontal sizes and distances keep
/// their length in units of the height, from the left edge or from the centre. A window of the same shape (any
/// resolution) needs nothing: fractions already scale. UNVERIFIED beyond 16:9 until a match at another shape confirms it.
/// Pure, so tested.
/// </summary>
public static class HudCarry
{
    /// <summary>Aspect ratios closer than this count as the same shape.</summary>
    public const double SameShape = 0.01;

    public static HudRegion Carry(HudRegion box, HudAnchor anchor, double fromAspect, double toAspect)
    {
        if (fromAspect <= 0 || toAspect <= 0 || Math.Abs(fromAspect - toAspect) < SameShape) return box;
        double k = fromAspect / toAspect, w = box.Width * k;
        double x = anchor == HudAnchor.Left
            ? box.X * k
            : 0.5 + (box.X + box.Width / 2 - 0.5) * k - w / 2;
        return new HudRegion(x, box.Y, w, box.Height);
    }

    /// <summary>Whether boxes drawn on <paramref name="saved"/>'s window need carrying to one of <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static bool Moves(HudRegions saved, int width, int height)
        => saved.DrawnOn is { Width: > 0, Height: > 0 } d && width > 0 && height > 0
           && Math.Abs((double)d.Width / d.Height - (double)width / height) >= SameShape;

    /// <summary>
    /// The boxes as they fall on a window of <paramref name="width"/> × <paramref name="height"/>, with
    /// <see cref="HudRegions.DrawnOn"/> set to it; unchanged when they were saved without a size or the shape is the same.
    /// </summary>
    public static HudRegions For(HudRegions saved, int width, int height)
    {
        if (!Moves(saved, width, height)) return saved;
        double from = (double)saved.DrawnOn!.Width / saved.DrawnOn.Height, to = (double)width / height;
        HudRect? Move(HudRect? r, HudAnchor a) => RegionCalibration.FromRect(r) is { } box ? RegionCalibration.ToRect(Carry(box, a, from, to)) : r;
        return saved with { Souls = Move(saved.Souls, HudAnchor.Left), Timer = Move(saved.Timer, HudAnchor.Centre), DrawnOn = new HudSize(width, height) };
    }
}

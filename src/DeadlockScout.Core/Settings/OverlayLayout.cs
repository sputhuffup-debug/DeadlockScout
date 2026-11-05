namespace DeadlockScout.Core.Settings;

/// <summary>
/// Where panels the player dragged in the overlay's layout editor sit. Positions are in the overlay's reference units
/// (the window is scaled so its height is 1080 of them), so a layout keeps its place at any resolution of the same
/// aspect ratio; a different aspect ratio (an ultrawide) gets a layout of its own. Pure, so it is unit tested.
/// </summary>
public static class OverlayLayout
{
    /// <summary>Drag positions snap to this many reference units.</summary>
    public const double Grid = 8;

    /// <summary>"PacePanel@1920x1080": the panel and the overlay's size in reference units.</summary>
    public static string Key(string panel, double referenceWidth, double referenceHeight)
        => $"{panel}@{Math.Round(referenceWidth):0}x{Math.Round(referenceHeight):0}";

    /// <summary>The "@WxH" part every key for this overlay size ends with.</summary>
    public static string SizeSuffix(double referenceWidth, double referenceHeight) => Key("", referenceWidth, referenceHeight);

    public static double Snap(double v) => Math.Round(v / Grid) * Grid;

    /// <summary>A dragged panel's top-left, snapped and kept fully inside the overlay.</summary>
    public static PanelPosition Place(double left, double top, double panelWidth, double panelHeight, double referenceWidth, double referenceHeight)
    {
        double x = Math.Clamp(Snap(left), 0, Math.Max(0, referenceWidth - panelWidth));
        double y = Math.Clamp(Snap(top), 0, Math.Max(0, referenceHeight - panelHeight));
        return new PanelPosition(x, y);
    }

    /// <summary>Settings with one panel's position set.</summary>
    public static OverlaySettings WithPosition(this OverlaySettings o, string key, PanelPosition position)
    {
        var positions = new Dictionary<string, PanelPosition>(o.Positions ?? new()) { [key] = position };
        return o with { Positions = positions };
    }

    public const double MinPanelScale = 0.5, MaxPanelScale = 2.0, PanelScaleStep = 0.05;

    /// <summary>One panel's own size from settings, 1 when not set.</summary>
    public static double PanelScale(this OverlaySettings o, string panel)
        => o.PanelScales is { } scales && scales.TryGetValue(panel, out var s) && double.IsFinite(s) ? Math.Clamp(s, MinPanelScale, MaxPanelScale) : 1.0;

    /// <summary>
    /// Settings with one panel's own size moved by <paramref name="steps"/> wheel notches (5% each, 50–200%); back at 100%
    /// the entry is removed, so an untouched layout stays empty.
    /// </summary>
    public static OverlaySettings WithPanelScaleStep(this OverlaySettings o, string panel, int steps)
    {
        double next = Math.Clamp(Math.Round((o.PanelScale(panel) + steps * PanelScaleStep) * 20) / 20, MinPanelScale, MaxPanelScale);
        var scales = new Dictionary<string, double>(o.PanelScales ?? new());
        if (Math.Abs(next - 1.0) < 1e-9) scales.Remove(panel); else scales[panel] = next;
        return o with { PanelScales = scales };
    }

    /// <summary>Settings with every position for this overlay size removed: the panels go back to the default columns.</summary>
    public static OverlaySettings WithoutPositions(this OverlaySettings o, string sizeSuffix)
        => o with { Positions = (o.Positions ?? new()).Where(kv => !kv.Key.EndsWith(sizeSuffix, StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value) };
}

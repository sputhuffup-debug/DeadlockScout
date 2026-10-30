namespace DeadlockScout.Core.Settings;

/// <summary>A whole overlay layout: which panels show, where the moved ones sit (per screen shape), and their size.</summary>
public sealed record LayoutPreset(PanelSettings Panels, Dictionary<string, PanelPosition> Positions, double PanelScale);

/// <summary>
/// Overlay layouts to switch between: three built in (everything; the laning essentials; the bare minimum) and any the
/// player saves from the current layout. A preset replaces the panel switches, the moved panels' positions and the panel
/// size; the overlay's HUD scaling (1080p = 1.0, positions per screen shape) is not part of it, so one preset fits every
/// resolution.
/// </summary>
public static class OverlayPresets
{
    public const double MinScale = 0.6, MaxScale = 1.6;

    public static double ClampScale(double scale) => double.IsFinite(scale) ? Math.Clamp(Math.Round(scale, 2), MinScale, MaxScale) : 1.0;

    /// <summary>The built-in presets, by name, with a line saying what each keeps.</summary>
    public static readonly IReadOnlyList<(string Name, string Description, LayoutPreset Preset)> BuiltIn = new[]
    {
        ("Full", "Every panel, in the default columns.", new LayoutPreset(new PanelSettings(), new(), 1.0)),
        ("Laning", "Pace, lane card, laning timers, objectives and enemy pace; no build hint or session card.",
            new LayoutPreset(new PanelSettings { Build = false, NextItem = false, Session = false, Personal = false }, new(), 1.0)),
        ("Minimal", "The pace strip, laning timers and objectives, a little smaller.",
            new LayoutPreset(new PanelSettings
            {
                Sparkline = false, Personal = false, Lane = false, EnemyPace = false, Spikes = false, EnemyTendencies = false, Build = false, NextItem = false,
                Session = false, Scout = ScoutPanelMode.Hotkey,
            }, new(), 0.9)),
    };

    public static bool IsBuiltIn(string name) => BuiltIn.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A built-in preset or a saved one, by name (any case); null when neither has it.</summary>
    public static LayoutPreset? Find(OverlaySettings o, string name)
        => BuiltIn.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Preset
           ?? o.Presets.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The current layout as a preset (copies, so later drags do not change it).</summary>
    public static LayoutPreset Capture(OverlaySettings o)
        => new(o.Panels, new Dictionary<string, PanelPosition>(o.Positions ?? new()), ClampScale(o.PanelScale));

    /// <summary>Settings with <paramref name="preset"/>'s panels, positions and size; everything else as it was.</summary>
    public static OverlaySettings Apply(this OverlaySettings o, LayoutPreset preset)
        => o with { Panels = preset.Panels, Positions = new Dictionary<string, PanelPosition>(preset.Positions), PanelScale = ClampScale(preset.PanelScale) };

    /// <summary>Settings with the current layout saved as <paramref name="name"/> (a built-in name is refused: false).</summary>
    public static bool TrySave(this OverlaySettings o, string name, out OverlaySettings saved)
    {
        saved = o;
        name = name.Trim();
        if (name.Length == 0 || IsBuiltIn(name)) return false;
        var presets = new Dictionary<string, LayoutPreset>(o.Presets ?? new(), StringComparer.OrdinalIgnoreCase) { [name] = Capture(o) };
        saved = o with { Presets = new Dictionary<string, LayoutPreset>(presets) };
        return true;
    }

    public static OverlaySettings WithoutPreset(this OverlaySettings o, string name)
        => o with { Presets = (o.Presets ?? new()).Where(p => !p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToDictionary(p => p.Key, p => p.Value) };
}

using DeadlockScout.Core.Settings;

namespace DeadlockScout.Tests.Core;

public class OverlayLayoutTests
{
    [Fact]
    public void Keys_name_the_panel_and_the_overlay_size_in_reference_units()
    {
        Assert.Equal("PacePanel@1920x1080", OverlayLayout.Key("PacePanel", 1920, 1080));
        Assert.Equal("PacePanel@2520x1080", OverlayLayout.Key("PacePanel", 2519.6, 1080)); // 21:9
        Assert.Equal("@1920x1080", OverlayLayout.SizeSuffix(1920, 1080));
    }

    [Fact]
    public void A_drop_snaps_to_the_grid_and_stays_on_screen()
    {
        Assert.Equal(new PanelPosition(104, 200), OverlayLayout.Place(101, 203, 300, 100, 1920, 1080));
        Assert.Equal(new PanelPosition(0, 0), OverlayLayout.Place(-50, -9, 300, 100, 1920, 1080));
        Assert.Equal(new PanelPosition(1620, 980), OverlayLayout.Place(1900, 1070, 300, 100, 1920, 1080)); // right and bottom edges
    }

    [Fact]
    public void Reset_clears_only_the_current_size()
    {
        var o = new OverlaySettings()
            .WithPosition(OverlayLayout.Key("PacePanel", 1920, 1080), new PanelPosition(8, 8))
            .WithPosition(OverlayLayout.Key("LanePanel", 1920, 1080), new PanelPosition(16, 16))
            .WithPosition(OverlayLayout.Key("PacePanel", 2520, 1080), new PanelPosition(24, 24));
        var reset = o.WithoutPositions(OverlayLayout.SizeSuffix(1920, 1080));
        Assert.Equal(new[] { "PacePanel@2520x1080" }, reset.Positions.Keys);
        Assert.Equal(3, o.Positions.Count); // the original is untouched
    }

    [Fact]
    public void Each_panel_is_sized_on_its_own_in_wheel_steps_and_back_at_100_percent_it_is_forgotten()
    {
        var o = new OverlaySettings();
        Assert.Equal(1.0, o.PanelScale("PacePanel"));
        var bigger = o.WithPanelScaleStep("PacePanel", 1).WithPanelScaleStep("PacePanel", 1);
        Assert.Equal(1.1, bigger.PanelScale("PacePanel"), 9);
        Assert.Equal(1.0, bigger.PanelScale("TimersPanel"));
        Assert.Empty(o.PanelScales);                                         // the original is untouched

        var back = bigger.WithPanelScaleStep("PacePanel", -1).WithPanelScaleStep("PacePanel", -1);
        Assert.False(back.PanelScales.ContainsKey("PacePanel"));             // 100%: not kept

        var tiny = Enumerable.Range(0, 30).Aggregate(o, (s, _) => s.WithPanelScaleStep("TimersPanel", -1));
        Assert.Equal(OverlayLayout.MinPanelScale, tiny.PanelScale("TimersPanel"), 9);
        var huge = Enumerable.Range(0, 40).Aggregate(o, (s, _) => s.WithPanelScaleStep("TimersPanel", 1));
        Assert.Equal(OverlayLayout.MaxPanelScale, huge.PanelScale("TimersPanel"), 9);
        Assert.Equal(1.0, (o with { PanelScales = new() { ["PacePanel"] = double.NaN } }).PanelScale("PacePanel"));
    }

    [Fact]
    public void A_preset_keeps_each_panels_size_and_an_older_preset_leaves_them_alone()
    {
        var o = new OverlaySettings().WithPanelScaleStep("EnemyPanel", 2);
        var preset = OverlayPresets.Capture(o);
        Assert.Equal(1.1, preset.PanelScales!["EnemyPanel"], 9);
        Assert.Equal(1.1, new OverlaySettings().Apply(preset).PanelScale("EnemyPanel"), 9);

        var old = preset with { PanelScales = null };                        // saved before 0.5
        Assert.Equal(1.1, o.Apply(old).PanelScale("EnemyPanel"), 9);
    }

    [Theory]
    [InlineData(0.85, 0.85)]
    [InlineData(0.52, 0.5)]      // 5% steps
    [InlineData(0.05, 0.2)]      // never fully see-through: the text needs a backing
    [InlineData(1.4, 1.0)]
    [InlineData(double.NaN, 0.85)]
    public void Panel_opacity_is_kept_between_20_and_100_percent_in_5_percent_steps(double given, double kept)
    {
        Assert.Equal(kept, OverlayPresets.ClampOpacity(given), 9);
        Assert.Equal(0.85, new OverlaySettings().PanelOpacity, 9);           // as the panels looked before 0.6
    }
}

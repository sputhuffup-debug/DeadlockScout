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
}

using DeadlockScout.Core.Settings;

namespace DeadlockScout.Tests.Core;

public class OverlayPresetsTests
{
    private static OverlaySettings Moved() => new OverlaySettings { PanelScale = 1.25 }
        .WithPosition(OverlayLayout.Key("TimersPanel", 1920, 1080), new PanelPosition(40, 600));

    [Fact]
    public void Saving_captures_panels_positions_and_size_and_applying_puts_them_back()
    {
        var o = Moved() with { Panels = new PanelSettings { Build = false } };
        Assert.True(o.TrySave("My laning", out var saved));
        var reset = saved with { Panels = new PanelSettings(), Positions = new(), PanelScale = 1.0 };
        var back = reset.Apply(OverlayPresets.Find(reset, "my laning")!);   // any case
        Assert.False(back.Panels.Build);
        Assert.Equal(1.25, back.PanelScale);
        Assert.Equal(new PanelPosition(40, 600), back.Positions[OverlayLayout.Key("TimersPanel", 1920, 1080)]);
        Assert.Single(back.Presets);                                       // presets themselves stay
    }

    [Fact]
    public void A_saved_preset_does_not_move_when_the_layout_changes_later()
    {
        Assert.True(Moved().TrySave("Mine", out var saved));
        var dragged = saved.WithPosition(OverlayLayout.Key("TimersPanel", 1920, 1080), new PanelPosition(800, 80));
        Assert.Equal(new PanelPosition(40, 600), OverlayPresets.Find(dragged, "Mine")!.Positions[OverlayLayout.Key("TimersPanel", 1920, 1080)]);
    }

    [Fact]
    public void Built_ins_cannot_be_overwritten_and_presets_can_be_deleted()
    {
        Assert.False(new OverlaySettings().TrySave("minimal", out _));
        Assert.False(new OverlaySettings().TrySave("  ", out _));
        Assert.Equal(new[] { "Full", "Standard", "Laning", "Minimal" }, OverlayPresets.BuiltIn.Select(b => b.Name));
        Assert.True(Moved().TrySave("Mine", out var saved));
        Assert.Empty(saved.WithoutPreset("MINE").Presets);
        var minimal = new OverlaySettings().Apply(OverlayPresets.Find(new OverlaySettings(), "Minimal")!);
        Assert.True(minimal.Panels.Pace && minimal.Panels.Timers);
        Assert.False(minimal.Panels.EnemyPace || minimal.Panels.Objectives || minimal.Panels.Build || minimal.Panels.Lane);
    }

    [Fact]
    public void A_fresh_install_starts_calmer_than_Full_and_the_wizard_can_tell_which_layout_is_on()
    {
        var fresh = new OverlaySettings();
        Assert.False(fresh.Panels.Sparkline || fresh.Panels.Personal || fresh.Panels.Session || fresh.Panels.NextItem || fresh.Panels.EnemyTendencies);
        Assert.True(fresh.Panels.Pace && fresh.Panels.EnemyPace && fresh.Panels.Timers && fresh.Panels.Build);
        Assert.Equal("Standard", OverlayPresets.BuiltInNamed(fresh));
        var moved = Moved();
        var full = moved.WithPanelsOf(OverlayPresets.Find(moved, "Full")!);
        Assert.Equal("Full", OverlayPresets.BuiltInNamed(full));
        Assert.True(full.Panels.Sparkline && full.Panels.Session);
        Assert.Equal(moved.Positions, full.Positions);                           // the wizard keeps where panels were dragged
        Assert.Null(OverlayPresets.BuiltInNamed(fresh with { Panels = fresh.Panels with { Timers = false } }));
    }

    [Theory]
    [InlineData(0.2, 0.6)]
    [InlineData(1.234, 1.23)]
    [InlineData(3.0, 1.6)]
    [InlineData(double.NaN, 1.0)]
    public void Panel_size_stays_in_range(double input, double expected) => Assert.Equal(expected, OverlayPresets.ClampScale(input));
}

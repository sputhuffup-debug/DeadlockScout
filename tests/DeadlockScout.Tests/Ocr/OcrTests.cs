using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using DeadlockScout.Ocr;
using DeadlockScout.Ocr.Abstractions;

namespace DeadlockScout.Tests.Ocr;

public class OcrTests
{
    [Theory]
    [InlineData("12,480", 12480)] [InlineData("12 480", 12480)] [InlineData("12480", 12480)] [InlineData("l2480", 12480)]
    [InlineData("Souls: 9,050", 9050)] [InlineData("", null)] [InlineData("abc", null)] [InlineData("99999999", null)]
    public void Parses_souls(string text, int? expected) => Assert.Equal(expected, HudParser.ParseSouls(text));

    [Theory]
    [InlineData("14:20", 14, 20)] [InlineData("1:05", 1, 5)] [InlineData("l4:2O", 14, 20)] [InlineData("14;20", 14, 20)]
    public void Parses_clock(string text, int min, int sec) => Assert.Equal(new TimeSpan(0, min, sec), HudParser.ParseClock(text));

    [Theory] [InlineData("14:75")] [InlineData("nope")] [InlineData("")]
    public void Rejects_bad_clock(string text) => Assert.Null(HudParser.ParseClock(text));

    [Fact]
    public void Health_needs_enough_samples_and_a_high_success_rate()
    {
        var h = new OcrHealth(TimeSpan.FromSeconds(30), 0.8, 5);
        var t = DateTimeOffset.UtcNow;
        for (int i = 0; i < 4; i++) h.Record(t.AddSeconds(i), true);
        Assert.False(h.IsHealthy);
        h.Record(t.AddSeconds(4), true);
        Assert.True(h.IsHealthy);
        for (int i = 5; i < 9; i++) h.Record(t.AddSeconds(i), false);
        Assert.False(h.IsHealthy); // 5/9
        // old samples fall out of the window
        for (int i = 0; i < 6; i++) h.Record(t.AddSeconds(60 + i), true);
        Assert.True(h.IsHealthy);
    }

    /// <summary>Hands back a 1-px "crop" per region and records which regions were asked for.</summary>
    private sealed class FakeCapture : IRegionCapture
    {
        public readonly List<HudRegion> Asked = new();
        public bool IsAvailable => true;
        public Task<CapturedFrame?> CaptureAsync(HudRegion region, CancellationToken ct)
        {
            Asked.Add(region);
            return Task.FromResult<CapturedFrame?>(new CapturedFrame(Asked.Count, 1, 4, new byte[4], DateTimeOffset.UtcNow));
        }
    }

    /// <summary>Reads the crop by the order it was captured in: first the souls box, then the clock.</summary>
    private sealed class FakeEngine(Func<CapturedFrame, string?> read) : IOcrEngine
    {
        public string Name => "fake";
        public bool IsAvailable => true;
        public Task<string?> RecognizeAsync(CapturedFrame region, CancellationToken ct) => Task.FromResult(read(region));
    }

    private sealed class Regions : IHudRegionMap
    {
        public HudRegion? Souls { get; init; } = new(0.45, 0.92, 0.1, 0.04);
        public HudRegion? Timer { get; init; } = new(0.48, 0.0, 0.04, 0.02);
    }

    [Fact]
    public async Task Source_captures_only_the_two_boxes_and_emits_a_sample_when_both_read()
    {
        var regions = new Regions();
        var capture = new FakeCapture();
        var engine = new FakeEngine(crop => crop.Width % 2 == 1 ? "8,120" : "10:30");
        var src = new OcrSoulSource(capture, regions, engine, DeadlockScout.Core.Abstractions.SystemClock.Instance);
        // The first clock is used once a few readings in a row agree (OcrClockGuard).
        for (int i = 1; i < OcrClockGuard.Confirm; i++) Assert.Null(await src.TryReadOnceAsync(default));
        var s = await src.TryReadOnceAsync(default);
        Assert.Equal(new SoulSample(new TimeSpan(0, 10, 30), 8120, SampleSource.Ocr), s);
        Assert.Equal(new[] { regions.Souls, regions.Timer }, capture.Asked.Take(2)); // never a whole frame
        for (int i = 0; i < 5; i++) await src.TryReadOnceAsync(default);
        Assert.True(src.IsHealthy);

        var bad = new OcrSoulSource(new FakeCapture(), regions, new FakeEngine(_ => "??"), DeadlockScout.Core.Abstractions.SystemClock.Instance);
        Assert.Null(await bad.TryReadOnceAsync(default));
        Assert.False(bad.IsHealthy);
    }

    [Fact]
    public async Task A_misread_clock_is_put_right_when_one_digit_is_off_and_drops_the_reading_otherwise()
    {
        // The Oct 31, 2026 match read the HUD clock "22:53" between 12:52 and 12:54: one digit off, so since 0.3.3 it is
        // put right to 12:53. A clock further off ("34:55" for 12:55) still drops the whole reading, souls included,
        // before the soul guard or the meter see it, while the readings around it carry on.
        var clocks = new Queue<string>(new[] { "12:50", "12:51", "12:52", "22:53", "12:54", "34:55", "12:56" });
        string clockNow = "";
        var engine = new FakeEngine(crop => crop.Width % 2 == 1 ? "7,300" : clockNow);
        var time = new TestClock(new DateTimeOffset(2026, 11, 1, 4, 34, 0, TimeSpan.Zero));
        var src = new OcrSoulSource(new FakeCapture(), new Regions(), engine, time);
        var read = new List<SoulSample?>();
        while (clocks.Count > 0)
        {
            clockNow = clocks.Dequeue();
            read.Add(await src.TryReadOnceAsync(default));
            time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(new TimeSpan?[] { null, null, new TimeSpan(0, 12, 52), new TimeSpan(0, 12, 53), new TimeSpan(0, 12, 54), null, new TimeSpan(0, 12, 56) },
            read.Select(r => r?.GameTime));
        Assert.Equal((1, 1), (src.ClockCorrections, src.ClockRejections));
    }

    [Fact]
    public async Task Source_without_calibrated_boxes_captures_nothing()
    {
        var capture = new FakeCapture();
        var src = new OcrSoulSource(capture, new Regions { Timer = null }, new FakeEngine(_ => "1"), DeadlockScout.Core.Abstractions.SystemClock.Instance);
        Assert.Null(await src.TryReadOnceAsync(default));
        Assert.Empty(capture.Asked);
    }

    [Fact]
    public void Null_engine_leaves_the_source_unhealthy_so_the_feed_is_used()
    {
        var src = new OcrSoulSource(new NullRegionCapture(), new Regions(), new NullOcrEngine(), DeadlockScout.Core.Abstractions.SystemClock.Instance);
        Assert.False(src.IsHealthy);
    }

    [Fact]
    public void Settings_regions_are_fractions_of_the_game_window()
    {
        var map = new SettingsHudRegionMap(() => new HudRegions { Souls = new(0.5, 0.92, 0.1, 0.04) });
        Assert.Equal(new HudRegion(0.5, 0.92, 0.1, 0.04), map.Souls);
        Assert.Null(map.Timer);
        Assert.Equal((960, 994, 192, 43), map.Souls!.ToPixels(1920, 1080));
        Assert.Equal((1280, 1325, 256, 58), map.Souls.ToPixels(2560, 1440)); // the same box at 1440p
    }

    [Theory]
    [InlineData(100, 50, 300, 90, 0.1, 0.1, 0.2, 0.08)]   // top-left to bottom-right
    [InlineData(300, 90, 100, 50, 0.1, 0.1, 0.2, 0.08)]   // dragged the other way
    [InlineData(-50, -20, 100, 50, 0.0, 0.0, 0.1, 0.1)]   // clamped to the surface
    [InlineData(900, 450, 1200, 700, 0.9, 0.9, 0.1, 0.1)]
    public void A_drag_becomes_a_box_in_fractions_of_the_surface(double x1, double y1, double x2, double y2, double x, double y, double w, double h)
    {
        var box = HudRegion.FromDrag(x1, y1, x2, y2, 1000, 500)!;
        Assert.Equal(x, box.X, 6); Assert.Equal(y, box.Y, 6); Assert.Equal(w, box.Width, 6); Assert.Equal(h, box.Height, 6);
    }

    [Fact]
    public void A_click_or_a_sliver_is_not_a_box()
    {
        Assert.Null(HudRegion.FromDrag(100, 100, 100, 100, 1000, 500));
        Assert.Null(HudRegion.FromDrag(100, 100, 400, 100.5, 1000, 500));
        Assert.Null(HudRegion.FromDrag(0, 0, 10, 10, 0, 0));
    }

    [Fact]
    public void Pixel_boxes_stay_inside_the_client_area()
    {
        Assert.Equal((1910, 1075, 10, 5), new HudRegion(0.995, 0.995, 0.1, 0.1).ToPixels(1920, 1080)); // clipped at the right and bottom edges
        Assert.Equal((0, 0, 1, 1), new HudRegion(0, 0, 0.0001, 0.0001).ToPixels(1920, 1080));
    }

    [Fact]
    public void Calibration_round_trips_settings_and_walks_to_the_next_unset_box()
    {
        var cal = new RegionCalibration(new HudRegions { Souls = new(0.47, 0.92, 0.06, 0.03), Timer = new(0, 0, 0, 0.1) }); // a zero-width box is ignored
        Assert.True(cal.IsSet(RegionCalibration.SoulsKey));
        Assert.False(cal.IsSet(RegionCalibration.TimerKey));
        Assert.Equal(RegionCalibration.TimerKey, cal.NextUnset(RegionCalibration.SoulsKey));

        cal.Set(RegionCalibration.TimerKey, new HudRegion(0.48, 0.01, 0.04, 0.03));
        Assert.Null(cal.NextUnset(RegionCalibration.TimerKey));

        var saved = cal.ToSettings();
        Assert.Equal(new HudRect(0.48, 0.01, 0.04, 0.03), saved.Timer);
        Assert.Equal(new HudRect(0.47, 0.92, 0.06, 0.03), saved.Souls);

        var again = new RegionCalibration(saved);
        Assert.Equal(cal.Count, again.Count);
        Assert.Equal(cal.Get(RegionCalibration.TimerKey), again.Get(RegionCalibration.TimerKey));

        cal.Clear(RegionCalibration.TimerKey);
        Assert.Null(cal.ToSettings().Timer);
        Assert.Throws<ArgumentException>(() => cal.Set("nope", new HudRegion(0, 0, 0.1, 0.1)));
        Assert.Throws<ArgumentException>(() => cal.Set("enemy1", new HudRegion(0, 0, 0.1, 0.1))); // the scoreboard rows are gone
    }

    [Fact]
    public void Next_unset_wraps_around_and_is_null_when_every_box_is_set()
    {
        var cal = new RegionCalibration();
        foreach (var slot in RegionCalibration.Slots.Skip(1)) cal.Set(slot.Key, new HudRegion(0.1, 0.1, 0.1, 0.1));
        Assert.Equal(RegionCalibration.SoulsKey, cal.NextUnset(RegionCalibration.TimerKey));
        cal.Set(RegionCalibration.SoulsKey, new HudRegion(0.1, 0.1, 0.1, 0.1));
        Assert.Null(cal.NextUnset(RegionCalibration.SoulsKey));
        Assert.Equal(new[] { RegionCalibration.SoulsKey, RegionCalibration.TimerKey }, RegionCalibration.Slots.Select(s => s.Key));
    }

    [Fact]
    public void Regions_survive_the_settings_file_round_trip()
    {
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true };
        var settings = new ScoutSettings { Ocr = new OcrSettings { Regions = new HudRegions { Souls = new(0.47, 0.92, 0.06, 0.03), Timer = new(0.48, 0.01, 0.04, 0.03) } } };
        var back = System.Text.Json.JsonSerializer.Deserialize<ScoutSettings>(System.Text.Json.JsonSerializer.Serialize(settings, json), json)!;
        Assert.Equal(settings.Ocr.Regions.Souls, back.Ocr.Regions.Souls);
        Assert.Equal(settings.Ocr.Regions.Timer, back.Ocr.Regions.Timer);
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<ScoutSettings>("{}", json)!.Ocr.Regions.Souls);
    }

    [Fact]
    public void An_older_settings_file_with_enemy_rows_still_loads()
    {
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        const string old = """{"ocr":{"regions":{"souls":{"x":0.47,"y":0.92,"width":0.06,"height":0.03},"enemySouls":[null,{"x":0.62,"y":0.34,"width":0.05,"height":0.03}]}}}""";
        var back = System.Text.Json.JsonSerializer.Deserialize<ScoutSettings>(old, json)!;
        Assert.Equal(new HudRect(0.47, 0.92, 0.06, 0.03), back.Ocr.Regions.Souls);
        Assert.DoesNotContain("enemySouls", System.Text.Json.JsonSerializer.Serialize(back, json));
    }

    [Fact]
    public void Presets_at_16_9_are_the_measured_boxes_and_other_shapes_keep_the_hud_anchors()
    {
        var p1440 = HudPresets.For(2560, 1440)!;
        Assert.True(p1440.Measured);
        Assert.Equal(0.055859375, p1440.Souls.X, 9);
        Assert.Equal(0.8111111, p1440.Souls.Y, 6);
        Assert.Equal(0.0707031, p1440.Souls.Width, 6);
        Assert.Equal(0.487890625, p1440.Timer.X, 9);
        Assert.Equal(0.023046875, p1440.Timer.Width, 9);

        var p1080 = HudPresets.For(1920, 1080)!;           // the same shape, the same fractions
        Assert.True(p1080.Measured);
        Assert.Equal(p1440.Souls, p1080.Souls);
        Assert.Equal(p1440.Timer, p1080.Timer);

        var wide = HudPresets.For(3440, 1440)!;            // 21:9: same pixels from the left and the centre
        Assert.False(wide.Measured);
        Assert.Equal(143.0, wide.Souls.X * 3440, 6);
        Assert.Equal(181.0, wide.Souls.Width * 3440, 6);
        Assert.Equal(0.8111111, wide.Souls.Y, 6);
        Assert.Equal(3440 / 2.0 - 1.5, (wide.Timer.X + wide.Timer.Width / 2) * 3440, 6);
        Assert.False(HudPresets.For(1920, 1200)!.Measured); // 16:10
        Assert.Null(HudPresets.For(0, 1080));
    }

    [Fact]
    public void A_preset_fills_only_the_boxes_not_drawn_yet()
    {
        var mine = new HudRegion(0.05, 0.8, 0.08, 0.06);
        var cal = new RegionCalibration();
        cal.Set(RegionCalibration.SoulsKey, mine);
        var preset = HudPresets.For(1920, 1080)!;
        Assert.Equal(1, cal.FillFrom(preset));
        Assert.Equal(mine, cal.Get(RegionCalibration.SoulsKey));          // the drawn box stays
        Assert.Equal(preset.Timer, cal.Get(RegionCalibration.TimerKey));
        Assert.Equal(0, cal.FillFrom(preset));
    }

    [Fact]
    public void Boxes_carried_to_a_wider_window_keep_their_place_from_the_left_edge_and_the_centre()
    {
        var p = HudPresets.For(2560, 1440)!;
        var saved = new HudRegions { Souls = RegionCalibration.ToRect(p.Souls), Timer = RegionCalibration.ToRect(p.Timer), DrawnOn = new HudSize(2560, 1440) };

        Assert.False(HudCarry.Moves(saved, 1920, 1080));                 // the same shape: fractions already scale
        Assert.Same(saved, HudCarry.For(saved, 1920, 1080));
        Assert.True(HudCarry.Moves(saved, 3440, 1440));

        var wide = HudCarry.For(saved, 3440, 1440);
        var preset = HudPresets.For(3440, 1440)!;                         // the same rule as the presets
        Assert.Equal(preset.Souls, RegionCalibration.FromRect(wide.Souls));
        Assert.Equal(preset.Timer, RegionCalibration.FromRect(wide.Timer));
        Assert.Equal(new HudSize(3440, 1440), wide.DrawnOn);
        Assert.Equal(143.0, wide.Souls!.X * 3440, 6);                     // the same pixels from the left edge
        Assert.Equal(saved.Souls!.Y, wide.Souls.Y);                       // heights do not move

        var back = HudCarry.For(wide, 2560, 1440);                        // and back again
        Assert.Equal(saved.Souls.X, back.Souls!.X, 9);
        Assert.Equal(saved.Timer!.X, back.Timer!.X, 9);

        var old = saved with { DrawnOn = null };                          // saved before 0.5: used as they are
        Assert.False(HudCarry.Moves(old, 3440, 1440));
        Assert.Same(old, HudCarry.For(old, 3440, 1440));
    }

    [Fact]
    public void The_region_map_carries_live_and_says_so_once_per_window_shape()
    {
        var p = HudPresets.For(1920, 1080)!;
        var saved = new HudRegions { Souls = RegionCalibration.ToRect(p.Souls), Timer = RegionCalibration.ToRect(p.Timer), DrawnOn = new HudSize(1920, 1080) };
        (int, int)? window = (1920, 1080);
        var map = new SettingsHudRegionMap(() => saved, () => window);
        var said = new List<(int, int)>();
        map.Carried += (w, h) => said.Add((w, h));

        Assert.Equal(p.Souls, map.Souls);
        window = (2560, 1080);
        Assert.Equal(HudPresets.For(2560, 1080)!.Souls, map.Souls);
        Assert.Equal(HudPresets.For(2560, 1080)!.Timer, map.Timer);
        Assert.Equal(new[] { (2560, 1080) }, said);                       // once, not on every read
        window = null;                                                    // game not found: as saved
        Assert.Equal(p.Souls, map.Souls);
        window = (1920, 1080);
        Assert.Single(said);
    }

    [Fact]
    public void The_window_the_boxes_were_drawn_on_survives_the_settings_file()
    {
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var settings = new ScoutSettings { Ocr = new OcrSettings { Regions = new HudRegions { Souls = new(0.05, 0.8, 0.07, 0.05), DrawnOn = new HudSize(2560, 1440) } } };
        var back = System.Text.Json.JsonSerializer.Deserialize<ScoutSettings>(System.Text.Json.JsonSerializer.Serialize(settings, json), json)!;
        Assert.Equal(new HudSize(2560, 1440), back.Ocr.Regions.DrawnOn);
    }

    [Fact]
    public void The_overlay_palette_is_saved_by_name_and_defaults_to_normal()
    {
        var json = DeadlockScout.Core.Settings.SettingsBackup.Json;
        var text = System.Text.Json.JsonSerializer.Serialize(new ScoutSettings { Overlay = new OverlaySettings { Palette = OverlayPalette.ColourBlind } }, json);
        Assert.Contains("\"palette\": \"colourBlind\"", text.Replace("\":\"", "\": \""));
        Assert.Equal(OverlayPalette.ColourBlind, System.Text.Json.JsonSerializer.Deserialize<ScoutSettings>(text, json)!.Overlay.Palette);
        Assert.Equal(OverlayPalette.Normal, System.Text.Json.JsonSerializer.Deserialize<ScoutSettings>("{}", json)!.Overlay.Palette);
    }
}

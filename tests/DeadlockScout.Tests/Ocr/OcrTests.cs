using DeadlockScout.Core.Models;
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

    private sealed class FakeCapture : ICaptureSource
    {
        public bool IsAvailable => true;
        public Task<CapturedFrame?> CaptureAsync(CancellationToken ct) => Task.FromResult<CapturedFrame?>(new CapturedFrame(1920, 1080, 1920 * 4, new byte[16], DateTimeOffset.UtcNow));
    }

    private sealed class FakeEngine(Func<HudRegion, string?> read) : IOcrEngine
    {
        public string Name => "fake";
        public bool IsAvailable => true;
        public Task<string?> RecognizeAsync(CapturedFrame frame, HudRegion region, CancellationToken ct) => Task.FromResult(read(region));
    }

    private sealed class Regions : IHudRegionMap
    {
        public HudRegion? Souls { get; init; } = new(0.45, 0.02, 0.1, 0.04);
        public HudRegion? Timer { get; init; } = new(0.48, 0.0, 0.04, 0.02);
    }

    [Fact]
    public async Task Source_emits_a_sample_when_both_regions_read_and_tracks_health()
    {
        var regions = new Regions();
        var engine = new FakeEngine(r => r == regions.Souls ? "8,120" : "10:30");
        var src = new OcrSoulSource(new FakeCapture(), regions, engine, DeadlockScout.Core.Abstractions.SystemClock.Instance);
        var s = await src.TryReadOnceAsync(default);
        Assert.Equal(new SoulSample(new TimeSpan(0, 10, 30), 8120, SampleSource.Ocr), s);
        for (int i = 0; i < 5; i++) await src.TryReadOnceAsync(default);
        Assert.True(src.IsHealthy);

        var bad = new OcrSoulSource(new FakeCapture(), regions, new FakeEngine(_ => "??"), DeadlockScout.Core.Abstractions.SystemClock.Instance);
        Assert.Null(await bad.TryReadOnceAsync(default));
        Assert.False(bad.IsHealthy);
    }

    [Fact]
    public void Null_engine_leaves_the_source_unhealthy_so_the_feed_is_used()
    {
        var src = new OcrSoulSource(new NullCaptureSource(), new Regions(), new NullOcrEngine(), DeadlockScout.Core.Abstractions.SystemClock.Instance);
        Assert.False(src.IsHealthy);
    }

    [Fact]
    public void Region_conversion_from_1080p_settings()
    {
        var map = new SettingsHudRegionMap(() => new DeadlockScout.Core.Settings.HudRegions { Souls = new(960, 21.6, 192, 43.2) });
        Assert.Equal(new HudRegion(0.5, 0.02, 0.1, 0.04), map.Souls);
        Assert.Null(map.Timer);
        Assert.Equal((960, 21, 192, 43), map.Souls!.ToPixels(1920, 1080));
    }
}

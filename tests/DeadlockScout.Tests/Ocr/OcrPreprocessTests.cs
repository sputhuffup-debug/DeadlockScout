using DeadlockScout.Core.Models;
using DeadlockScout.Ocr;
using DeadlockScout.Ocr.Abstractions;

namespace DeadlockScout.Tests.Ocr;

public class OcrPreprocessTests
{
    /// <summary>A w×h crop of one background colour with a block of "text" colour in the middle third.</summary>
    private static CapturedFrame Crop(int w, int h, byte background, byte text)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool inText = x >= w / 3 && x < 2 * w / 3 && y >= h / 3 && y < 2 * h / 3;
                byte v = inText ? text : background;
                int i = (y * w + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = v;
                px[i + 3] = 0xFF;
            }
        return new CapturedFrame(w, h, w * 4, px, DateTimeOffset.UnixEpoch);
    }

    private static byte Gray(CapturedFrame f, int x, int y) => f.Bgra.Span[y * f.Stride + x * 4];

    [Fact]
    public void Light_hud_digits_on_dark_become_dark_on_white_enlarged_and_padded()
    {
        var hud = Crop(60, 16, background: 30, text: 230);
        var prepared = OcrPreprocess.Prepare(hud);

        int scale = 4; // ceil(64 / 16)
        Assert.Equal(60 * scale + 2 * OcrPreprocess.Padding, prepared.Width);
        Assert.Equal(16 * scale + 2 * OcrPreprocess.Padding, prepared.Height);
        Assert.Equal(prepared.Width * 4, prepared.Stride);
        Assert.Equal(255, Gray(prepared, 2, 2));                                       // padding
        Assert.True(Gray(prepared, OcrPreprocess.Padding + 4, OcrPreprocess.Padding + 4) > 200); // background → light
        Assert.True(Gray(prepared, prepared.Width / 2, prepared.Height / 2) < 50);     // text → dark
        Assert.Equal(DateTimeOffset.UnixEpoch, prepared.At);
    }

    [Fact]
    public void Dark_text_on_light_keeps_its_polarity_and_tall_crops_are_not_enlarged()
    {
        var light = Crop(200, 100, background: 220, text: 20);
        var prepared = OcrPreprocess.Prepare(light);
        Assert.Equal(200 + 2 * OcrPreprocess.Padding, prepared.Width); // already taller than the target
        Assert.True(Gray(prepared, OcrPreprocess.Padding + 2, OcrPreprocess.Padding + 2) > 200);
        Assert.True(Gray(prepared, prepared.Width / 2, prepared.Height / 2) < 50);
    }

    [Fact]
    public void Tiny_and_flat_crops_do_not_break_it()
    {
        var tiny = OcrPreprocess.Prepare(Crop(3, 2, 40, 40));
        Assert.Equal(3 * OcrPreprocess.MaxScale + 2 * OcrPreprocess.Padding, tiny.Width); // scale capped
        var empty = new CapturedFrame(0, 0, 0, Array.Empty<byte>(), DateTimeOffset.UnixEpoch);
        Assert.Same(empty, OcrPreprocess.Prepare(empty));
    }

    [Theory]
    [InlineData("souls", "12,480", "12,480")]
    [InlineData("enemy3", "9 050", "9,050")]
    [InlineData("timer", "14:2O", "14:20")]
    [InlineData("timer", "souls", null)]
    [InlineData("souls", "", null)]
    public void Calibration_shows_what_the_ocr_source_would_read(string key, string text, string? expected)
        => Assert.Equal(expected, RegionCalibration.ReadValue(key, text));

    private sealed class CountingCapture : IRegionCapture
    {
        public int Calls;
        public bool IsAvailable => true;
        public Task<CapturedFrame?> CaptureAsync(HudRegion region, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<CapturedFrame?>(new CapturedFrame(1, 1, 4, new byte[4], DateTimeOffset.UtcNow));
        }
    }

    private sealed class Engine : IOcrEngine
    {
        public string Name => "fake";
        public bool IsAvailable => true;
        public Task<string?> RecognizeAsync(CapturedFrame region, CancellationToken ct) => Task.FromResult<string?>("10:00");
    }

    private sealed class Regions : IHudRegionMap
    {
        public HudRegion? Souls => new(0.4, 0.9, 0.1, 0.04);
        public HudRegion? Timer => new(0.48, 0.0, 0.04, 0.02);
        public IReadOnlyList<HudRegion?> EnemySouls => Array.Empty<HudRegion?>();
    }

    [Fact]
    public async Task Nothing_is_captured_while_ocr_is_switched_off()
    {
        bool on = false;
        var capture = new CountingCapture();
        var src = new OcrSoulSource(capture, new Regions(), new Engine(), DeadlockScout.Core.Abstractions.SystemClock.Instance,
            interval: TimeSpan.FromMilliseconds(10), enabled: () => on);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
        var ctx = new MatchContext(1, 1, 1, DateTimeOffset.UtcNow);
        try { await foreach (var _ in src.ReadAsync(ctx, cts.Token)) { } } catch (OperationCanceledException) { }
        Assert.Equal(0, capture.Calls);

        on = true;
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var e = src.ReadAsync(ctx, cts2.Token).GetAsyncEnumerator(cts2.Token);
        Assert.True(await e.MoveNextAsync()); // switched on: it captures and reads
        Assert.True(capture.Calls >= 2);
    }
}

using DeadlockScout.Ocr;
using DeadlockScout.Ocr.Abstractions;

namespace DeadlockScout.Tests.Ocr;

/// <summary>
/// The souls box as the HUD fills it: the number large, a small label to its right, and with haunted souls a smaller
/// second line under it, the number moved up. Drawn with the test font, so the glyph reader can say what was kept.
/// </summary>
public class SoulsLineTests
{
    /// <summary>A dark box with each piece drawn where it says.</summary>
    internal static CapturedFrame BoxOf(int w, int h, params (CapturedFrame Piece, int X, int Y)[] pieces) => Box(w, h, pieces);

    private static CapturedFrame Box(int w, int h, params (CapturedFrame Piece, int X, int Y)[] pieces)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = 25; px[i * 4 + 3] = 255; }
        foreach (var (piece, ox, oy) in pieces)
        {
            var src = piece.Bgra.Span;
            for (int y = 0; y < piece.Height && oy + y < h; y++)
                for (int x = 0; x < piece.Width && ox + x < w; x++)
                {
                    int s = y * piece.Stride + x * 4, d = ((oy + y) * w + ox + x) * 4;
                    if (src[s] > 100) { px[d] = src[s]; px[d + 1] = src[s + 1]; px[d + 2] = src[s + 2]; } // ink only
                }
        }
        return new CapturedFrame(w, h, w * 4, px, DateTimeOffset.UnixEpoch);
    }

    private static GlyphSet Taught()
    {
        var set = new GlyphSet();
        foreach (var text in new[] { "12:34", "56:78", "9,0", "0.5" }) Assert.True(set.Learn(GlyphReaderTests.Draw(text, scale: 3), text));
        return set;
    }

    [Fact]
    public void The_label_beside_the_number_is_cut_off()
    {
        // "359" large, then a small "501" where the HUD writes SOULS (what Windows OCR made of it).
        var box = Box(160, 60, (GlyphReaderTests.Draw("359", scale: 3), 0, 10), (GlyphReaderTests.Draw("501", scale: 1), 80, 25));
        var only = SoulsLine.Isolate(box);
        Assert.Equal("359", Taught().Read(only));
        Assert.True(only.Width < 80);
    }

    [Fact]
    public void With_haunted_souls_the_raised_number_is_read_and_the_line_under_it_ignored()
    {
        var box = Box(170, 70,
            (GlyphReaderTests.Draw("2,340", scale: 3), 0, 0),       // moved up
            (GlyphReaderTests.Draw("501", scale: 1), 110, 15),      // the label
            (GlyphReaderTests.Draw("1,051", scale: 2), 0, 40));     // haunted souls, smaller, under it
        Assert.Equal("2,340", Taught().Read(SoulsLine.Isolate(box)));

        // Without haunted souls the same box holds the number lower down; still read.
        var plain = Box(170, 70, (GlyphReaderTests.Draw("2,340", scale: 3), 0, 20), (GlyphReaderTests.Draw("501", scale: 1), 110, 35));
        Assert.Equal("2,340", Taught().Read(SoulsLine.Isolate(plain)));
    }

    [Fact]
    public void A_box_with_nothing_to_cut_comes_back_as_it_was()
    {
        var blank = Box(40, 20);
        Assert.Same(blank, SoulsLine.Isolate(blank));
        var tight = GlyphReaderTests.Draw("768", scale: 2);
        Assert.Equal("768", Taught().Read(SoulsLine.Isolate(tight))); // a tight box only loses its margin
        var broken = new CapturedFrame(5, 1, 4, new byte[4], DateTimeOffset.UnixEpoch); // pixels short of its size
        Assert.Same(broken, SoulsLine.Isolate(broken));
    }

    [Fact]
    public void The_haunted_line_under_the_number_is_handed_back_and_none_without_it()
    {
        var box = Box(170, 70,
            (GlyphReaderTests.Draw("2,340", scale: 3), 0, 0),
            (GlyphReaderTests.Draw("1,051", scale: 2), 0, 40));
        var (number, below) = SoulsLine.Split(box);
        Assert.Equal("2,340", Taught().Read(number));
        Assert.NotNull(below);
        Assert.Equal("1,051", Taught().Read(below!));

        var plain = Box(170, 70, (GlyphReaderTests.Draw("2,340", scale: 3), 0, 20));
        Assert.Null(SoulsLine.Split(plain).Below);
    }

    [Theory]
    [InlineData("1,051 HAUNTED SOULS", 1051)]
    [InlineData("© 1,051 HAUNTED SOULS", 1051)]
    [InlineData("1.O51 Haunted Souls", 1051)]
    [InlineData("88 HAUNTED", 88)]
    [InlineData("1,051 SOULS", null)]        // not the haunted line
    [InlineData("grass and a rock", null)]   // the game world under the HUD
    [InlineData(null, null)]
    public void The_haunted_line_reads_only_when_it_says_haunted(string? text, int? expected)
    {
        Assert.Equal(expected, DeadlockScout.Ocr.HudParser.ParseHaunted(text));
    }

    private sealed class WordsEngine(string? digits, string? words) : IOcrEngine
    {
        public int WordReads;
        public string Name => "test";
        public bool IsAvailable => true;
        public Task<string?> RecognizeAsync(CapturedFrame region, CancellationToken ct) => Task.FromResult(digits);
        public Task<string?> RecognizeWordsAsync(CapturedFrame region, CancellationToken ct) { WordReads++; return Task.FromResult(words); }
    }

    [Fact]
    public async Task The_glyph_reader_sends_words_to_windows_ocr_without_counting_them()
    {
        var windows = new WordsEngine("2,340", "1,051 HAUNTED SOULS");
        var book = new GlyphBook(); book.Souls.LoadFrom(Taught());
        var engine = new GlyphOcrEngine(book, windows);
        Assert.Equal("1,051 HAUNTED SOULS", await engine.RecognizeWordsAsync(GlyphReaderTests.Draw("1,051"), default));
        Assert.Equal((1, 0L, 0L), (windows.WordReads, engine.GlyphReads, engine.FallbackReads));
    }

    private sealed class BoxCapture(CapturedFrame souls, HudRegion soulsRegion) : IRegionCapture
    {
        public bool IsAvailable => true;
        public Task<CapturedFrame?> CaptureAsync(HudRegion region, CancellationToken ct) =>
            Task.FromResult<CapturedFrame?>(region == soulsRegion ? souls : new CapturedFrame(10, 5, 40, new byte[200], DateTimeOffset.UnixEpoch));
    }

    private sealed class Map(HudRegion souls) : IHudRegionMap
    {
        public HudRegion? Souls => souls;
        public HudRegion? Timer { get; } = new(0.48, 0.0, 0.04, 0.02);
        public IReadOnlyList<HudRegion?> EnemySouls => Array.Empty<HudRegion?>();
    }

    [Fact]
    public async Task The_source_counts_haunted_souls_as_unspent_souls()
    {
        var soulsRegion = new HudRegion(0.45, 0.92, 0.1, 0.06);
        var box = Box(170, 70, (GlyphReaderTests.Draw("2,340", scale: 3), 0, 0), (GlyphReaderTests.Draw("1,051", scale: 2), 0, 40));
        var engine = new HeightEngine();
        var src = new OcrSoulSource(new BoxCapture(box, soulsRegion), new Map(soulsRegion), engine, DeadlockScout.Core.Abstractions.SystemClock.Instance);
        DeadlockScout.Core.Models.SoulSample? sample = null;
        for (int i = 0; i < DeadlockScout.Core.Meter.OcrClockGuard.Confirm; i++) sample = await src.TryReadOnceAsync(default);
        Assert.Equal(2_340 + 1_051, sample!.Souls);
        Assert.Equal(1_051, src.LastHaunted);
        Assert.True(engine.WordReads > 0);
    }

    /// <summary>The souls count is the tall crop, the clock the small one; words are always the haunted line.</summary>
    private sealed class HeightEngine : IOcrEngine
    {
        public int WordReads;
        public string Name => "test";
        public bool IsAvailable => true;
        public Task<string?> RecognizeAsync(CapturedFrame region, CancellationToken ct) => Task.FromResult<string?>(region.Height > 20 ? "2,340" : "10:30");
        public Task<string?> RecognizeWordsAsync(CapturedFrame region, CancellationToken ct) { WordReads++; return Task.FromResult<string?>("1,051 HAUNTED SOULS"); }
    }

    /// <summary>Windows OCR stand-in: reads any box with a fully taught set of the test font.</summary>
    private sealed class FontOcr : IOcrEngine
    {
        private readonly GlyphSet _set = Taught();
        public int Calls;
        public string Name => "test OCR";
        public bool IsAvailable => true;
        public Task<string?> RecognizeAsync(CapturedFrame region, CancellationToken ct) { Calls++; return Task.FromResult(_set.Read(region)); }
    }

    private sealed class SequenceCapture(HudRegion soulsRegion, Func<int, (string Souls, string Clock)> at) : IRegionCapture
    {
        public int Pass = -1;
        public bool IsAvailable => true;
        public Task<CapturedFrame?> CaptureAsync(HudRegion region, CancellationToken ct)
        {
            if (region == soulsRegion) Pass++;
            var (souls, clock) = at(Pass);
            return Task.FromResult<CapturedFrame?>(region == soulsRegion ? GlyphReaderTests.Draw(souls, scale: 3) : GlyphReaderTests.Draw(clock, scale: 2));
        }
    }

    [Fact]
    public async Task During_a_match_the_souls_count_learns_its_own_digits_from_checked_readings()
    {
        var soulsRegion = new HudRegion(0.45, 0.92, 0.1, 0.06);
        string[] souls = { "1,234", "1,256", "1,278", "1,290", "1,345", "1,367", "1,389", "1,406" };
        var capture = new SequenceCapture(soulsRegion, i => (souls[Math.Min(i, souls.Length - 1)], $"10:{30 + i:00}"));
        var windows = new FontOcr();
        var engine = new GlyphOcrEngine(new GlyphBook(), windows);
        var time = new TestClock(new DateTimeOffset(2026, 11, 4, 2, 0, 0, TimeSpan.Zero));
        var src = new OcrSoulSource(capture, new Map(soulsRegion), engine, time);
        for (int i = 0; i < souls.Length; i++) { await src.TryReadOnceAsync(default); time.Advance(TimeSpan.FromSeconds(1)); }

        Assert.True(engine.Glyphs.Souls.HasAllDigits); // 0–9 all seen in the souls count
        Assert.True(engine.Glyphs.Clock.Known.Length > 0);
        Assert.Equal("1,406", engine.Glyphs.Souls.Read(GlyphReaderTests.Draw("1,406", scale: 3)));
    }
}

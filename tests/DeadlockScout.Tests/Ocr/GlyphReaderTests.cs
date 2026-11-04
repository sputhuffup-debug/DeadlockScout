using DeadlockScout.Ocr;
using DeadlockScout.Ocr.Abstractions;

namespace DeadlockScout.Tests.Ocr;

/// <summary>Synthetic HUD boxes: a 5×7 pixel font drawn light on dark, at any scale, so the reader is tested without a screen.</summary>
public class GlyphReaderTests
{
    // 5 wide, 9 tall: rows 0–6 are the line, 7–8 the space below it (for the comma's tail).
    private static readonly Dictionary<char, string[]> Font = new()
    {
        ['0'] = new[] { ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###.", ".....", "....." },
        ['1'] = new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###.", ".....", "....." },
        ['2'] = new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####", ".....", "....." },
        ['3'] = new[] { "####.", "....#", "....#", ".###.", "....#", "....#", "####.", ".....", "....." },
        ['4'] = new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#.", ".....", "....." },
        ['5'] = new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###.", ".....", "....." },
        ['6'] = new[] { "..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###.", ".....", "....." },
        ['7'] = new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#...", ".....", "....." },
        ['8'] = new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###.", ".....", "....." },
        ['9'] = new[] { ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##..", ".....", "....." },
        [':'] = new[] { ".....", "..#..", "..#..", ".....", "..#..", "..#..", ".....", ".....", "....." },
        ['.'] = new[] { ".....", ".....", ".....", ".....", ".....", ".##..", ".##..", ".....", "....." },
        [','] = new[] { ".....", ".....", ".....", ".....", ".....", ".##..", ".##..", "..#..", ".#..." },
        ['*'] = new[] { "#.#.#", ".###.", "#####", ".###.", "#.#.#", ".....", ".....", ".....", "....." }, // the soul icon, say
    };

    /// <summary>Draws <paramref name="text"/> at <paramref name="scale"/>, light text on a dark box with a margin.</summary>
    internal static CapturedFrame Draw(string text, int scale = 2, int gap = 1, byte ink = 235, byte background = 25, int noiseSeed = 0)
    {
        int glyphW = 5 * scale, gapW = gap * scale, margin = 3 * scale;
        int w = margin * 2 + text.Length * glyphW + Math.Max(0, text.Length - 1) * gapW, h = margin * 2 + 9 * scale;
        var px = new byte[w * h * 4];
        var random = new Random(noiseSeed);
        for (int i = 0; i < w * h; i++)
        {
            byte v = (byte)Math.Clamp(background + (noiseSeed == 0 ? 0 : random.Next(-12, 13)), 0, 255);
            px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = v;
            px[i * 4 + 3] = 255;
        }
        for (int n = 0; n < text.Length; n++)
        {
            var rows = Font[text[n]];
            int left = margin + n * (glyphW + gapW);
            for (int r = 0; r < 9; r++)
                for (int c = 0; c < 5; c++)
                {
                    if (rows[r][c] != '#') continue;
                    for (int dy = 0; dy < scale; dy++)
                        for (int dx = 0; dx < scale; dx++)
                        {
                            int i = ((margin + r * scale + dy) * w + left + c * scale + dx) * 4;
                            byte v = (byte)Math.Clamp(ink + (noiseSeed == 0 ? 0 : random.Next(-12, 13)), 0, 255);
                            px[i] = px[i + 1] = px[i + 2] = v;
                        }
                }
        }
        return new CapturedFrame(w, h, w * 4, px, DateTimeOffset.UnixEpoch);
    }

    /// <summary>A set taught the way the calibration tool does: a few clock and souls readings.</summary>
    private static GlyphSet Taught(int scale = 2)
    {
        var set = new GlyphSet();
        foreach (var text in new[] { "12:34", "56:78", "9,0", "0.5" }) Assert.True(set.Learn(Draw(text, scale), text));
        return set;
    }

    [Fact]
    public void The_segmenter_cuts_one_glyph_per_character()
    {
        Assert.Equal(5, GlyphSegmenter.Segment(Draw("12:34")).Count);
        Assert.Equal(5, GlyphSegmenter.Segment(Draw("9,850")).Count);
        Assert.Empty(GlyphSegmenter.Segment(Draw("")));
    }

    [Fact]
    public void A_taught_set_reads_new_numbers_and_clocks()
    {
        var set = Taught();
        Assert.True(set.HasAllDigits);
        Assert.Equal("0123456789:.,", set.Known);
        Assert.Equal("47:03", set.Read(Draw("47:03")));
        Assert.Equal("9,850", set.Read(Draw("9,850")));
        Assert.Equal("31.6", set.Read(Draw("31.6")));
        Assert.Equal(9850, HudParser.ParseSouls(set.Read(Draw("9,850"))));
    }

    [Fact]
    public void It_reads_at_another_size_and_through_noise()
    {
        var set = Taught(scale: 2);
        Assert.Equal("28:19", set.Read(Draw("28:19", scale: 3)));
        Assert.Equal("6,402", set.Read(Draw("6,402", scale: 2, noiseSeed: 7)));
        Assert.Equal("6,402", set.Read(Draw("6,402", scale: 2, ink: 20, background: 230))); // dark text on light reads too
    }

    [Fact]
    public void A_glyph_it_does_not_know_makes_the_whole_read_fail()
    {
        var set = Taught();
        Assert.Null(set.Read(Draw("*768")));   // the soul icon in the box: Windows OCR gets the read instead
        var partial = new GlyphSet();
        partial.Learn(Draw("12:34"), "12:34");
        Assert.False(partial.HasAllDigits);
        Assert.Null(partial.Read(Draw("56")));   // 5 and 6 not learned yet
        Assert.Null(new GlyphSet().Read(Draw("12")));
    }

    [Fact]
    public void Learning_refuses_a_box_that_does_not_match_its_text()
    {
        var set = new GlyphSet();
        Assert.False(set.Learn(Draw("*768"), "768"));     // an extra glyph: the icon
        Assert.False(set.Learn(Draw("768"), "7,689"));   // a misread
        Assert.False(set.Learn(Draw("768"), "76B"));     // a character outside the alphabet
        Assert.Equal("", set.Known);
        Assert.True(set.Learn(Draw("768"), "7 6 8"));    // spaces in the text are ignored
    }

    [Fact]
    public void The_set_survives_a_save_and_a_load()
    {
        var set = Taught();
        var back = GlyphSet.FromJson(set.ToJson());
        Assert.Equal(set.Known, back.Known);
        Assert.Equal("47:03", back.Read(Draw("47:03")));
        Assert.Equal("", GlyphSet.FromJson("{ not json").Known);
        Assert.Equal("", GlyphSet.FromJson(null).Known);
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var set = Taught();
        set.Clear();
        Assert.Null(set.Read(Draw("12")));
        Assert.Equal("", set.Known);
    }

    private sealed class CountingOcr(string? answer) : IOcrEngine
    {
        public int Calls;
        public string Name => "Windows OCR (test)";
        public bool IsAvailable => true;
        public Task<string?> RecognizeAsync(CapturedFrame region, CancellationToken ct) { Calls++; return Task.FromResult(answer); }
    }

    [Fact]
    public async Task The_engine_reads_with_the_glyphs_and_never_asks_windows_ocr_while_they_match()
    {
        var windows = new CountingOcr("wrong");
        var engine = new GlyphOcrEngine(Taught(), windows);
        Assert.True(engine.GlyphsActive);
        Assert.Equal("47:03", await engine.RecognizeAsync(Draw("47:03"), default));
        Assert.Equal("9,850", await engine.RecognizeAsync(Draw("9,850"), default));
        Assert.Equal(0, windows.Calls);
        Assert.Equal(2, engine.GlyphReads);
        Assert.StartsWith("ready", engine.Status);
    }

    [Fact]
    public async Task A_box_the_glyphs_cannot_read_goes_to_windows_ocr()
    {
        var windows = new CountingOcr("768");
        var engine = new GlyphOcrEngine(Taught(), windows);
        Assert.Equal("768", await engine.RecognizeAsync(Draw("*768"), default));
        Assert.Equal((1, 0L, 1L), (windows.Calls, engine.GlyphReads, engine.FallbackReads));
    }

    [Fact]
    public async Task Until_every_digit_is_known_or_when_switched_off_windows_ocr_reads_everything()
    {
        var partial = new GlyphSet();
        partial.Learn(Draw("12:34"), "12:34");
        var windows = new CountingOcr("12:34");
        var learning = new GlyphOcrEngine(partial, windows);
        Assert.False(learning.GlyphsActive);
        await learning.RecognizeAsync(Draw("12:34"), default);
        Assert.StartsWith("learning, knows 4 of 10 digits", learning.Status);

        var off = new GlyphOcrEngine(Taught(), windows, enabled: () => false);
        await off.RecognizeAsync(Draw("12:34"), default);
        Assert.Equal(2, windows.Calls);
        Assert.Equal("off", off.Status);
        Assert.Equal("Windows OCR (test)", off.Name);
    }

    [Fact]
    public async Task Most_recent_reads_falling_back_asks_for_a_recalibration()
    {
        var engine = new GlyphOcrEngine(Taught(), new CountingOcr("768"));
        for (int i = 0; i < GlyphOcrEngine.RecentReads; i++)
            await engine.RecognizeAsync(Draw(i % 3 == 0 ? "47:03" : "*768"), default); // a third read, two thirds fall back
        Assert.True(engine.NeedsRecalibration);
        Assert.Contains("teach it again", engine.Status);
    }
}

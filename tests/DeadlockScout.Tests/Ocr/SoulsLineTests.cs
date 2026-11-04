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
}

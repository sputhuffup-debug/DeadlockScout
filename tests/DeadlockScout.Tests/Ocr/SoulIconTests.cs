using DeadlockScout.Ocr;
using DeadlockScout.Ocr.Abstractions;

namespace DeadlockScout.Tests.Ocr;

/// <summary>The soul icon left of the souls count ('*' in the test font): learned from readings, then cut off.</summary>
public class SoulIconTests
{
    private static CapturedFrame Draw(string text) => GlyphReaderTests.Draw(text);

    private static GlyphSet Digits()
    {
        var set = new GlyphSet();
        foreach (var t in new[] { "12:34", "56:78", "9,0" }) Assert.True(set.Learn(Draw(t), t));
        return set;
    }

    [Fact]
    public void An_extra_glyph_left_of_the_text_three_times_is_the_icon_and_is_cut_off()
    {
        var set = Digits();
        Assert.Null(set.Read(Draw("*768"))); // unknown glyph: the read fails, as before
        Assert.False(set.ObserveLeading(Draw("*768"), "768"));
        Assert.False(set.ObserveLeading(Draw("*770"), "770"));
        Assert.True(set.ObserveLeading(Draw("*792"), "792"));
        Assert.NotNull(set.Icon);
        Assert.Equal("768", set.Read(Draw("*768")));
        Assert.Equal("1,406", set.Read(Draw("*1,406")));
        Assert.Equal("768", set.Read(Draw("768"))); // a box drawn without the icon still reads
    }

    [Fact]
    public void A_dropped_digit_is_never_taken_for_the_icon()
    {
        // Windows OCR left out a leading 1 three times: the extra glyph is a known digit, so nothing is cut.
        var set = Digits();
        for (int i = 0; i < GlyphSet.IconVotes; i++) set.ObserveLeading(Draw("1768"), "768");
        Assert.Equal("1768", set.Read(Draw("1768")));
        // Glyphs that differ each time are no icon either.
        var other = Digits();
        other.ObserveLeading(Draw("*768"), "768");
        other.ObserveLeading(Draw("5768"), "768");
        other.ObserveLeading(Draw("*768"), "768");
        Assert.Null(other.Icon);
    }

    [Fact]
    public void Learning_a_box_with_the_icon_learns_its_digits_too()
    {
        // From nothing: the readings name the digits only ("$" or nothing for the icon).
        var set = new GlyphSet();
        string[] seen = { "1,234", "5,678", "9,012", "3,456", "7,890" };
        foreach (var t in seen) set.LearnBox(Draw("*" + t), "$" + t);
        Assert.NotNull(set.Icon);
        Assert.True(set.HasAllDigits);
        Assert.Equal("4,321", set.Read(Draw("*4,321")));
        Assert.Equal("", new string(set.Known.Where(c => c == '$' || c == '*').ToArray())); // the icon is no character
    }

    [Fact]
    public async Task The_engine_learns_the_icon_from_its_readings_and_then_reads_without_windows_ocr()
    {
        var book = new GlyphBook();
        book.Souls.LoadFrom(Digits());
        var windows = new CountingOcr("768");
        var engine = new GlyphOcrEngine(book, windows);
        Assert.Equal("768", await engine.RecognizeAsync(Draw("*768"), OcrField.Souls, default)); // icon unknown: Windows OCR
        for (int i = 0; i < GlyphSet.IconVotes; i++) engine.LearnFromReading(OcrField.Souls, Draw("*768"), "768");
        Assert.NotNull(book.Souls.Icon);
        int before = windows.Calls;
        Assert.Equal("792", await engine.RecognizeAsync(Draw("*792"), OcrField.Souls, default));
        Assert.Equal(before, windows.Calls);
        Assert.Null(book.Clock.Icon); // the clock box never learned one
    }

    [Fact]
    public void The_icon_survives_a_save()
    {
        var set = Digits();
        for (int i = 0; i < GlyphSet.IconVotes; i++) set.ObserveLeading(Draw("*768"), "768");
        var back = GlyphSet.FromJson(set.ToJson());
        Assert.NotNull(back.Icon);
        Assert.Equal("768", back.Read(Draw("*768")));
        back.Clear();
        Assert.Null(back.Icon);
    }

    [Fact]
    public void The_outline_marks_the_count_the_icon_and_the_haunted_line()
    {
        var set = Digits();
        for (int i = 0; i < GlyphSet.IconVotes; i++) set.ObserveLeading(Draw("*768"), "768");
        var box = SoulsLineTests.BoxOf(170, 70, (GlyphReaderTests.Draw("*2,340", scale: 2), 0, 0), (GlyphReaderTests.Draw("1,051", scale: 1), 0, 40));
        var outlined = SoulsLine.Outline(box, set);
        var (number, below) = SoulsLine.Locate(box);
        Assert.NotNull(number);
        Assert.NotNull(below);
        (byte B, byte G, byte R) At(int x, int y) { var p = outlined.Bgra.Span; int i = y * outlined.Stride + x * 4; return (p[i], p[i + 1], p[i + 2]); }
        Assert.Equal((0x5A, 0x5A, 0xF0), At(below!.Value.X0, below.Value.Y0));     // red: haunted line
        Assert.Contains(Enumerable.Range(number!.Value.X0, 8), x => At(x, number.Value.Y0) == (0x80, 0x80, 0x80)); // grey: the icon at the left
        Assert.Equal((0xF5, 0xA8, 0x5F), At(number.Value.X1, number.Value.Y1));     // blue: the count
        var blank = SoulsLineTests.BoxOf(20, 10);
        Assert.Same(blank, SoulsLine.Outline(blank, set)); // nothing to outline
    }

    private sealed class CountingOcr(string? answer) : IOcrEngine
    {
        public int Calls;
        public string Name => "Windows OCR (test)";
        public bool IsAvailable => true;
        public Task<string?> RecognizeAsync(CapturedFrame region, CancellationToken ct) { Calls++; return Task.FromResult(answer); }
    }
}

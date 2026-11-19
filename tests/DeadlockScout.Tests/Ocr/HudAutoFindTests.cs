using DeadlockScout.Ocr;
using DeadlockScout.Ocr.Abstractions;

namespace DeadlockScout.Tests.Ocr;

public class HudAutoFindTests
{
    [Fact]
    public void Candidates_start_at_the_preset_and_stay_inside_the_window()
    {
        var preset = new HudRegion(0.48, 0.0, 0.03, 0.02);
        var all = HudAutoFind.Candidates(preset);
        Assert.Equal(preset, all[0]);
        Assert.True(all.Count >= 50);
        Assert.All(all, b => Assert.True(b.X >= 0 && b.Y >= 0 && b.X + b.Width <= 1.000001 && b.Y + b.Height <= 1.000001));
    }

    [Fact]
    public async Task It_keeps_the_first_box_that_reads_a_steady_clock_and_leaves_an_unreadable_one_for_the_manual_tool()
    {
        var preset = HudPresets.For(2560, 1080)!;                   // 21:9, estimated
        var truth = HudAutoFind.Candidates(preset.Timer)[7];       // the real clock sits a little off the estimate
        var now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        var boxes = new Dictionary<int, HudRegion>();
        int reads = 0;

        CapturedFrame? Capture(HudRegion r)
        {
            int id = boxes.Count + 1;
            boxes[id] = r;
            return new CapturedFrame(id, 1, id * 4, ReadOnlyMemory<byte>.Empty, now); // empty pixels: passed on as they are
        }
        Task<string?> Read(CapturedFrame f, OcrField field, CancellationToken _)
        {
            reads++;
            var box = boxes[f.Width];
            if (field == OcrField.Clock && box == truth) return Task.FromResult<string?>($"12:{(now - new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero)).Seconds + 10:00}");
            if (field == OcrField.Clock && box.Equals(preset.Timer)) return Task.FromResult<string?>("1:");        // half the clock: not clean
            return Task.FromResult<string?>(null);
        }
        Task Wait(TimeSpan t, CancellationToken _) { now += t; return Task.CompletedTask; }

        // The glyph reader's cut: one shape per character in the true box; a near box with a sliver of the HUD in it reads
        // the same clock cleanly, yet cuts into one shape more, and is passed over.
        var sliver = HudAutoFind.Candidates(preset.Timer)[3];
        int Shapes(CapturedFrame f) => boxes[f.Width] == sliver ? 6 : 5;
        Task<string?> ReadWithSliver(CapturedFrame f, OcrField field, CancellationToken ct)
            => field == OcrField.Clock && boxes[f.Width] == sliver ? Read(f, field, ct).ContinueWith(_ => (string?)"12:10") : Read(f, field, ct);
        var found = await HudAutoFind.FindAsync(preset, Capture, ReadWithSliver, Wait, CancellationToken.None, Shapes);
        Assert.Equal(truth, found.Timer.Box);
        Assert.Contains("12:1", found.Timer.Reading);              // a confirming reading of the true box
        Assert.Null(found.Souls.Box);
        Assert.True(reads > 50);
    }

    [Fact]
    public void A_box_must_cut_into_one_glyph_per_character_the_souls_count_may_add_its_icon()
    {
        Assert.True(HudAutoFind.Cuts(OcrField.Clock, 5, "12:34"));
        Assert.False(HudAutoFind.Cuts(OcrField.Clock, 6, "12:34"));
        Assert.True(HudAutoFind.Cuts(OcrField.Souls, 6, "12,480"));
        Assert.True(HudAutoFind.Cuts(OcrField.Souls, 7, "12,480"));          // the soul icon not learned yet
        Assert.False(HudAutoFind.Cuts(OcrField.Souls, 8, "12,480"));
        Assert.False(HudAutoFind.Cuts(OcrField.Clock, 0, null));
    }

    [Fact]
    public void Diagnostics_say_why_the_glyphs_fell_back()
    {
        Assert.Contains("cuts into 7 shapes where Windows OCR reads 5 characters", GlyphOcrEngine.MissReason(OcrField.Clock, 7, false, "12:34"));
        Assert.Contains("teach this box again", GlyphOcrEngine.MissReason(OcrField.Clock, 5, true, "12:34"));
        Assert.Null(GlyphOcrEngine.MissReason(OcrField.Clock, 0, false, null));     // nothing on screen
    }
}

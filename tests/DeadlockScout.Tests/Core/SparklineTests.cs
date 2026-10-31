using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

public class SparklineTests
{
    /// <summary>A curve expecting a steady 500 souls a minute.</summary>
    private static PaceCurve Curve() => new(13, RankBracket.All, "v1",
        Enumerable.Range(0, 9).Select(i => new PacePoint(i * 5, null, null, null, i * 5 * 500)).ToList(), 40, "test");

    private static SoulRateMeter Meter(int untilSeconds, Func<int, double> spm)
    {
        var m = new SoulRateMeter(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(20));
        double souls = 0;
        for (int s = 0; s <= untilSeconds; s += 10)
        {
            m.Add(new SoulSample(TimeSpan.FromSeconds(s), (int)souls, SampleSource.LiveFeed));
            souls += spm(s) / 6; // 10 s of farming
        }
        return m;
    }

    [Fact]
    public void Points_cover_the_last_ten_minutes_every_30_seconds_ending_now()
    {
        var points = PaceMath.Sparkline(Meter(900, _ => 550), Curve(), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30));
        Assert.Equal(21, points.Count);                            // 5:00 .. 15:00
        Assert.Equal(TimeSpan.FromMinutes(5), points[0].GameTime);
        Assert.Equal(TimeSpan.FromMinutes(15), points[^1].GameTime);
        Assert.All(points, p => Assert.Equal(0.1, p.Delta, 2));   // 550 against 500: +10% throughout
    }

    [Fact]
    public void A_slump_shows_as_a_dip()
    {
        // On pace, then half pace from 9:00 to 11:00, then on pace again.
        var points = PaceMath.Sparkline(Meter(900, s => s is >= 540 and < 660 ? 250 : 500), Curve(), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30));
        var low = points.MinBy(p => p.Delta);
        Assert.InRange(low.Delta, -0.55, -0.4);
        Assert.Equal(TimeSpan.FromMinutes(11), low.GameTime);    // the 2-minute window is all slump at 11:00
        Assert.Equal(0, points[^1].Delta, 2);                     // recovered by 15:00
    }

    [Fact]
    public void Early_game_starts_one_window_in_and_an_empty_meter_has_no_line()
    {
        var early = PaceMath.Sparkline(Meter(180, _ => 500), Curve(), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromMinutes(2), early[0].GameTime); // not from 0:00, where a short window is jumpy
        Assert.Equal(3, early.Count);                             // 2:00, 2:30, 3:00
        Assert.Empty(PaceMath.Sparkline(new SoulRateMeter(), Curve(), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30)));
    }

    [Theory]
    [InlineData(5, 2, 0.25)]
    [InlineData(15, 4, 0.40)]
    [InlineData(32, 6, 0.60)]
    public void The_window_and_band_widen_with_the_phase(int minute, int windowMinutes, double range)
    {
        var scale = SparkScale.At(TimeSpan.FromMinutes(minute), TimeSpan.FromMinutes(2));
        Assert.Equal(TimeSpan.FromMinutes(windowMinutes), scale.Window);
        Assert.Equal(range, scale.Range);
        // A longer window chosen in the settings is never shortened.
        Assert.Equal(TimeSpan.FromMinutes(5), SparkScale.At(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)).Window);
        Assert.Equal("±40% · 4m", SparkScale.At(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(2)).Label);
    }

    [Fact]
    public void Past_the_band_the_line_keeps_rising_but_never_reaches_the_edge()
    {
        var scale = new SparkScale(TimeSpan.FromMinutes(4), 0.40);
        Assert.Equal(0.4, scale.Position(0.20), 3);                       // half the band: linear
        Assert.Equal(SparkScale.Band, scale.Position(0.40), 3);           // the band's edge
        Assert.Equal(-SparkScale.Band, scale.Position(-0.40), 3);
        double twice = scale.Position(0.80), thrice = scale.Position(1.20);
        Assert.True(twice > SparkScale.Band && thrice > twice && thrice < 1); // a bigger spike still reads bigger
        Assert.True(scale.Position(10) < 1);
    }

    [Fact]
    public void A_late_kill_no_longer_pins_the_line_to_the_edge()
    {
        // 500 a minute, on pace, with a 1,500-soul kill at 28:00; read at 28:30.
        var meter = Meter(1710, s => s == 1680 ? 500 + 1500 * 6 : 500);
        var late = SparkScale.At(TimeSpan.FromSeconds(1710), meter.Window);

        var before = PaceMath.Sparkline(meter, Curve(), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30))[^1].Delta;
        Assert.True(before > 1);                                          // +150% over 2 minutes: off any band

        var after = PaceMath.Sparkline(meter, Curve(), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30), late.Window)[^1].Delta;
        Assert.Equal(0.5, after, 2);                                       // +50% over 6 minutes
        Assert.InRange(late.Position(after), 0.6, SparkScale.Band);        // inside the late band, clear of the edge
    }

    [Fact]
    public void The_meter_reads_any_window_on_request()
    {
        var meter = Meter(900, s => s < 600 ? 500 : 800);
        Assert.Equal(800, meter.ReadAt(TimeSpan.FromMinutes(15))!.WindowSpm, 0);                          // its own 2 minutes
        Assert.Equal(650, meter.ReadAt(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(10))!.WindowSpm, 0); // half and half
    }
}

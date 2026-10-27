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
}

using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

public class CurveBuilderTests
{
    [Fact]
    public void Bucket_means_become_a_monotone_curve_anchored_at_zero()
    {
        var curve = CurveBuilder.FromBucketMeans(5, RankBracket.All, "w1", "analytics", new[]
        {
            (17, 15000.0, 300), (12, 9000.0, 120), (22, 14000.0, 10), (27, 0.0, 0),
        });
        Assert.Equal(0, curve.Points[0].Minute);
        Assert.Equal(new[] { 0, 12, 17, 22 }, curve.Points.Select(p => p.Minute));
        Assert.Equal(new[] { 0, 9000, 15000, 15000 }, curve.Points.Select(p => p.Mean)); // 22-min dip clamped
        Assert.Equal(430, curve.SampleSize);
        Assert.False(curve.HasPercentiles);
    }

    [Fact]
    public void Series_become_percentiles_per_minute_until_too_few_matches_remain()
    {
        var series = new List<IReadOnlyList<NetWorthPoint>>();
        for (int i = 0; i < 8; i++)
        {
            // match i earns (500 + 50 i) per minute and lasts 10 + i minutes
            var s = new List<NetWorthPoint>();
            for (int m = 0; m <= 10 + i; m++) s.Add(new(TimeSpan.FromMinutes(m), (500 + 50 * i) * m));
            series.Add(s);
        }
        var curve = CurveBuilder.FromSeries(1, RankBracket.All, "w1", "metadata", series, minMatches: 5);
        Assert.True(curve.HasPercentiles);
        Assert.Equal(8, curve.SampleSize);
        // minute 10: all 8 running; values 5000..8500 step 500 -> P50 = 6750, mean 6750
        var m10 = curve.Points.Single(p => p.Minute == 10);
        Assert.Equal(6750, m10.P50);
        Assert.Equal(6750, m10.Mean);
        Assert.Equal(5875, m10.P25);
        Assert.Equal(7625, m10.P75);
        // after minute 13 only 4 matches remain (i >= 4 lasts >= 14 min), so the curve stops at 13
        Assert.Equal(13, curve.Points[^1].Minute);
    }

    [Fact]
    public void Quantile_interpolates()
    {
        var v = new double[] { 1, 2, 3, 4 };
        Assert.Equal(1.75, CurveBuilder.Quantile(v, 0.25), 6);
        Assert.Equal(2.5, CurveBuilder.Quantile(v, 0.5), 6);
        Assert.Equal(4, CurveBuilder.Quantile(v, 1), 6);
        Assert.Equal(7, CurveBuilder.Quantile(new double[] { 7 }, 0.9), 6);
    }
}

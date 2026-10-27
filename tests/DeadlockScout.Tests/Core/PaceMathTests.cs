using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

public class PaceMathTests
{
    private static PaceCurve MeanCurve() => new(1, RankBracket.All, "w1",
        new[] { new PacePoint(0, null, null, null, 0), new PacePoint(10, null, null, null, 6000), new PacePoint(20, null, null, null, 14000) }, 100, "test");

    private static PaceCurve PctCurve() => new(1, RankBracket.All, "w1",
        new[]
        {
            new PacePoint(0, 0, 0, 0, 0),
            new PacePoint(10, 5000, 6000, 7000, 6000),
            new PacePoint(20, 12000, 14000, 16000, 14000),
        }, 40, "metadata");

    [Fact]
    public void Net_worth_interpolates_and_clamps()
    {
        var c = MeanCurve();
        Assert.Equal(3000, PaceMath.NetWorthAt(c, TimeSpan.FromMinutes(5)), 3);
        Assert.Equal(10000, PaceMath.NetWorthAt(c, TimeSpan.FromMinutes(15)), 3);
        Assert.Equal(14000, PaceMath.NetWorthAt(c, TimeSpan.FromMinutes(30)), 3);
        Assert.Equal(0, PaceMath.NetWorthAt(c, TimeSpan.Zero), 3);
    }

    [Fact]
    public void Expected_spm_uses_the_same_window_as_the_meter()
    {
        var c = MeanCurve();
        // 10..20 min slope is 800/min
        Assert.Equal(800, PaceMath.ExpectedSpm(c, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(2)), 3);
        // first minute: window from match start
        Assert.Equal(600, PaceMath.ExpectedSpm(c, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)), 3);
    }

    [Fact]
    public void Percentile_is_null_without_quartiles_and_piecewise_with_them()
    {
        Assert.Null(PaceMath.Percentile(MeanCurve(), TimeSpan.FromMinutes(10), 6000));
        var c = PctCurve();
        Assert.Equal(50, PaceMath.Percentile(c, TimeSpan.FromMinutes(10), 6000)!.Value, 3);
        Assert.Equal(25, PaceMath.Percentile(c, TimeSpan.FromMinutes(10), 5000)!.Value, 3);
        Assert.Equal(75, PaceMath.Percentile(c, TimeSpan.FromMinutes(10), 7000)!.Value, 3);
        Assert.Equal(62.5, PaceMath.Percentile(c, TimeSpan.FromMinutes(10), 6500)!.Value, 3);
        Assert.InRange(PaceMath.Percentile(c, TimeSpan.FromMinutes(10), 20000)!.Value, 90, 99);
        Assert.InRange(PaceMath.Percentile(c, TimeSpan.FromMinutes(10), 0)!.Value, 1, 10);
    }

    [Fact]
    public void Evaluate_and_format_match_the_design_doc_line()
    {
        var c = MeanCurve();
        var reading = new MeterReading(TimeSpan.FromMinutes(15), 10500, 840, 700, TimeSpan.FromMinutes(2), SampleSource.LiveFeed, 10);
        var e = PaceMath.Evaluate(c, reading);
        Assert.Equal(800, e.ExpectedSpm, 3);
        Assert.Equal(0.05, e.Delta!.Value, 3);
        Assert.Null(e.Percentile);
        Assert.Equal("Haze · 15:00 · 840 spm (2m) · pace 800 · +5%", PaceMath.FormatLine("Haze", e, TimeSpan.FromMinutes(2)));

        var e2 = PaceMath.Evaluate(PctCurve(), reading with { Souls = 10000 });
        Assert.EndsWith("50th pct", PaceMath.FormatLine("Haze", e2, TimeSpan.FromMinutes(2)));
    }

    [Theory]
    [InlineData(1, "1st")] [InlineData(2, "2nd")] [InlineData(3, "3rd")] [InlineData(11, "11th")]
    [InlineData(12, "12th")] [InlineData(13, "13th")] [InlineData(21, "21st")] [InlineData(58, "58th")] [InlineData(100, "100th")]
    public void Ordinals(int n, string expected) => Assert.Equal(expected, PaceMath.Ordinal(n));
}

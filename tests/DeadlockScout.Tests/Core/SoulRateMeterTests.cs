using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

public class SoulRateMeterTests
{
    private static SoulSample S(int seconds, int souls, SampleSource src = SampleSource.LiveFeed)
        => new(TimeSpan.FromSeconds(seconds), souls, src);

    [Fact]
    public void Empty_meter_has_no_reading()
    {
        var m = new SoulRateMeter();
        Assert.Null(m.Read());
    }

    [Fact]
    public void Before_window_fills_rate_is_measured_from_match_start()
    {
        var m = new SoulRateMeter();
        m.Add(S(60, 600));
        var r = m.Read()!;
        Assert.Equal(TimeSpan.FromSeconds(60), r.WindowUsed);
        Assert.Equal(600, r.WindowSpm, 3);
        Assert.Equal(600, r.OverallSpm, 3);
    }

    [Fact]
    public void Two_minute_rate_is_difference_over_two_minutes()
    {
        var m = new SoulRateMeter();
        m.Add(S(0, 0));
        m.Add(S(600, 6000));   // 10:00
        m.Add(S(720, 7400));   // 12:00: +1400 in 2 min
        var r = m.Read()!;
        Assert.Equal(TimeSpan.FromSeconds(120), r.WindowUsed);
        Assert.Equal(700, r.WindowSpm, 3);
        Assert.Equal(7400 / 12.0, r.OverallSpm, 3);
    }

    [Fact]
    public void Window_start_is_interpolated_between_samples()
    {
        var m = new SoulRateMeter();
        m.Add(S(540, 5000));   // 9:00
        m.Add(S(660, 6200));   // 11:00
        m.Add(S(720, 7000));   // 12:00 -> window start 10:00 interpolates to 5600
        var r = m.Read()!;
        Assert.Equal((7000 - 5600) / 2.0, r.WindowSpm, 3);
    }

    [Fact]
    public void Out_of_order_and_duplicate_samples_are_handled()
    {
        var m = new SoulRateMeter();
        m.Add(S(120, 1200));
        m.Add(S(60, 500));
        m.Add(S(120, 1250));   // replaces
        Assert.Equal(2, m.Count);
        Assert.Equal(1250, m.Latest!.Souls);
        Assert.Equal(500, m.InterpolateAt(TimeSpan.FromSeconds(60)), 3);
    }

    [Fact]
    public void Souls_never_decrease_so_a_lower_later_value_is_rejected()
    {
        var m = new SoulRateMeter();
        m.Add(S(100, 1000));
        m.Add(S(110, 900, SampleSource.Ocr)); // misread
        Assert.Equal(1, m.Count);
    }

    [Fact]
    public void Samples_older_than_retention_are_dropped()
    {
        var m = new SoulRateMeter(retention: TimeSpan.FromMinutes(10));
        for (int s = 0; s <= 1500; s += 30) m.Add(S(s, s * 10));
        Assert.True(m.Count <= 21 + 1);
        Assert.True(m.Latest!.GameTime - TimeSpan.FromMinutes(10) <= TimeSpan.FromSeconds(900 + 30));
        var r = m.Read()!;
        Assert.Equal(600, r.WindowSpm, 3); // constant 10 souls/s
    }

    [Fact]
    public void Mixed_sources_share_one_timeline()
    {
        var m = new SoulRateMeter();
        m.Add(S(600, 6000, SampleSource.LiveFeed));
        m.Add(S(675, 6900, SampleSource.Ocr));
        m.Add(S(720, 7500, SampleSource.Ocr));
        var r = m.Read()!;
        Assert.Equal(SampleSource.Ocr, r.Source);
        Assert.Equal(750, r.WindowSpm, 3);
    }

    [Fact]
    public void Rejects_bad_configuration()
    {
        Assert.Throws<ArgumentException>(() => new SoulRateMeter(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SoulRateMeter(TimeSpan.Zero));
    }
}

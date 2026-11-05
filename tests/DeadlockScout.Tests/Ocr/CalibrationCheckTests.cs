using DeadlockScout.Ocr;

namespace DeadlockScout.Tests.Ocr;

public class CalibrationCheckTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const string Souls = RegionCalibration.SoulsKey;
    private const string Timer = RegionCalibration.TimerKey;

    [Theory]
    [InlineData("Souls: 768", "Souls:")]
    [InlineData("768 9", "9")]
    [InlineData("12,480", "")]
    [InlineData(" 12 480 ", "")]
    [InlineData("l2480", "")]
    public void SoulsLeftover_is_what_the_box_read_besides_the_number(string text, string expected)
        => Assert.Equal(expected, HudParser.SoulsLeftover(text));

    [Theory]
    [InlineData("14:20", "")]
    [InlineData("14:20 x", "x")]
    [InlineData("◆ 3:05", "◆")]
    public void ClockLeftover_is_what_the_box_read_besides_the_clock(string text, string expected)
        => Assert.Equal(expected, HudParser.ClockLeftover(text));

    [Fact]
    public void Text_that_is_not_a_number_is_bad()
    {
        var check = new CalibrationCheck();
        var r = check.Observe(Souls, "—", T0);
        Assert.Equal(CalibrationLevel.Bad, r.Level);
        Assert.Contains("not a number", r.Text);
        Assert.Equal(CalibrationLevel.Bad, check.Observe(Souls, "  ", T0).Level);
        Assert.Contains("not a clock", check.Observe(Timer, "768", T0).Text);
    }

    [Fact]
    public void A_clean_number_is_good_and_steady_after_three_readings()
    {
        var check = new CalibrationCheck();
        var first = check.Observe(Souls, "9,850", T0);
        Assert.Equal(CalibrationLevel.Good, first.Level);
        Assert.Equal("Reads “9,850” → 9,850", first.Text);
        Assert.False(first.Steady);                       // too soon to teach the glyph reader from
        check.Observe(Souls, "9,850", T0.AddSeconds(1));
        var third = check.Observe(Souls, "9,880", T0.AddSeconds(2));
        Assert.Equal(CalibrationLevel.Good, third.Level);
        Assert.Equal("Reads “9,880” → 9,880 · steady", third.Text);
    }

    [Fact]
    public void Extra_characters_in_the_box_are_a_warning()
    {
        var check = new CalibrationCheck();
        var r = check.Observe(Souls, "768 9", T0);
        Assert.Equal(CalibrationLevel.Warn, r.Level);
        Assert.Contains("but also “9”", r.Text);
        Assert.Equal(CalibrationLevel.Warn, check.Observe(Timer, "12:51 ▲", T0).Level);
    }

    [Fact]
    public void A_soul_count_that_jumps_is_a_warning()
    {
        // Oct 31, 2026: the soul icon was read as a digit now and then, so 768 came back as 7,689.
        var check = new CalibrationCheck();
        check.Observe(Souls, "768", T0);
        var r = check.Observe(Souls, "7,689", T0.AddSeconds(1));
        Assert.Equal(CalibrationLevel.Warn, r.Level);
        Assert.Contains("768 → 7,689", r.Text);
        // Still flagged while the jump is among the recent readings, even once the box reads right again.
        Assert.Equal(CalibrationLevel.Warn, check.Observe(Souls, "770", T0.AddSeconds(2)).Level);
    }

    [Fact]
    public void A_soul_count_that_drops_by_more_than_a_misread_is_a_warning()
    {
        var check = new CalibrationCheck();
        check.Observe(Souls, "7,689", T0);
        Assert.Equal(CalibrationLevel.Warn, check.Observe(Souls, "768", T0.AddSeconds(1)).Level);
    }

    [Fact]
    public void Farming_is_not_a_jump()
    {
        var check = new CalibrationCheck();
        check.Observe(Souls, "1,000", T0);
        check.Observe(Souls, "1,250", T0.AddSeconds(1)); // a camp
        var r = check.Observe(Souls, "1,600", T0.AddSeconds(2)); // a kill
        Assert.Equal(CalibrationLevel.Good, r.Level);
    }

    [Fact]
    public void A_clock_that_does_not_follow_itself_is_a_warning()
    {
        var check = new CalibrationCheck();
        check.Observe(Timer, "12:51", T0);
        var r = check.Observe(Timer, "22:52", T0.AddSeconds(1));
        Assert.Equal(CalibrationLevel.Warn, r.Level);
        Assert.Contains("12:51 → 22:52", r.Text);
    }

    [Fact]
    public void A_ticking_clock_is_good()
    {
        var check = new CalibrationCheck();
        check.Observe(Timer, "12:51", T0);
        check.Observe(Timer, "12:52", T0.AddSeconds(1));
        var r = check.Observe(Timer, "12:53", T0.AddSeconds(2));
        Assert.Equal(new CalibrationReading(CalibrationLevel.Good, "Reads “12:53” → 12:53 · steady", Steady: true), r);
    }

    [Fact]
    public void Reset_forgets_the_readings_of_a_moved_box()
    {
        var check = new CalibrationCheck();
        check.Observe(Souls, "768", T0);
        check.Observe(Souls, "7,689", T0.AddSeconds(1));
        check.Reset(Souls);
        Assert.Equal(CalibrationLevel.Good, check.Observe(Souls, "7,690", T0.AddSeconds(2)).Level);
    }

    [Fact]
    public void Old_readings_age_out()
    {
        var check = new CalibrationCheck();
        check.Observe(Souls, "768", T0);
        var later = T0 + CalibrationCheck.MaxAge + TimeSpan.FromSeconds(1);
        Assert.Equal(CalibrationLevel.Good, check.Observe(Souls, "3,500", later).Level);
    }

    [Fact]
    public void Boxes_are_judged_apart()
    {
        var check = new CalibrationCheck();
        check.Observe(Souls, "768", T0);
        Assert.Equal(CalibrationLevel.Good, check.Observe(RegionCalibration.TimerKey, "14:20", T0.AddSeconds(1)).Level);
    }
}

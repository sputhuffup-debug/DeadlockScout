using DeadlockScout.Core.Meter;

namespace DeadlockScout.Tests.Core;

public class OcrClockGuardTests
{
    private static readonly DateTimeOffset W0 = new(2026, 11, 1, 4, 30, 0, TimeSpan.Zero);
    private static TimeSpan T(int m, int s) => new(0, m, s);

    /// <summary>Reads clock values one real second apart, starting at <paramref name="startSecond"/>; returns what was accepted.</summary>
    private static List<bool> Read(OcrClockGuard g, int startSecond, params TimeSpan[] clocks)
        => clocks.Select((c, i) => g.Accept(c, W0.AddSeconds(startSecond + i))).ToList();

    private static OcrClockGuard Started(TimeSpan at, int second = 0)
    {
        var g = new OcrClockGuard();
        for (int i = 0; i < OcrClockGuard.Confirm; i++) g.Accept(at + TimeSpan.FromSeconds(i), W0.AddSeconds(second + i));
        Assert.NotNull(g.Last);
        return g;
    }

    [Fact]
    public void A_first_clock_needs_a_few_readings_in_a_row_that_follow_each_other()
    {
        var g = new OcrClockGuard();
        Assert.Equal(new[] { false, false, true, true }, Read(g, 0, T(12, 0), T(12, 1), T(12, 2), T(12, 3)));

        // A wrong first reading does not start the clock: the run begins again from the reading that broke it.
        var h = new OcrClockGuard();
        Assert.Equal(new[] { false, false, false, true, true }, Read(h, 0, T(22, 0), T(12, 1), T(12, 2), T(12, 3), T(12, 4)));
        Assert.Equal(T(12, 4), h.Last!.Value.Clock);
        Assert.Equal(0, h.Rejected);                                   // confirming a first clock is not rejecting misreads
    }

    [Fact]
    public void A_reading_ahead_of_real_time_or_behind_the_last_one_is_a_misread()
    {
        var g = Started(T(12, 50));                                   // 12:50, 12:51, 12:52 at seconds 0, 1, 2
        Assert.Equal(new[] { true, false, true, false, true },
            Read(g, 3, T(12, 53), T(22, 54), T(12, 55), T(2, 56), T(12, 57)));   // 22:54 and 2:56 are OCR slips
        Assert.Equal(2, g.Rejected);
        Assert.Equal(T(12, 57), g.Last!.Value.Clock);
    }

    [Fact]
    public void A_paused_game_and_a_gap_in_reading_are_both_fine()
    {
        var g = Started(T(20, 0));                                    // 20:00..20:02 at seconds 0..2
        Assert.Equal(new[] { true, true, true }, Read(g, 3, T(20, 2), T(20, 2), T(20, 2)));   // paused: the clock stands still
        // Alt-tabbed for five minutes: the clock moved on by about as much real time (20:02 at second 5, 300 s later).
        Assert.True(g.Accept(T(25, 4), W0.AddSeconds(5 + 300)));
        // ...but never further than the real time allows.
        Assert.False(g.Accept(T(35, 0), W0.AddSeconds(5 + 301)));
    }

    [Fact]
    public void A_clock_that_really_moved_is_taken_after_the_resync_time_but_a_single_slip_never_is()
    {
        var g = Started(T(10, 0));                                    // 10:00..10:02 at seconds 0..2
        // The clock really jumped back (a reconnect, a replay of the match start): readings follow each other from 5:00.
        var results = Enumerable.Range(0, 40).Select(i => g.Accept(T(5, 0) + TimeSpan.FromSeconds(i), W0.AddSeconds(3 + i))).ToList();
        int first = results.IndexOf(true);
        Assert.InRange(first, (int)OcrClockGuard.Resync.TotalSeconds - 3, (int)OcrClockGuard.Resync.TotalSeconds + 1);
        Assert.All(results.Skip(first), Assert.True);

        // A lone slip in the middle of a good clock never becomes the clock, however long it has been.
        var h = Started(T(10, 0));
        Assert.False(h.Accept(T(40, 0), W0.AddSeconds(200)));
        Assert.True(h.Accept(T(13, 18), W0.AddSeconds(201)));
    }

    [Fact]
    public void Reset_starts_over_for_the_next_match()
    {
        var g = Started(T(38, 0));
        g.Reset();
        Assert.Null(g.Last);
        Assert.Equal(new[] { false, false, true }, Read(g, 100, T(0, 0), T(0, 1), T(0, 2)));
    }

    /// <summary>Reads like the soul source does: accept, else try the one-digit correction.</summary>
    private static TimeSpan? ReadFixed(OcrClockGuard g, TimeSpan read, DateTimeOffset now)
        => g.Accept(read, now) ? read : g.Correct(read, now) is { } fixedClock && g.Accept(fixedClock, now) ? fixedClock : null;

    [Fact]
    public void A_clock_one_digit_off_is_put_right()
    {
        // The 0.2.1 logs: "20:01" when 29:01 was due (9 read as 0), "23:36" for 27:36 (7 as 3), "6:27" for 6:17 (1 as 2).
        var g = Started(T(29, 0), second: 0);                         // 29:00, 29:01, 29:02 at seconds 0–2
        Assert.Equal(T(29, 3), ReadFixed(g, T(20, 3), W0.AddSeconds(3)));
        var h = Started(T(27, 33));
        Assert.Equal(T(27, 36), ReadFixed(h, T(23, 36), W0.AddSeconds(3)));
        var k = Started(T(6, 14));
        Assert.Equal(T(6, 17), ReadFixed(k, T(6, 27), W0.AddSeconds(3)));
        Assert.Equal((1, 0), (g.Corrected, g.Rejected));
    }

    [Fact]
    public void Only_one_digit_off_and_never_without_a_good_clock()
    {
        var g = Started(T(12, 50));
        Assert.Null(ReadFixed(g, T(28, 64 - 60), W0.AddSeconds(3)));  // 28:04 for 12:53: three digits off
        Assert.Equal(1, g.Rejected);
        Assert.Null(new OcrClockGuard().Correct(T(20, 1), W0));       // nothing to compare with yet
        // A reading the rule accepts as it is is never "corrected".
        Assert.Equal(T(12, 54), ReadFixed(g, T(12, 54), W0.AddSeconds(4)));
        Assert.Equal(0, g.Corrected);
        g.Reset();
        Assert.Equal(0, g.Corrected);
    }
}

using DeadlockScout.Core.Laning;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Tests.Core;

public class LaningTimersTests
{
    private static TimeSpan T(int m, int s = 0) => new(0, m, s);
    private static readonly DateTimeOffset W0 = new(2026, 10, 29, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Rows_count_down_turn_to_push_now_inside_the_lead_and_stay_up_briefly()
    {
        var settings = new TimerSettings();
        var early = SpawnTimers.Rows(T(1, 10), settings);
        Assert.Equal(new[] { "Small camps", "Crates", "Medium camps · Midtown crates" }, early.Select(r => r.Names));
        Assert.Equal(TimerState.Later, early[0].State);
        Assert.Equal(T(0, 50), early[0].In);

        var push = SpawnTimers.Rows(T(1, 31), settings);
        Assert.Equal(TimerState.PushNow, push[0].State);

        var up = SpawnTimers.Rows(T(2, 10), settings);
        Assert.Equal((TimerState.Up, "Small camps"), (up[0].State, up[0].Names));
        Assert.Equal("Crates", SpawnTimers.Rows(T(2, 21), settings)[0].Names);   // gone 20 s after its spawn

        Assert.Equal("Large camps · Sinner's Sacrifice", SpawnTimers.Rows(T(6), settings).Single().Names);
        Assert.Empty(SpawnTimers.Rows(T(8, 30), settings));
    }

    [Fact]
    public void The_screen_clock_wins_then_the_game_log_anchor_then_the_broadcast_plus_its_delay()
    {
        var clock = new LiveClock();
        Assert.Null(clock.Read(W0, TimeSpan.FromSeconds(75)));

        clock.ObserveFeed(T(5), W0);                                     // the broadcast says 5:00 as it arrives
        Assert.Equal((T(6, 25), ClockSource.Broadcast), clock.Read(W0.AddSeconds(10), TimeSpan.FromSeconds(75))!.Value);

        clock.SetAnchor(W0 - T(6, 20));                                  // the game started 6:20 ago by the console log
        Assert.Equal((T(6, 20), ClockSource.GameLog), clock.Read(W0, TimeSpan.FromSeconds(75))!.Value);
        Assert.Equal(T(1, 20), clock.MeasuredDelay(W0));

        clock.ObserveScreen(T(6, 18), W0);                               // OCR of the HUD
        Assert.Equal((T(6, 20), ClockSource.Screen), clock.Read(W0.AddSeconds(2), TimeSpan.Zero)!.Value);
        Assert.Equal(ClockSource.GameLog, clock.Read(W0.AddSeconds(6), TimeSpan.Zero)!.Value.Source);   // OCR went stale
    }

    [Fact]
    public void An_anchor_that_puts_the_broadcast_ahead_of_the_game_is_dropped()
    {
        var clock = new LiveClock();
        clock.SetAnchor(W0 - T(2));                                      // a reconnect's game-state line, not the start
        clock.ObserveFeed(T(12), W0);
        Assert.Null(clock.Anchor);
        Assert.Equal(ClockSource.Broadcast, clock.Read(W0, TimeSpan.FromSeconds(75))!.Value.Source);

        clock.SetAnchor(W0 - T(13, 15));                                 // only the first anchor of a match counts...
        clock.SetAnchor(W0 - T(1));
        Assert.Equal(W0 - T(13, 15), clock.Anchor);                      // ...once a wrong one is dropped, a new one is taken
    }

    [Fact]
    public void A_paused_hud_clock_stops_the_clock_instead_of_running_on()
    {
        var clock = new LiveClock();
        clock.ObserveScreen(T(3, 26), W0);
        clock.ObserveScreen(T(3, 27), W0.AddSeconds(1));
        Assert.Equal(T(3, 27) + TimeSpan.FromMilliseconds(500), clock.Read(W0.AddSeconds(1.5), TimeSpan.Zero)!.Value.GameTime);   // running: extrapolated
        // Paused at 3:27: the HUD reads 3:27 every second.
        for (int i = 2; i <= 30; i++) clock.ObserveScreen(T(3, 27), W0.AddSeconds(i));
        Assert.True(clock.Paused(W0.AddSeconds(30.5)));
        Assert.Equal((T(3, 27), ClockSource.Screen), clock.Read(W0.AddSeconds(30.5), TimeSpan.Zero)!.Value);
        // Unpaused: the HUD moves again and so does the clock.
        clock.ObserveScreen(T(3, 28), W0.AddSeconds(31));
        Assert.False(clock.Paused(W0.AddSeconds(31.5)));
        Assert.Equal(T(3, 28) + TimeSpan.FromMilliseconds(500), clock.Read(W0.AddSeconds(31.5), TimeSpan.Zero)!.Value.GameTime);
    }

    [Fact]
    public void A_pause_on_the_broadcast_holds_the_broadcast_clock_and_comes_off_the_game_log_clock()
    {
        var delay = TimeSpan.FromSeconds(30);
        var feedOnly = new LiveClock();
        feedOnly.ObserveFeed(T(3, 0), W0);
        for (int i = 1; i <= 20; i++) feedOnly.ObserveFeed(T(3, 0), W0.AddSeconds(i));   // the broadcast clock stands at 3:00
        Assert.True(feedOnly.Paused(W0.AddSeconds(20)));
        Assert.Equal((T(3, 0), ClockSource.Broadcast), feedOnly.Read(W0.AddSeconds(20), delay)!.Value);

        // With the game-log anchor: a 60 s pause seen on the broadcast comes off its clock, while it lasts and after.
        var clock = new LiveClock();
        clock.SetAnchor(W0 - T(3, 30));                                    // 0:00 was 3:30 ago: the broadcast runs 30 s behind
        clock.ObserveFeed(T(3, 0), W0);
        for (int i = 1; i <= 60; i++) clock.ObserveFeed(T(3, 0), W0.AddSeconds(i));
        Assert.Equal(T(3, 30), clock.Read(W0.AddSeconds(60), delay)!.Value.GameTime);   // held through the pause
        clock.ObserveFeed(T(3, 1), W0.AddSeconds(61));                     // the broadcast moves again
        Assert.False(clock.Paused(W0.AddSeconds(61)));
        Assert.Equal(T(3, 31), clock.Read(W0.AddSeconds(61), delay)!.Value.GameTime);   // 4:31 since 0:00, less the 60 s pause
        Assert.Equal(TimeSpan.FromSeconds(30), clock.MeasuredDelay(W0.AddSeconds(61)));
    }

    [Fact]
    public void Ticks_at_the_same_game_time_within_a_moment_are_no_pause()
    {
        var clock = new LiveClock();
        for (int p = 0; p < 12; p++) clock.ObserveFeed(T(3, 0), W0.AddMilliseconds(p));  // twelve players' rows of one tick
        clock.ObserveFeed(T(3, 0) + TimeSpan.FromMilliseconds(16), W0.AddMilliseconds(20));
        Assert.False(clock.Paused(W0.AddSeconds(1)));
    }
}

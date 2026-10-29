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
}

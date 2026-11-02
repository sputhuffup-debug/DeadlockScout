using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Tests.Core;

public class LiveGoalsTests
{
    private static TimeSpan T(int m, int s = 0) => new(0, m, s);
    private static readonly TargetSettings All = new() { LaningSpm = 420, MaxLaningDeaths = 1, FixPhase = GamePhase.Mid };

    [Fact]
    public void Laning_pace_says_on_track_or_what_the_rest_of_laning_needs()
    {
        // 6:00 with 2,700 souls: 450 a minute so far; 4,200 by 10:00 needs 375 from here.
        Assert.Equal("Goals: on track for 420 spm by 10:00", LiveGoals.Line(All with { MaxLaningDeaths = null }, T(6), 2700, null, null));
        // 6:00 with 2,100: 2,100 more in four minutes is 525 a minute.
        Assert.Equal("Goals: need 525 spm to 10:00 for 420", LiveGoals.Line(All with { MaxLaningDeaths = null }, T(6), 2100, null, null));
        Assert.Equal("Goals: laning goal of 420 spm met", LiveGoals.Line(All with { MaxLaningDeaths = null }, T(9, 30), 4300, null, null));
        // Too early to say anything about pace.
        Assert.Null(LiveGoals.Line(All with { MaxLaningDeaths = null }, T(0, 40), 100, null, null));
    }

    [Fact]
    public void Laning_deaths_count_down_to_the_goal()
    {
        var t = new TargetSettings { MaxLaningDeaths = 2 };
        Assert.Equal("Goals: 2 laning deaths left", LiveGoals.Line(t, T(3), 900, 0, null));
        Assert.Equal("Goals: one laning death left", LiveGoals.Line(t, T(5), 1900, 1, null));
        Assert.Equal("Goals: no laning deaths left", LiveGoals.Line(t, T(7), 2600, 2, null));
        Assert.Equal("Goals: 3 laning deaths, 2 was the goal", LiveGoals.Line(t, T(8), 3000, 3, null));
        Assert.Null(LiveGoals.Line(t, T(8), 3000, null, null));   // the feed has not said
    }

    [Fact]
    public void Laning_goals_go_at_ten_minutes_and_the_phase_to_fix_shows_while_it_is_on()
    {
        var mid = new PhaseResult(GamePhase.Mid, T(10), T(14), 2800, 3000);   // 7% behind the gate so far
        Assert.Equal("Goals: mid game to fix: −7% on the gate", LiveGoals.Line(All, T(14), 9000, 2, mid));
        var ahead = mid with { Gained = 3150 };
        Assert.Equal("Goals: mid game to fix: +5% on the gate", LiveGoals.Line(All, T(14), 9000, 2, ahead));
        // In laning, both laning parts.
        Assert.Equal("Goals: on track for 420 spm by 10:00 · one laning death left", LiveGoals.Line(All, T(6), 2700, 0, null));
    }

    [Fact]
    public void Nothing_set_says_nothing()
    {
        Assert.Null(LiveGoals.Line(new TargetSettings(), T(6), 2700, 0, null));
        var laning = new PhaseResult(GamePhase.Laning, T(0), T(6), 2700, 2500);
        Assert.Null(LiveGoals.Line(new TargetSettings { FixPhase = GamePhase.Mid }, T(6), 2700, 0, laning)); // not the phase to fix
    }
}

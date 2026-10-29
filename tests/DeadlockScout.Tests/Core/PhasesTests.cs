using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

public class PhasesTests
{
    /// <summary>A rank curve gaining <paramref name="spm"/> souls a minute, steady, out to 45 minutes.</summary>
    private static PaceCurve Steady(int spm)
        => new(13, RankGoal.Bracket(8), "p", Enumerable.Range(0, 10).Select(i => new PacePoint(i * 5, null, null, null, i * 5 * spm)).ToList(), 500, "analytics");

    /// <summary>Your souls a minute: <paramref name="lane"/> for the first 10, <paramref name="mid"/> to 20, <paramref name="late"/> after.</summary>
    private static List<NetWorthPoint> Souls(int minutes, int lane, int mid, int late)
    {
        var pts = new List<NetWorthPoint>();
        int nw = 0;
        for (int m = 0; m <= minutes; m++)
        {
            pts.Add(new NetWorthPoint(TimeSpan.FromMinutes(m), nw));
            nw += m < 10 ? lane : m < 20 ? mid : late;
        }
        return pts;
    }

    [Fact]
    public void Each_phase_compares_your_gain_with_the_curve_over_the_same_minutes()
    {
        var phases = Phases.Of(Souls(34, 660, 540, 700), TimeSpan.FromMinutes(34), Steady(600));
        Assert.Equal(new[] { GamePhase.Laning, GamePhase.Mid, GamePhase.Late }, phases.Select(p => p.Phase));
        Assert.Equal(6600, phases[0].Gained);
        Assert.Equal(6000, phases[0].Expected, 3);
        Assert.Equal(0.10, phases[0].Delta!.Value, 3);
        Assert.Equal(-0.10, phases[1].Delta!.Value, 3);
        Assert.Equal(TimeSpan.FromMinutes(34), phases[2].To);
        Assert.Equal(700, phases[2].Spm, 3);
    }

    [Fact]
    public void A_late_game_shorter_than_two_minutes_is_left_out()
    {
        var phases = Phases.Of(Souls(21, 600, 600, 600), TimeSpan.FromMinutes(21), Steady(600));
        Assert.Equal(new[] { GamePhase.Laning, GamePhase.Mid }, phases.Select(p => p.Phase));
    }

    [Fact]
    public void The_late_game_stops_where_the_curve_does()
    {
        var phases = Phases.Of(Souls(60, 600, 600, 600), TimeSpan.FromMinutes(60), Steady(600));
        Assert.Equal(TimeSpan.FromMinutes(45), phases[^1].To);
        Assert.Equal(0, phases[^1].Delta!.Value, 3);
    }

    [Fact]
    public void Totals_pool_the_games_and_the_weakest_needs_three_games_behind()
    {
        var curve = Steady(600);
        var games = Enumerable.Range(0, 3).Select(_ => Phases.Of(Souls(30, 600, 480, 660), TimeSpan.FromMinutes(30), curve)).ToList();
        var totals = Phases.Total(games);
        Assert.Equal(3, totals.Count);
        Assert.All(totals, t => Assert.Equal(3, t.Games));
        Assert.Equal(-0.20, totals[1].Delta!.Value, 3);
        Assert.Equal(1200, totals[1].ShortfallPerGame, 3);
        Assert.Equal(GamePhase.Mid, Phases.Weakest(totals)!.Phase);

        Assert.Null(Phases.Weakest(Phases.Total(games.Take(2))));                     // two games are not enough
        var ahead = Phases.Total(new[] { Phases.Of(Souls(30, 700, 700, 700), TimeSpan.FromMinutes(30), curve) });
        Assert.Null(Phases.Weakest(ahead));                                           // nothing behind
    }

    /// <summary>A meter fed every 30 s from <paramref name="fromMinute"/> to <paramref name="toMinute"/> by <see cref="Souls"/>' rates.</summary>
    private static SoulRateMeter Meter(double fromMinute, double toMinute, int lane, int mid, int late)
    {
        var meter = new SoulRateMeter(TimeSpan.FromMinutes(2), TimeSpan.FromHours(2));
        var souls = Souls((int)Math.Ceiling(toMinute) + 1, lane, mid, late);
        for (double m = fromMinute; m <= toMinute + 1e-9; m += 0.5)
            meter.Add(new SoulSample(TimeSpan.FromMinutes(m), (int)CurveBuilder.InterpolateSeries(souls, TimeSpan.FromMinutes(m)), SampleSource.LiveFeed));
        return meter;
    }

    [Fact]
    public void Live_phases_cover_the_match_so_far_with_the_current_one_up_to_now()
    {
        var live = Phases.Live(Meter(0.5, 14, 660, 540, 700), Steady(600));
        Assert.Equal(new[] { GamePhase.Laning, GamePhase.Mid }, live.Select(p => p.Phase));
        Assert.Equal(0.10, live[0].Delta!.Value, 2);
        Assert.Equal(TimeSpan.FromMinutes(14), live[1].To);
        Assert.Equal(-0.10, live[1].Delta!.Value, 2);

        // 4 minutes of mid game at 540 against 600: 240 souls short, to make up over the 6 minutes left.
        Assert.Equal(640, Phases.CatchUpSpm(live[1], Steady(600))!.Value, 0);
        Assert.Null(Phases.CatchUpSpm(live[0], Steady(600)));                   // laning is over
    }

    [Fact]
    public void Live_phases_leave_out_what_the_app_did_not_see_and_a_phase_under_a_minute_old()
    {
        var joined = Phases.Live(Meter(12, 25, 600, 600, 600), Steady(600));    // started the app at 12:00
        Assert.Equal(new[] { GamePhase.Late }, joined.Select(p => p.Phase));

        var fresh = Phases.Live(Meter(0.5, 10.5, 600, 600, 600), Steady(600));  // mid game 30 s old
        Assert.Equal(new[] { GamePhase.Laning }, fresh.Select(p => p.Phase));
    }
}

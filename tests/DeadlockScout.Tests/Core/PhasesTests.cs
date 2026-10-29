using DeadlockScout.Core.Benchmarks;
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
}

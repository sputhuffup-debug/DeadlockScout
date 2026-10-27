using DeadlockScout.Core.Models;
using DeadlockScout.Core.Scouting;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Tests.Core;

public class ScoutRulesTests
{
    private static readonly FlagSettings Cfg = new();
    private static RosterPlayer P(int? rank = null, int? party = null) => new(1, "x", Teams.Amber, 1, rank, null) { PartyId = party };

    [Fact]
    public void Median_rank_ignores_unknowns()
    {
        var players = new[] { P(50), P(null), P(70), P(60), P(null) };
        Assert.Equal(60, ScoutRules.LobbyMedianRank(players));
        Assert.Equal(55, ScoutRules.LobbyMedianRank(new[] { P(50), P(60) }));
        Assert.Null(ScoutRules.LobbyMedianRank(new[] { P(), P() }));
    }

    [Fact]
    public void Private_when_no_data()
    {
        var f = ScoutRules.Compute(Cfg, P(80), hasData: false, 0, 0, 0, 60, null);
        Assert.Equal(ScoutFlags.Private, f);
        Assert.Equal("private", ScoutRules.Label(f));
    }

    [Fact]
    public void New_on_hero_under_five_games()
    {
        var f = ScoutRules.Compute(Cfg, P(60), true, gamesOnHero: 4, 0.5, totalGames: 200, 60, 0);
        Assert.True(f.HasFlag(ScoutFlags.NewOnHero));
        Assert.False(f.HasFlag(ScoutFlags.SuspectedSmurf));
        Assert.Equal("new on hero", ScoutRules.Label(f));
    }

    [Fact]
    public void Smurf_when_few_games_and_above_lobby_median()
    {
        var f = ScoutRules.Compute(Cfg, P(rank: 80), true, 20, 0.5, totalGames: 30, lobbyMedianRank: 60, 0);
        Assert.True(f.HasFlag(ScoutFlags.SuspectedSmurf));
        Assert.Equal("Smurf?", ScoutRules.Label(f));
        // same games but at or below the median: not a smurf
        var g = ScoutRules.Compute(Cfg, P(rank: 60), true, 20, 0.5, 30, 60, 0);
        Assert.False(g.HasFlag(ScoutFlags.SuspectedSmurf));
    }

    [Fact]
    public void Smurf_when_hot_win_rate_on_ten_plus_games()
    {
        Assert.True(ScoutRules.Compute(Cfg, P(), true, 12, 0.75, 500, null, 0).HasFlag(ScoutFlags.SuspectedSmurf));
        Assert.False(ScoutRules.Compute(Cfg, P(), true, 9, 0.9, 500, null, 0).HasFlag(ScoutFlags.SuspectedSmurf));
        Assert.False(ScoutRules.Compute(Cfg, P(), true, 12, 0.70, 500, null, 0).HasFlag(ScoutFlags.SuspectedSmurf));
    }

    [Fact]
    public void Unknown_stack_and_met_before()
    {
        var f = ScoutRules.Compute(Cfg, P(party: 3), true, 0, 0, totalGames: 2, null, metBefore: 3);
        Assert.True(f.HasFlag(ScoutFlags.Unknown));
        Assert.True(f.HasFlag(ScoutFlags.Stack));
        Assert.True(f.HasFlag(ScoutFlags.MetBefore));
        Assert.Equal("unknown", ScoutRules.Label(f));
        Assert.Equal("stack", ScoutRules.Label(ScoutFlags.Stack | ScoutFlags.MetBefore));
        Assert.Null(ScoutRules.Label(ScoutFlags.None));
    }
}

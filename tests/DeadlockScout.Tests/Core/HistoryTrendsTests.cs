using DeadlockScout.Core.History;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

public class HistoryTrendsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);

    /// <summary>A game n hours after T0 on Amber: won or lost (null: not scored), 30 minutes, souls as given.</summary>
    private static MatchHistoryEntry G(int n, int hero, bool? won, int souls = 18000, int k = 5, int d = 5, int a = 5)
        => new(n, hero, T0.AddHours(n), TimeSpan.FromMinutes(30), Teams.Amber, k, d, a, souls, won is null ? 5 : won.Value ? 1 : 2, null, null,
            won is null ? null : won.Value ? Teams.Amber : Teams.Sapphire);

    /// <summary>Newest first, as the history endpoint returns it.</summary>
    private static List<MatchHistoryEntry> NewestFirst(params MatchHistoryEntry[] oldestFirst) => oldestFirst.Reverse().ToList();

    [Fact]
    public void Range_filters_by_hero_before_taking_the_newest()
    {
        var history = NewestFirst(G(1, 13, true), G(2, 7, true), G(3, 13, false), G(4, 7, false), G(5, 13, true));
        Assert.Equal(new long[] { 5, 3 }, HistoryTrends.Range(history, 2, heroId: 13).Select(g => g.MatchId));
        Assert.Equal(new long[] { 5, 4, 3 }, HistoryTrends.Range(history, 3).Select(g => g.MatchId));
    }

    [Fact]
    public void Series_runs_oldest_first_with_rolling_values_and_no_win_rate_until_three_scored_games()
    {
        var history = NewestFirst(G(1, 13, true, 15000), G(2, 13, null, 21000), G(3, 13, false, 18000), G(4, 13, true, 24000));
        var s = HistoryTrends.Series(history);

        Assert.Equal(new long[] { 1, 2, 3, 4 }, s.Select(p => p.Game.MatchId));
        Assert.Equal(new bool?[] { true, null, false, true }, s.Select(p => p.Won));
        Assert.Null(s[1].RollingWinRate);              // one scored game so far (the unscored one does not count)
        Assert.Null(s[2].RollingWinRate);              // two scored: still too few
        Assert.Equal(2 / 3.0, s[3].RollingWinRate!.Value, 6); // won 2 of 3 scored
        Assert.Equal(500, s[0].SoulsPerMinute);
        Assert.Equal((500 + 700 + 600) / 3.0, s[2].RollingSoulsPerMinute, 6);
    }

    [Fact]
    public void Rolling_values_cover_at_most_the_last_ten_games()
    {
        var oldestFirst = Enumerable.Range(1, 12).Select(n => G(n, 13, n > 2, 3000 * n)).ToArray(); // lost the first two, won the rest
        var last = HistoryTrends.Series(NewestFirst(oldestFirst))[^1];
        Assert.Equal(1.0, last.RollingWinRate);        // games 3-12
        Assert.Equal(Enumerable.Range(3, 10).Average(n => 100.0 * n), last.RollingSoulsPerMinute, 6);
    }

    [Fact]
    public void Compare_sets_the_older_half_against_the_newer_and_pools_kda()
    {
        var history = NewestFirst(
            G(1, 13, false, 15000, k: 2, d: 8, a: 2), G(2, 13, false, 15000, k: 2, d: 8, a: 2),
            G(3, 13, true, 21000, k: 8, d: 2, a: 8), G(4, 13, true, 21000, k: 8, d: 2, a: 8), G(5, 13, false, 21000, k: 8, d: 2, a: 8));
        var c = HistoryTrends.Compare(history)!;
        Assert.Equal(5, c.Games);
        Assert.Equal(0.0, c.WinRateThen);              // games 1-2
        Assert.Equal(2 / 3.0, c.WinRateNow!.Value, 6);  // games 3-5: the odd game goes to the newer half
        Assert.Equal(500, c.SpmThen);
        Assert.Equal(700, c.SpmNow);
        Assert.Equal(0.5, c.KdaThen);                   // (4 + 4) / 16
        Assert.Equal(8.0, c.KdaNow);                    // (24 + 24) / 6
        Assert.Null(HistoryTrends.Compare(history.Take(3).ToList()));
    }

    [Fact]
    public void Heroes_are_ordered_by_games_then_most_recent()
    {
        var history = NewestFirst(G(1, 7, true), G(2, 13, false), G(3, 1, true), G(4, 13, true), G(5, 7, null));
        var rows = HistoryTrends.Heroes(history);
        Assert.Equal(new[] { 7, 13, 1 }, rows.Select(r => r.HeroId)); // 7 and 13 have two games each; 7 was played last
        Assert.Equal((2, 1, 0), (rows[0].Games, rows[0].Wins, rows[0].Losses));
        Assert.Equal(1.0, rows[0].WinRate);              // the unscored game is left out of the rate
        Assert.Equal(0.5, rows[1].WinRate);
        Assert.Equal(T0.AddHours(5), rows[0].LastPlayed);
    }
}

using DeadlockScout.Core.History;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

/// <summary>Synthetic games: made-up ids, hero ids from the assets list.</summary>
public class CounterNotesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static HistoryGame Game(long id, int myHero, bool? won, params int[] enemyHeroes)
    {
        var entry = new MatchHistoryEntry(id, myHero, T0.AddHours(-id), TimeSpan.FromMinutes(30), Teams.Amber, 5, 5, 5, 20_000,
            won is null ? 5 : won.Value ? 1 : 2, null, null, won is null ? null : won.Value ? Teams.Amber : Teams.Sapphire);
        var enemies = enemyHeroes.Select((h, i) => new HistoryPlayer((uint)(100 + i), h, 1, 1, 1, 1000, false)).ToList();
        var detail = new MatchDetail(id, null, null, null, new long[] { 11 }, 1, 6, 70, Array.Empty<HistoryPlayer>(), enemies,
            Purchases: new[] { new MatchItem(22, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20)), new MatchItem(11, TimeSpan.FromMinutes(9), null) });
        return new HistoryGame(entry, detail);
    }

    [Fact]
    public void Bought_counts_kept_and_sold_items()
    {
        var d = Game(1, 13, true, 2).Detail!;
        Assert.True(CounterNotes.Bought(d, 11));   // kept
        Assert.True(CounterNotes.Bought(d, 22));   // bought and sold
        Assert.False(CounterNotes.Bought(d, 33));
    }

    [Fact]
    public void Nemeses_are_the_heroes_you_lose_to_most_with_the_hero_you_played_most()
    {
        var games = new[]
        {
            Game(1, 13, false, 20, 2), Game(2, 13, false, 20, 2), Game(3, 1, false, 20, 4),
            Game(4, 13, true, 2, 4), Game(5, 13, false, 18), Game(6, 7, null, 20, 18, 18), // not scored: skipped
            new HistoryGame(Game(7, 13, false, 20).Entry, null),                        // no lobby yet: skipped
        };
        var n = CounterNotes.Nemeses(games);
        // Ivy 0-3 (played Haze twice, Infernus once); Seven 1-2. Lady Geist 1-1 is even, Mo & Krill 0-1 too few games.
        Assert.Equal(new[] { new Nemesis(20, 0, 3, 13), new Nemesis(2, 1, 2, 13) }, n);
        Assert.Single(CounterNotes.Nemeses(games, count: 1));
        Assert.Contains(CounterNotes.Nemeses(games, minGames: 1), x => x.EnemyHeroId == 18);
    }
}

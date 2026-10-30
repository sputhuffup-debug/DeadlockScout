using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.History;

/// <summary>An enemy hero you lose to: your record in the page's games they were in, and the hero you played most against them.</summary>
public sealed record Nemesis(int EnemyHeroId, int Wins, int Losses, int MyHeroId)
{
    public int Games => Wins + Losses;
}

/// <summary>
/// Counter notes for the match history, from your own games: which counter items you bought in a game, and the enemy
/// heroes you lose to most over a page. The counter items themselves come from <c>CounterItemService</c>. Pure, so it is
/// unit tested.
/// </summary>
public static class CounterNotes
{
    /// <summary>Whether you bought the item in that game, kept or sold.</summary>
    public static bool Bought(MatchDetail detail, long itemId)
        => detail.FinalItems.Contains(itemId) || (detail.Purchases?.Any(p => p.ItemId == itemId) ?? false);

    /// <summary>
    /// The enemy heroes with your worst records over <paramref name="games"/> (scored games with a processed lobby only):
    /// at least <paramref name="minGames"/> games and more losses than wins, the most lost first. Each names the hero you
    /// played most in those games, the one whose counter items apply.
    /// </summary>
    public static IReadOnlyList<Nemesis> Nemeses(IEnumerable<HistoryGame> games, int minGames = 2, int count = 3)
    {
        var tally = new Dictionary<int, (int Wins, int Losses, List<int> MyHeroes)>();
        foreach (var g in games)
        {
            if (g.Detail is not { } d || !(g.Entry.IsWin || g.Entry.IsLoss)) continue;
            foreach (var enemy in d.Enemies.Select(e => e.HeroId).Where(h => h > 0).Distinct())
            {
                var t = tally.TryGetValue(enemy, out var x) ? x : (Wins: 0, Losses: 0, MyHeroes: new List<int>());
                if (g.Entry.IsWin) t.Wins++; else t.Losses++;
                t.MyHeroes.Add(g.Entry.HeroId);
                tally[enemy] = t;
            }
        }
        return tally
            .Where(kv => kv.Value.Wins + kv.Value.Losses >= minGames && kv.Value.Losses > kv.Value.Wins)
            .OrderByDescending(kv => kv.Value.Losses - kv.Value.Wins)
            .ThenByDescending(kv => kv.Value.Losses)
            .ThenBy(kv => kv.Key)
            .Take(count)
            // The hero you played most against them; ties go to the most recent (games come newest first).
            .Select(kv => new Nemesis(kv.Key, kv.Value.Wins, kv.Value.Losses,
                kv.Value.MyHeroes.GroupBy(h => h).OrderByDescending(gr => gr.Count()).ThenBy(gr => kv.Value.MyHeroes.IndexOf(gr.Key)).First().Key))
            .ToList();
    }
}

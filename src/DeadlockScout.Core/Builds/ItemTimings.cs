using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Builds;

/// <summary>One item you bought in a game against a rank's average buy time for it on the same hero.</summary>
public sealed record ItemTimingResult(long ItemId, TimeSpan You, TimeSpan Rank, int RankMatches)
{
    /// <summary>Positive: you bought it later than the rank does.</summary>
    public TimeSpan Difference => You - Rank;
}

/// <summary>An item over many games: how often you bought it, your average time, the rank's, and the gap.</summary>
public sealed record ItemTimingTotal(long ItemId, int Games, TimeSpan You, TimeSpan Rank)
{
    public TimeSpan Difference => You - Rank;
}

/// <summary>
/// Your item timings against a rank's average buy times. Only the first purchase of an item counts (a rebuy after a sell is
/// a different decision), only items the rank has enough matches on, and only after the first minute: starting items are
/// bought in the base and say nothing about pace.
/// </summary>
public static class ItemTimings
{
    /// <summary>Rank matches an item needs before its average is worth comparing with.</summary>
    public const int MinRankMatches = 50;
    public static readonly TimeSpan StartingItems = TimeSpan.FromMinutes(1);
    /// <summary>A gap within this reads as on time.</summary>
    public static readonly TimeSpan OnTime = TimeSpan.FromSeconds(45);

    public static IReadOnlyList<ItemTimingResult> Compare(IEnumerable<MatchItem> purchases, IReadOnlyDictionary<long, ItemTiming> rank)
    {
        var seen = new HashSet<long>();
        var results = new List<ItemTimingResult>();
        foreach (var p in purchases.OrderBy(p => p.BoughtAt))
        {
            if (!seen.Add(p.ItemId) || p.BoughtAt < StartingItems) continue;
            if (!rank.TryGetValue(p.ItemId, out var t) || t.Matches < MinRankMatches || t.AvgBuyTime < StartingItems) continue;
            results.Add(new ItemTimingResult(p.ItemId, p.BoughtAt, t.AvgBuyTime, t.Matches));
        }
        return results;
    }

    /// <summary>The average gap over <paramref name="results"/>; null when there are none.</summary>
    public static TimeSpan? AverageGap(IReadOnlyCollection<ItemTimingResult> results)
        => results.Count == 0 ? null : TimeSpan.FromSeconds(results.Average(r => r.Difference.TotalSeconds));

    /// <summary>
    /// Each item over <paramref name="games"/>: games bought, your average time and the rank's (averaged over the games, as
    /// the hero can differ), most bought first, then the biggest gap.
    /// </summary>
    public static IReadOnlyList<ItemTimingTotal> Total(IEnumerable<IReadOnlyList<ItemTimingResult>> games)
        => games.SelectMany(g => g)
            .GroupBy(r => r.ItemId)
            .Select(g => new ItemTimingTotal(g.Key, g.Count(),
                TimeSpan.FromSeconds(g.Average(r => r.You.TotalSeconds)), TimeSpan.FromSeconds(g.Average(r => r.Rank.TotalSeconds))))
            .OrderByDescending(t => t.Games).ThenByDescending(t => t.Difference)
            .ToList();

    /// <summary>The item furthest behind the rank among those bought in at least <paramref name="minGames"/> games; null when none is late.</summary>
    public static ItemTimingTotal? Latest(IReadOnlyList<ItemTimingTotal> totals, int minGames = 2)
        => totals.Where(t => t.Games >= minGames && t.Difference > OnTime).OrderByDescending(t => t.Difference).FirstOrDefault();
}

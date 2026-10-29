using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Builds;

/// <summary>A big item you usually buy on a hero: in how many of your games, and your median first-purchase time.</summary>
public sealed record UsualItem(long ItemId, int Games, TimeSpan MedianAt);

/// <summary>Where the next item stands against the rank's buy time: ahead of it, inside the lead, or already late.</summary>
public enum NudgeState { Ahead, Soon, Late, NoRank }

/// <summary>The overlay's next-item nudge: the item, the rank's average buy time when known, and how far off it is.</summary>
public sealed record NextItemNudge(long ItemId, TimeSpan? RankAt, TimeSpan? In, NudgeState State, UsualItem Usual);

/// <summary>
/// Your usual order of big items on a hero, from your recent games' purchases, and the next one of them you have not bought
/// this match, set against when the rank gate's players have it (the item timings table). "Big" is 3,000 souls and up:
/// the purchases that decide a fight, not the 500s and 1,250s bought on the way.
/// </summary>
public static class NextItem
{
    public const int MinCost = 3000;
    /// <summary>An item has to be in this share of your games on the hero (and at least two) to count as usual.</summary>
    public const double MinShare = 0.4;

    /// <summary>The usual big items, in the order you usually buy them (median first-purchase time).</summary>
    public static IReadOnlyList<UsualItem> UsualBuild(IReadOnlyList<IReadOnlyList<MatchItem>> games, Func<long, int> cost)
    {
        if (games.Count == 0) return Array.Empty<UsualItem>();
        int need = Math.Max(2, (int)Math.Ceiling(games.Count * MinShare));
        return games
            .SelectMany(g => g.Where(p => cost(p.ItemId) >= MinCost).GroupBy(p => p.ItemId).Select(x => (Id: x.Key, At: x.Min(p => p.BoughtAt))))
            .GroupBy(x => x.Id)
            .Where(g => g.Count() >= need)
            .Select(g => new UsualItem(g.Key, g.Count(), Median(g.Select(x => x.At).ToList())))
            .OrderBy(u => u.MedianAt)
            .ToList();
    }

    /// <summary>
    /// The first usual item you do not own yet (<paramref name="owned"/>; null when the feed sends no items, then the first
    /// one whose usual time has not long passed), against <paramref name="rank"/>'s average buy time.
    /// </summary>
    public static NextItemNudge? Next(IReadOnlyList<UsualItem> usual, IReadOnlySet<long>? owned, TimeSpan now,
                                      IReadOnlyDictionary<long, ItemTiming>? rank, TimeSpan lead)
    {
        var next = owned is not null
            ? usual.FirstOrDefault(u => !owned.Contains(u.ItemId))
            : usual.FirstOrDefault(u => u.MedianAt > now - TimeSpan.FromMinutes(1));
        if (next is null) return null;
        if (rank is null || !rank.TryGetValue(next.ItemId, out var t) || t.Matches < ItemTimings.MinRankMatches)
            return new NextItemNudge(next.ItemId, null, null, NudgeState.NoRank, next);
        var left = t.AvgBuyTime - now;
        var state = left < TimeSpan.Zero ? NudgeState.Late : left <= lead ? NudgeState.Soon : NudgeState.Ahead;
        return new NextItemNudge(next.ItemId, t.AvgBuyTime, left, state, next);
    }

    private static TimeSpan Median(List<TimeSpan> xs)
    {
        xs.Sort();
        int n = xs.Count;
        return n % 2 == 1 ? xs[n / 2] : TimeSpan.FromTicks((xs[n / 2 - 1].Ticks + xs[n / 2].Ticks) / 2);
    }
}

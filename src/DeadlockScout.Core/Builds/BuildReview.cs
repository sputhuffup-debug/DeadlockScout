using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Builds;

/// <summary>What a suggestion is about: an item the rank buys that you skipped or bought late, or one it rarely buys.</summary>
public enum BuildAdvice { Skipped, Late, Rare }

/// <summary>
/// One change to a game's build (0.7): the item, what kind of change, when the rank gate's players buy it, when you did
/// (null when you did not), how many of their games have it next to their most bought item (<paramref name="Share"/>,
/// 0 to 1), and how many points its win rate is above their usual (<paramref name="Lift"/>, null without the item stats).
/// </summary>
public sealed record BuildSuggestion(long ItemId, BuildAdvice Kind, TimeSpan RankAt, TimeSpan? YouAt, double Share, double? Lift);

/// <summary>The change that recurs most over a hero's games: skipped or late in <see cref="Times"/> of <see cref="Games"/>.</summary>
public sealed record RecurringChange(int HeroId, long ItemId, TimeSpan RankAt, int Times, int Games);

/// <summary>
/// Build suggestions for the match history (0.7), from data the history already caches: the rank gate's average buy
/// times on the hero (how often and when each item is bought) and its item stats (wins with each item). Post-match
/// only; nothing of this reaches the overlay. Pure, so tested.
/// </summary>
public static class BuildReview
{
    /// <summary>An item bought in at least this share of the rank's games, next to its most bought one, is part of its build.</summary>
    public const double CoreShare = 0.4;
    /// <summary>An item bought in under this share is one the rank rarely buys.</summary>
    public const double RareShare = 0.1;
    /// <summary>Cheaper items (the 500-soul tier, starting items) say little about a build.</summary>
    public const int MinCost = 1250;
    /// <summary>A core item bought this much later than the rank does is late.</summary>
    public static readonly TimeSpan LateBy = TimeSpan.FromMinutes(2);
    /// <summary>Item stats need this many games before a win rate is trusted.</summary>
    public const int MinStatMatches = 100;
    /// <summary>Suggestions kept per kind.</summary>
    public const int MaxEach = 3;

    /// <param name="purchases">Your purchases in the game, sold ones included.</param>
    /// <param name="duration">How long the game lasted: an item the rank buys after the end is no suggestion.</param>
    /// <param name="rank">The rank gate's average buy time per item on the hero.</param>
    /// <param name="stats">The rank's item stats on the hero (any souls bucket), for each item's win rate; null without.</param>
    /// <param name="cost">An item's price, 0 for abilities and unknown ids.</param>
    public static IReadOnlyList<BuildSuggestion> Review(IEnumerable<MatchItem> purchases, TimeSpan duration,
        IReadOnlyDictionary<long, ItemTiming> rank, IReadOnlyList<ItemBucketStat>? stats, Func<long, int> cost)
    {
        var shop = rank.Values.Where(t => cost(t.ItemId) >= MinCost && t.Matches > 0).ToList();
        if (shop.Count == 0) return Array.Empty<BuildSuggestion>();
        double top = shop.Max(t => t.Matches);
        double Share(long id) => rank.TryGetValue(id, out var t) ? t.Matches / top : 0;
        TimeSpan RankAt(long id) => rank.TryGetValue(id, out var t) ? TimeSpan.FromSeconds(t.AvgBuyTimeSeconds) : TimeSpan.Zero;
        var lift = Lifts(stats);
        double? Lift(long id) => lift.TryGetValue(id, out var l) ? l : null;

        var bought = purchases.Where(p => cost(p.ItemId) >= MinCost).GroupBy(p => p.ItemId).ToDictionary(g => g.Key, g => g.Min(p => p.BoughtAt));
        var core = shop.Where(t => t.Matches / top >= CoreShare && TimeSpan.FromSeconds(t.AvgBuyTimeSeconds) < duration - TimeSpan.FromMinutes(1)).ToList();

        var skipped = core.Where(t => !bought.ContainsKey(t.ItemId) && Lift(t.ItemId) is not < 0)
            .Select(t => new BuildSuggestion(t.ItemId, BuildAdvice.Skipped, RankAt(t.ItemId), null, Share(t.ItemId), Lift(t.ItemId)))
            .OrderByDescending(s => s.Lift ?? 0).ThenByDescending(s => s.Share).Take(MaxEach);
        var late = core.Where(t => bought.TryGetValue(t.ItemId, out var at) && at - RankAt(t.ItemId) >= LateBy)
            .Select(t => new BuildSuggestion(t.ItemId, BuildAdvice.Late, RankAt(t.ItemId), bought[t.ItemId], Share(t.ItemId), Lift(t.ItemId)))
            .OrderByDescending(s => s.YouAt!.Value - s.RankAt).Take(MaxEach);
        var rare = bought.Where(b => Share(b.Key) < RareShare)
            .Select(b => new BuildSuggestion(b.Key, BuildAdvice.Rare, RankAt(b.Key), b.Value, Share(b.Key), Lift(b.Key)))
            .OrderBy(s => s.Lift ?? 0).ThenBy(s => s.YouAt).Take(MaxEach);
        return skipped.Concat(late).Concat(rare).ToList();
    }

    /// <summary>Each item's win rate less the rank's usual on the hero (all items' games pooled), from enough games.</summary>
    public static IReadOnlyDictionary<long, double> Lifts(IReadOnlyList<ItemBucketStat>? stats)
    {
        if (stats is not { Count: > 0 }) return new Dictionary<long, double>();
        long matches = stats.Sum(s => (long)s.Matches), wins = stats.Sum(s => (long)s.Wins);
        if (matches == 0) return new Dictionary<long, double>();
        double usual = (double)wins / matches;
        return stats.GroupBy(s => s.ItemId)
            .Select(g => (Id: g.Key, Matches: g.Sum(s => s.Matches), Wins: g.Sum(s => s.Wins)))
            .Where(x => x.Matches >= MinStatMatches)
            .ToDictionary(x => x.Id, x => (double)x.Wins / x.Matches - usual);
    }

    /// <summary>
    /// The core item skipped or bought late most often over one hero's games, when it happened in at least two of them
    /// and in at least a third; null otherwise.
    /// </summary>
    public static RecurringChange? Recurring(int heroId, IReadOnlyList<IReadOnlyList<BuildSuggestion>> games)
    {
        if (games.Count < 2) return null;
        var best = games.SelectMany(g => g.Where(s => s.Kind is BuildAdvice.Skipped or BuildAdvice.Late).Select(s => s.ItemId).Distinct()
                .Select(id => (Id: id, At: g.First(s => s.ItemId == id).RankAt)))
            .GroupBy(x => x.Id)
            .Select(g => (Id: g.Key, Times: g.Count(), At: g.First().At))
            .OrderByDescending(x => x.Times).ThenBy(x => x.At)
            .FirstOrDefault();
        return best.Times >= 2 && best.Times * 3 >= games.Count ? new RecurringChange(heroId, best.Id, best.At, best.Times, games.Count) : null;
    }
}

using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Builds;

/// <summary>The two tables behind the build hint, for one hero, bracket and patch.</summary>
public sealed record BuildTables(IReadOnlyList<ItemBucketStat> Items, IReadOnlyList<AbilityOrderStat> Abilities)
{
    public static readonly BuildTables Empty = new(Array.Empty<ItemBucketStat>(), Array.Empty<AbilityOrderStat>());
}

/// <summary>One step of an ability order: the ability, the rank it reaches (1 = unlock), and when players usually take it.</summary>
public sealed record AbilityStep(long AbilityId, int Rank, TimeSpan TypicalTime);

/// <summary>The most played complete ability order for a hero, reduced to what the build hint shows.</summary>
public sealed record AbilityPlan(IReadOnlyList<AbilityStep> Steps, long? MaxFirst, int Matches, int Wins)
{
    public double WinRate => Matches == 0 ? 0 : (double)Wins / Matches;

    /// <summary>The first step players usually take after <paramref name="gameTime"/>, or null when the order is done.</summary>
    public AbilityStep? NextAfter(TimeSpan gameTime) => Steps.FirstOrDefault(s => s.TypicalTime > gameTime);
}

/// <summary>Pure reductions for the build hint. Nothing here is stored; the overlay recomputes it at 2 Hz.</summary>
public static class BuildHints
{
    /// <summary>Width of the item-stats net-worth buckets the client requests (bucket=net_worth_by_2000).</summary>
    public const int BucketWidth = 2000;

    /// <summary>An ability is maxed after its unlock and three upgrades.</summary>
    public const int MaxRank = 4;

    public static int BucketOf(int netWorth) => Math.Max(0, netWorth) / BucketWidth * BucketWidth;

    /// <summary>
    /// The items the hero's players buy most at your net worth, most bought first. Falls back to the nearest lower bucket
    /// with data, so a net worth past the table's end still gets a hint.
    /// </summary>
    public static IReadOnlyList<ItemBucketStat> CommonBuys(IReadOnlyList<ItemBucketStat> items, int netWorth, int count = 3)
    {
        int bucket = BucketOf(netWorth);
        var available = items.Where(i => i.Bucket <= bucket).Select(i => i.Bucket).DefaultIfEmpty(-1).Max();
        if (available < 0) return Array.Empty<ItemBucketStat>();
        return items.Where(i => i.Bucket == available)
            .OrderByDescending(i => i.Matches).ThenBy(i => i.ItemId)
            .Take(Math.Max(0, count))
            .ToList();
    }

    /// <summary>The most played complete order, with each step's rank and typical time, and the ability maxed first.</summary>
    public static AbilityPlan? Plan(IEnumerable<AbilityOrderStat> orders)
    {
        var top = orders.OrderByDescending(o => o.Matches).ThenByDescending(o => o.Wins).FirstOrDefault();
        if (top is null || top.Abilities.Count == 0) return null;

        var ranks = new Dictionary<long, int>();
        var steps = new List<AbilityStep>(top.Abilities.Count);
        long? maxFirst = null;
        for (int i = 0; i < top.Abilities.Count; i++)
        {
            long id = top.Abilities[i];
            int rank = ranks[id] = ranks.GetValueOrDefault(id) + 1;
            if (rank == MaxRank) maxFirst ??= id;
            // A step without a recorded time sorts last, so NextAfter never picks it ahead of a timed one.
            var typical = i < top.AvgUpgradeTimesSeconds.Count ? TimeSpan.FromSeconds(top.AvgUpgradeTimesSeconds[i]) : TimeSpan.MaxValue;
            steps.Add(new AbilityStep(id, rank, typical));
        }
        return new AbilityPlan(steps, maxFirst, top.Matches, top.Wins);
    }
}

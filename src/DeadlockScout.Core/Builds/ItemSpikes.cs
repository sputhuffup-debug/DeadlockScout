using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Builds;

/// <summary>
/// Power spikes: items that tend to swing a hero's games, so the overlay can flag an enemy the moment they buy one.
/// A spike is any 6,400-soul item, or one of the few items that lift the hero's win rate most at your rank, compared
/// with what other buyers at the same net worth do (so a late item is not credited for the lead that paid for it).
/// The ultimate unlocking counts too. Pure: the sets and the latest spike are derived, never stored.
/// </summary>
public static class ItemSpikes
{
    /// <summary>Stands in for the ultimate in an item list, so "ultimate unlocked" flows like a purchase.</summary>
    public const long UltimateId = -1;
    /// <summary>The most expensive shop tier; every item at this price is a spike.</summary>
    public const int BigItemCost = 6400;
    /// <summary>
    /// Cheaper items are left out of the data-driven pick: on real stats (Oct 2026) the top lifts at 1,600 souls were
    /// sustain items like Spirit Lifesteal, which help but do not change a fight the moment they are bought.
    /// </summary>
    public const int MinCost = 3200;
    /// <summary>Purchases an item needs across all buckets before its lift is trusted.</summary>
    public const int MinMatches = 200;
    /// <summary>Win-rate lift (fraction) an item needs over other buys at the same net worth.</summary>
    public const double MinLift = 0.02;
    public const int TopCount = 4;
    /// <summary>How long the row flashes after the purchase shows up, in game time.</summary>
    public static readonly TimeSpan FlashFor = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Each item's win-rate lift: in every net-worth bucket, its buyers' win rate minus the win rate of all buys in that
    /// bucket, averaged with the item's purchases as weights. Items under <see cref="MinMatches"/> are left out.
    /// </summary>
    public static IReadOnlyDictionary<long, double> Lifts(IReadOnlyList<ItemBucketStat> stats)
    {
        var baseline = stats.GroupBy(s => s.Bucket).ToDictionary(g => g.Key, g =>
        {
            long m = g.Sum(s => (long)s.Matches);
            return m == 0 ? 0 : (double)g.Sum(s => (long)s.Wins) / m;
        });
        var lifts = new Dictionary<long, double>();
        foreach (var item in stats.GroupBy(s => s.ItemId))
        {
            long matches = item.Sum(s => (long)s.Matches);
            if (matches < MinMatches) continue;
            lifts[item.Key] = item.Sum(s => s.Matches * (s.WinRate - baseline[s.Bucket])) / matches;
        }
        return lifts;
    }

    /// <summary>The hero's spike items: every 6,400-soul item, plus the top lifts among 3,200-soul items.</summary>
    public static IReadOnlySet<long> Pick(IReadOnlyList<ItemBucketStat> stats, IEnumerable<ItemAsset> shop)
    {
        var byId = shop.Where(i => i.Slot != "ability").GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        var set = byId.Values.Where(i => i.Cost >= BigItemCost).Select(i => i.Id).ToHashSet();
        foreach (var (id, _) in Lifts(stats)
                     .Where(kv => kv.Value >= MinLift && byId.TryGetValue(kv.Key, out var i) && i.Cost >= MinCost && i.Cost < BigItemCost)
                     .OrderByDescending(kv => kv.Value).Take(TopCount))
            set.Add(id);
        set.Add(UltimateId);
        return set;
    }

    /// <summary>
    /// The enemy's most recent spike, or null without one. <paramref name="owned"/> maps each item they hold to when it
    /// first showed up (null for what they already had when the feed started, which never flashes); ties go to the
    /// dearer item. It flashes for <see cref="FlashFor"/> after the purchase.
    /// </summary>
    public static SpikeView? Latest(IReadOnlyDictionary<long, TimeSpan?> owned, IReadOnlySet<long> spikes, Func<long, int> cost, TimeSpan now)
    {
        var best = owned.Where(kv => spikes.Contains(kv.Key))
            .OrderByDescending(kv => kv.Value ?? TimeSpan.MinValue)
            .ThenByDescending(kv => kv.Key == UltimateId ? 0 : cost(kv.Key))
            .Select(kv => (KeyValuePair<long, TimeSpan?>?)kv).FirstOrDefault();
        if (best is not { } b) return null;
        bool flash = b.Value is { } at && now >= at && now - at < FlashFor;
        return new SpikeView(b.Key, b.Value, flash);
    }
}

/// <summary>An enemy's latest spike: the item (or <see cref="ItemSpikes.UltimateId"/>), when it was bought, and whether the row flashes now.</summary>
public sealed record SpikeView(long ItemId, TimeSpan? BoughtAt, bool Flash);

/// <summary>
/// The items one player holds and when each first showed up in the live feed. The first observation is what they had
/// when the feed started, so those items get no purchase time. Sold items drop out. Thread-safe: the feed writes it,
/// the overlay reads snapshots.
/// </summary>
public sealed class ItemTimeline
{
    private readonly Dictionary<long, TimeSpan?> _owned = new();
    private readonly object _gate = new();
    private bool _seen;

    public void Observe(IEnumerable<long> items, TimeSpan gameTime)
    {
        lock (_gate)
        {
            var now = items.ToHashSet();
            foreach (var gone in _owned.Keys.Where(k => !now.Contains(k)).ToList()) _owned.Remove(gone);
            foreach (var id in now)
                if (!_owned.ContainsKey(id)) _owned[id] = _seen ? gameTime : null;
            _seen = true;
        }
    }

    public IReadOnlyDictionary<long, TimeSpan?> Snapshot()
    {
        lock (_gate) return new Dictionary<long, TimeSpan?>(_owned);
    }
}

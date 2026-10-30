using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Builds;

/// <summary>
/// Items that counter an enemy hero, from match records only (no opinion): for each item, your hero's win rate in the
/// matches they bought it with that hero on the other team, against their win rate with it overall. Two corrections keep
/// it honest. The matchup's own shift is taken out (if your hero beats that hero anyway, every item looks a little better),
/// and only items clearly above noise are kept: enough games, a lift of at least <c>minLift</c> and at least <c>minZ</c>
/// standard errors. Most matchups have none or one, of one to three points. Pure, so it is unit tested.
/// </summary>
public static class CounterItems
{
    public static IReadOnlyList<CounterItem> Rank(int enemyHeroId, IReadOnlyList<ItemWinStat> overall, IReadOnlyList<ItemWinStat> versus,
                                                  int minMatches = 300, double minZ = 2.5, double minLift = 0.005)
    {
        var baseline = overall.Where(b => b.Matches > 0).GroupBy(b => b.ItemId).ToDictionary(g => g.Key, g => g.First());
        var paired = versus.Where(v => v.Matches > 0 && baseline.ContainsKey(v.ItemId)).ToList();
        if (paired.Count == 0) return Array.Empty<CounterItem>();

        // The matchup's shift: the games-weighted average lift across every item bought against that hero.
        double total = paired.Sum(v => (double)v.Matches);
        double offset = paired.Sum(v => v.Matches * (v.WinRate - baseline[v.ItemId].WinRate)) / total;

        var result = new List<CounterItem>();
        foreach (var v in paired)
        {
            if (v.Matches < minMatches) continue;
            double lift = v.WinRate - baseline[v.ItemId].WinRate - offset;
            double p = Math.Clamp(v.WinRate, 0.01, 0.99);
            double z = lift / Math.Sqrt(p * (1 - p) / v.Matches);
            if (lift >= minLift && z >= minZ) result.Add(new CounterItem(v.ItemId, enemyHeroId, lift, z, v.Matches));
        }
        return result.OrderByDescending(c => c.Z).ThenByDescending(c => c.Lift).ToList();
    }
}

/// <summary>
/// The counter items for your hero against each enemy hero: one cached item-stats call for your hero overall and one per
/// enemy hero, across all ranks over the last 30 days (a single rank has too few games against one hero to tell an item
/// apart from noise). The raw rows are cached for the benchmark TTL under the patch key; the ranking is computed on read.
/// </summary>
public sealed class CounterItemService
{
    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly IPatchKeyProvider _patch;
    private readonly TimeSpan _ttl;

    public CounterItemService(IDeadlockApi api, ICache cache, IPatchKeyProvider patch, TimeSpan ttl)
    {
        _api = api;
        _cache = cache;
        _patch = patch;
        _ttl = ttl;
    }

    /// <summary>Counter items for <paramref name="heroId"/> against <paramref name="enemyHeroId"/>, strongest first; empty when none is clear.</summary>
    public async Task<IReadOnlyList<CounterItem>> GetAsync(int heroId, int enemyHeroId, CancellationToken ct)
    {
        if (heroId <= 0 || enemyHeroId <= 0) return Array.Empty<CounterItem>();
        var patch = await _patch.GetAsync(ct).ConfigureAwait(false);
        var overall = await _cache.GetOrCreateAsync(CacheKeys.CounterBase(heroId, patch), _ttl,
            async c => (await _api.ItemWinStatsAsync(heroId, null, c).ConfigureAwait(false)).ToList(),
            staleWhileRevalidate: true, ct).ConfigureAwait(false);
        var versus = await _cache.GetOrCreateAsync(CacheKeys.CounterItems(heroId, enemyHeroId, patch), _ttl,
            async c => (await _api.ItemWinStatsAsync(heroId, enemyHeroId, c).ConfigureAwait(false)).ToList(),
            staleWhileRevalidate: true, ct).ConfigureAwait(false);
        return CounterItems.Rank(enemyHeroId, overall, versus);
    }
}

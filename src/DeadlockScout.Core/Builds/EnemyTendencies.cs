using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Core.Builds;

/// <summary>A big item a player usually buys on a hero: in what share of their games, and when on average.</summary>
public sealed record TendencyItem(long ItemId, double Share, TimeSpan AvgBuyTime, int Games);

/// <summary>
/// An enemy's build habits on their hero, from their own public matches: the big items (3,200 souls and up) they buy in
/// at least half their games, in the order they usually buy them. Shown on the enemy pace board as "next: Boundless Spirit
/// ~34:08" before they buy it. Pure, so it is unit tested.
/// </summary>
public static class EnemyTendencies
{
    /// <summary>
    /// The usual big items from one player's per-item stats on a hero. Their game count is taken as the most games any
    /// one item was bought in (every game buys some cheap item), and fewer than <paramref name="minGames"/> games say
    /// nothing.
    /// </summary>
    public static IReadOnlyList<TendencyItem> Usual(IReadOnlyList<ItemTiming> rows, Func<long, int> cost,
                                                 int minGames = 3, double minShare = 0.5, int minCost = 3200, int max = 4)
    {
        if (rows.Count == 0) return Array.Empty<TendencyItem>();
        int games = rows.Max(r => r.Matches);
        if (games < minGames) return Array.Empty<TendencyItem>();
        return rows.Where(r => cost(r.ItemId) >= minCost && r.AvgBuyTimeSeconds > 0 && (double)r.Matches / games >= minShare)
                   .OrderBy(r => r.AvgBuyTimeSeconds)
                   .Take(max)
                   .Select(r => new TendencyItem(r.ItemId, (double)r.Matches / games, TimeSpan.FromSeconds(r.AvgBuyTimeSeconds), games))
                   .ToList();
    }

    /// <summary>
    /// The usual items they do not hold yet (<paramref name="held"/>, from the Direct feed), in buy order. Without item
    /// data (null) it goes by the clock instead: the usual items whose average buy time has not passed by more than a minute.
    /// </summary>
    public static IReadOnlyList<TendencyItem> Next(IReadOnlyList<TendencyItem> usual, IEnumerable<long>? held, TimeSpan now, int count = 1)
    {
        if (held is null) return usual.Where(u => u.AvgBuyTime > now - TimeSpan.FromMinutes(1)).Take(count).ToList();
        var have = held.ToHashSet();
        return usual.Where(u => !have.Contains(u.ItemId)).Take(count).ToList();
    }
}

/// <summary>
/// One cached analytics/item-stats call per enemy (account and hero, the last 90 days), a day. Analytics calls allow
/// 60 a minute here, so a lobby's six enemies cost six calls; a player seen in the last day costs none.
/// </summary>
public sealed class EnemyTendencyService
{
    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly IOptionsMonitor<ScoutSettings> _settings;

    public EnemyTendencyService(IDeadlockApi api, ICache cache, IOptionsMonitor<ScoutSettings> settings)
    {
        _api = api;
        _cache = cache;
        _settings = settings;
    }

    public async Task<IReadOnlyList<ItemTiming>> GetAsync(uint accountId, int heroId, CancellationToken ct)
    {
        if (accountId == 0 || heroId <= 0) return Array.Empty<ItemTiming>();
        var ttl = TimeSpan.FromHours(_settings.CurrentValue.Cache.SharedHistoryTtlHours);
        return await _cache.GetOrCreateAsync(CacheKeys.PlayerItems(accountId, heroId), ttl,
            async c => (await _api.PlayerItemTimingsAsync(accountId, heroId, c).ConfigureAwait(false)).ToList(),
            staleWhileRevalidate: true, ct).ConfigureAwait(false);
    }
}

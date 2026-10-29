using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockScout.Core.Builds;

/// <summary>
/// The build hint's tables for a hero in a bracket: item purchases by net worth at buy time and ability orders. Each is
/// one analytics call per hero, bracket and patch, cached for the patch and warmed between matches. A table that fails
/// to load is empty, so the other half of the hint still shows.
/// </summary>
public sealed class BuildHintService
{
    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly IPatchKeyProvider _patch;
    private readonly TimeSpan _ttl;
    private readonly ILogger _log;

    public BuildHintService(IDeadlockApi api, ICache cache, IPatchKeyProvider patch, TimeSpan ttl, ILogger<BuildHintService>? log = null)
    {
        _api = api;
        _cache = cache;
        _patch = patch;
        _ttl = ttl;
        _log = log ?? NullLogger<BuildHintService>.Instance;
    }

    public async Task<BuildTables> GetAsync(int heroId, RankBracket bracket, CancellationToken ct)
    {
        if (heroId <= 0) return BuildTables.Empty;
        var patch = await _patch.GetAsync(ct).ConfigureAwait(false);
        var items = await ItemStatsAsync(heroId, bracket, patch, ct).ConfigureAwait(false);
        var abilities = await TryAsync(CacheKeys.AbilityOrder(heroId, bracket, patch),
            async c => (await _api.AbilityOrderStatsAsync(heroId, bracket, c).ConfigureAwait(false)).ToList(), "ability orders", heroId, ct).ConfigureAwait(false);
        return new BuildTables(items, abilities);
    }

    /// <summary>Only the item table, for an enemy's power spikes; shares the build hint's cache entry.</summary>
    public async Task<IReadOnlyList<ItemBucketStat>> GetItemStatsAsync(int heroId, RankBracket bracket, CancellationToken ct)
    {
        if (heroId <= 0) return Array.Empty<ItemBucketStat>();
        return await ItemStatsAsync(heroId, bracket, await _patch.GetAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every item's average buy time for the hero in the bracket, by item id, for comparing your item timings with a rank's.
    /// Empty when the table fails to load.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, ItemTiming>> GetItemTimingsAsync(int heroId, RankBracket bracket, CancellationToken ct)
    {
        if (heroId <= 0) return new Dictionary<long, ItemTiming>();
        var patch = await _patch.GetAsync(ct).ConfigureAwait(false);
        var rows = await TryAsync(CacheKeys.ItemTimings(heroId, bracket, patch),
            async c => (await _api.ItemTimingsAsync(heroId, bracket, c).ConfigureAwait(false)).ToList(), "item timings", heroId, ct).ConfigureAwait(false);
        return rows.GroupBy(r => r.ItemId).ToDictionary(g => g.Key, g => g.First());
    }

    private Task<List<ItemBucketStat>> ItemStatsAsync(int heroId, RankBracket bracket, string patch, CancellationToken ct)
        => TryAsync(CacheKeys.ItemStats(heroId, bracket, patch),
            async c => (await _api.ItemStatsByNetWorthAsync(heroId, bracket, c).ConfigureAwait(false)).ToList(), "item stats", heroId, ct);

    private async Task<List<T>> TryAsync<T>(string key, Func<CancellationToken, Task<List<T>>> factory, string what, int heroId, CancellationToken ct)
    {
        try
        {
            return await _cache.GetOrCreateAsync(key, _ttl, factory, staleWhileRevalidate: true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "No {What} for hero {Hero}", what, heroId);
            return new List<T>();
        }
    }
}

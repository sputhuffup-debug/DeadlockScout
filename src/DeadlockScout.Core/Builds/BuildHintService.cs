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
        var items = await TryAsync(CacheKeys.ItemStats(heroId, bracket, patch),
            async c => (await _api.ItemStatsByNetWorthAsync(heroId, bracket, c).ConfigureAwait(false)).ToList(), "item stats", heroId, ct).ConfigureAwait(false);
        var abilities = await TryAsync(CacheKeys.AbilityOrder(heroId, bracket, patch),
            async c => (await _api.AbilityOrderStatsAsync(heroId, bracket, c).ConfigureAwait(false)).ToList(), "ability orders", heroId, ct).ConfigureAwait(false);
        return new BuildTables(items, abilities);
    }

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

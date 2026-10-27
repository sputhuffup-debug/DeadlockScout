using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Scouting;

/// <summary>
/// Lane matchup records for the lane card. Fetches the bracket's whole hero-versus-hero table once per patch and
/// answers every pair from it, so a lobby's matchups cost one call a week at most.
/// </summary>
public sealed class MatchupService
{
    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly IPatchKeyProvider _patch;
    private readonly TimeSpan _ttl;

    public MatchupService(IDeadlockApi api, ICache cache, IPatchKeyProvider patch, TimeSpan ttl)
    {
        _api = api;
        _cache = cache;
        _patch = patch;
        _ttl = ttl;
    }

    /// <summary>Your hero's lane record against the enemy hero, or null when the pair is below the sample floor.</summary>
    public async Task<HeroMatchup?> GetAsync(int heroId, int enemyHeroId, RankBracket bracket, CancellationToken ct)
    {
        if (heroId <= 0 || enemyHeroId <= 0) return null;
        var table = await TableAsync(bracket, ct).ConfigureAwait(false);
        return table.FirstOrDefault(m => m.HeroId == heroId && m.EnemyHeroId == enemyHeroId);
    }

    public async Task<IReadOnlyList<HeroMatchup>> TableAsync(RankBracket bracket, CancellationToken ct)
    {
        var patch = await _patch.GetAsync(ct).ConfigureAwait(false);
        return await _cache.GetOrCreateAsync(CacheKeys.Counters(bracket, patch), _ttl,
            async c => (await _api.HeroCounterStatsAsync(bracket, c).ConfigureAwait(false)).ToList(),
            staleWhileRevalidate: true, ct).ConfigureAwait(false);
    }
}

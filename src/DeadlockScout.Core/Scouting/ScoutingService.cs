using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Core.Scouting;

/// <summary>
/// Scouts a roster with at most one bulk hero-stats call (cache misses only) plus your own enemy/mate history,
/// which is cached for a day and shared across lobbies.
/// </summary>
public sealed class ScoutingService : IScoutingService
{
    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly ILogger _log;

    public ScoutingService(IDeadlockApi api, ICache cache, IOptionsMonitor<ScoutSettings> settings, ILogger<ScoutingService>? log = null)
    {
        _api = api;
        _cache = cache;
        _settings = settings;
        _log = log ?? NullLogger<ScoutingService>.Instance;
    }

    public async Task<IReadOnlyList<ScoutRow>> ScoutAsync(Roster roster, uint myAccountId, CancellationToken ct)
    {
        var cfg = _settings.CurrentValue;
        var ttl = TimeSpan.FromHours(cfg.Cache.PlayerStatsTtlHours);

        // 1. Collect cache misses and fetch them in one call.
        var stats = new Dictionary<uint, List<PlayerHeroStats>>();
        var misses = new List<uint>();
        foreach (var p in roster.Players)
        {
            var cached = await _cache.GetAsync<List<PlayerHeroStats>>(CacheKeys.PlayerHeroStats(p.AccountId), ct).ConfigureAwait(false);
            if (cached is not null) stats[p.AccountId] = cached; else misses.Add(p.AccountId);
        }
        if (misses.Count > 0)
        {
            try
            {
                var fetched = await _api.HeroStatsAsync(misses.Distinct(), ct).ConfigureAwait(false);
                foreach (var g in fetched.GroupBy(s => s.AccountId))
                    stats[g.Key] = g.ToList();
                foreach (var id in misses)
                {
                    // An account the API knows nothing about is cached as an empty list, so a private profile costs one call per TTL.
                    stats.TryAdd(id, new List<PlayerHeroStats>());
                    await _cache.SetAsync(CacheKeys.PlayerHeroStats(id), stats[id], ttl, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "hero-stats failed for {Count} accounts; rows will show as unknown", misses.Count);
            }
        }

        // 2. Your shared history, one cached pair of calls.
        var sharedTtl = TimeSpan.FromHours(cfg.Cache.SharedHistoryTtlHours);
        var enemies = await TryGetSharedAsync(CacheKeys.EnemyStats(myAccountId), sharedTtl, c => _api.EnemyStatsAsync(myAccountId, c), ct).ConfigureAwait(false);
        var mates = await TryGetSharedAsync(CacheKeys.MateStats(myAccountId), sharedTtl, c => _api.MateStatsAsync(myAccountId, c), ct).ConfigureAwait(false);

        // 3. Rows.
        int? median = ScoutRules.LobbyMedianRank(roster.Players);
        var rows = new List<ScoutRow>(roster.Players.Count);
        foreach (var p in roster.Players)
        {
            bool hasData = stats.TryGetValue(p.AccountId, out var list) && list.Count > 0;
            var onHero = hasData ? list!.FirstOrDefault(s => s.HeroId == p.HeroId) : null;
            int gamesOnHero = onHero?.Matches ?? 0;
            double wr = onHero?.WinRate ?? 0;
            int total = hasData ? list!.Sum(s => s.Matches) : 0;

            (int Wins, int Losses)? record = null;
            int? met = null;
            if (p.AccountId != myAccountId)
            {
                var e = enemies?.FirstOrDefault(s => s.AccountId == p.AccountId);
                var m = mates?.FirstOrDefault(s => s.AccountId == p.AccountId);
                if (e is not null || m is not null)
                {
                    met = (e?.Matches ?? 0) + (m?.Matches ?? 0);
                    record = ((e?.Wins ?? 0) + (m?.Wins ?? 0), (e?.Losses ?? 0) + (m?.Losses ?? 0));
                }
                else if (enemies is not null || mates is not null)
                {
                    met = 0;
                }
            }

            var flags = p.AccountId == myAccountId
                ? ScoutFlags.None
                : ScoutRules.Compute(cfg.Flags, p, hasData, gamesOnHero, wr, total, median, met);

            rows.Add(new ScoutRow(p, gamesOnHero, wr, total, p.Rank, met, ScoutRules.Label(flags))
            {
                Flags = flags,
                MetRecord = record,
            });
        }
        return rows;
    }

    private async Task<IReadOnlyList<SharedHistory>?> TryGetSharedAsync(string key, TimeSpan ttl,
        Func<CancellationToken, Task<IReadOnlyList<SharedHistory>>> fetch, CancellationToken ct)
    {
        try
        {
            return await _cache.GetOrCreateAsync(key, ttl, fetch, staleWhileRevalidate: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "shared history {Key} unavailable", key);
            return null;
        }
    }
}

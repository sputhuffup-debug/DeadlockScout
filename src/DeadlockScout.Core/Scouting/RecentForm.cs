using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Core.Scouting;

/// <summary>
/// A player's last games on one hero: their record and souls per minute, against their souls per minute on that hero
/// over every recorded match (<paramref name="CareerSpm"/>), so "on form" or "off form" reads at a glance.
/// </summary>
public sealed record RecentForm(int Games, int Wins, int Losses, double SoulsPerMinute, double? CareerSpm)
{
    /// <summary>(recent / career) - 1: +0.07 means they are farming 7% faster lately than usual on this hero.</summary>
    public double? Delta => CareerSpm is > 0 && Games > 0 ? SoulsPerMinute / CareerSpm.Value - 1 : null;

    /// <summary>
    /// From history rows (any order): the newest <paramref name="count"/> scored games on <paramref name="heroId"/>.
    /// Souls per minute is pooled (all souls over all minutes), so a short stomp does not outweigh a long game.
    /// </summary>
    public static RecentForm From(IEnumerable<MatchHistoryEntry> history, int heroId, double? careerSpm, int count = 10)
    {
        var games = Last(history, heroId, count);
        double minutes = games.Sum(g => g.Duration.TotalMinutes);
        return new RecentForm(games.Count, games.Count(g => g.IsWin), games.Count(g => g.IsLoss),
            minutes > 0 ? games.Sum(g => (double)g.NetWorth) / minutes : 0, careerSpm);
    }

    /// <summary>The newest <paramref name="count"/> games on the hero that lasted at least five minutes (left early or remade games say little).</summary>
    public static IReadOnlyList<MatchHistoryEntry> Last(IEnumerable<MatchHistoryEntry> history, int heroId, int count = 10)
        => history.Where(e => e.HeroId == heroId && e.Duration >= TimeSpan.FromMinutes(5))
                  .OrderByDescending(e => e.StartTime).Take(count).ToList();
}

/// <summary>
/// Lane opponents' recent form: one match-history call per opponent, of which only the last ten games on their hero
/// are cached (a day, the shared-history TTL); the record and pace are computed on read. The call counts against the
/// match-history rate limit, which is low, so it is only made for your lane opponents.
/// </summary>
public sealed class RecentFormService
{
    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly IOptionsMonitor<ScoutSettings> _settings;

    public RecentFormService(IDeadlockApi api, ICache cache, IOptionsMonitor<ScoutSettings> settings)
    {
        _api = api;
        _cache = cache;
        _settings = settings;
    }

    public async Task<RecentForm> GetAsync(uint accountId, int heroId, double? careerSpm, CancellationToken ct)
    {
        var ttl = TimeSpan.FromHours(_settings.CurrentValue.Cache.SharedHistoryTtlHours);
        var games = await _cache.GetOrCreateAsync(CacheKeys.RecentForm(accountId, heroId), ttl,
            async c => RecentForm.Last(await _api.MatchHistoryAsync(accountId, c).ConfigureAwait(false), heroId).ToList(),
            staleWhileRevalidate: true, ct).ConfigureAwait(false);
        return RecentForm.From(games, heroId, careerSpm);
    }
}

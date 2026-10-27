using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Core.Session;

/// <summary>What the session view shows.</summary>
public sealed record SessionView(SessionSummary Today, string? Nudge, IReadOnlyList<HeroPoolRow> HeroPool, RankBracket Bracket);

/// <summary>
/// Builds the session view from your match history, your per-hero totals and the bracket's hero meta. Every source is
/// cached (history 10 min, hero totals with the scouting TTL, meta a day per patch), so a refresh costs at most one call
/// each. The hero pool is left empty when its sources fail; today's results still show.
/// </summary>
public sealed class SessionService
{
    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly IPatchKeyProvider _patch;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly TimeZoneInfo _zone;
    private readonly ILogger _log;

    public SessionService(IDeadlockApi api, ICache cache, IPatchKeyProvider patch, IClock clock, IOptionsMonitor<ScoutSettings> settings,
                          TimeZoneInfo? zone = null, ILogger<SessionService>? log = null)
    {
        _api = api;
        _cache = cache;
        _patch = patch;
        _clock = clock;
        _settings = settings;
        _zone = zone ?? TimeZoneInfo.Local;
        _log = log ?? NullLogger<SessionService>.Instance;
    }

    public async Task<SessionView?> RefreshAsync(uint me, CancellationToken ct)
    {
        if (me == 0) return null;
        var cfg = _settings.CurrentValue;
        var history = await _cache.GetOrCreateAsync(CacheKeys.MatchHistory(me), TimeSpan.FromMinutes(10),
            async c => (await _api.MatchHistoryAsync(me, c).ConfigureAwait(false)).ToList(), staleWhileRevalidate: true, ct).ConfigureAwait(false);
        var today = SessionMath.Summarize(history, _clock.UtcNow, _zone, cfg.Session.DayStartsAtHour);
        var bracket = BracketPolicy.Resolve(cfg.Meter, today.BadgeNow);

        IReadOnlyList<HeroPoolRow> pool = Array.Empty<HeroPoolRow>();
        try
        {
            var mine = await _cache.GetOrCreateAsync(CacheKeys.PlayerHeroStats(me), TimeSpan.FromHours(cfg.Cache.PlayerStatsTtlHours),
                async c => (await _api.HeroStatsAsync(new[] { me }, c).ConfigureAwait(false)).Where(s => s.AccountId == me).ToList(),
                staleWhileRevalidate: true, ct).ConfigureAwait(false);
            var patch = await _patch.GetAsync(ct).ConfigureAwait(false);
            var meta = await _cache.GetOrCreateAsync(CacheKeys.MetaHeroes(bracket, patch), TimeSpan.FromDays(1),
                async c => (await _api.AnalyticsHeroStatsAsync(new AnalyticsQuery(MinAverageBadge: bracket.MinBadge, MaxAverageBadge: bracket.MaxBadge), c)
                    .ConfigureAwait(false)).ToList(), staleWhileRevalidate: true, ct).ConfigureAwait(false);
            pool = SessionMath.HeroPool(mine, meta, cfg.Session.HeroPoolSize);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.LogDebug(ex, "Hero pool unavailable"); }

        return new SessionView(today, SessionMath.Nudge(today, cfg.Session.LossStreakNudge), pool, bracket);
    }
}

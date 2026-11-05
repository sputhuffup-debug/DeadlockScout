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
public sealed record SessionView(SessionSummary Today, string? Nudge, IReadOnlyList<HeroPoolRow> HeroPool, RankBracket Bracket,
                                 IReadOnlyList<TeammateRow>? Teammates = null, RankForecast? Forecast = null);

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
    private readonly IPaceBenchmark? _gate;

    /// <param name="gate">
    /// The rank gate's curves (the analytics provider: one cached pass per bracket answers every hero), for the hero
    /// pool's pace; null leaves it out.
    /// </param>
    public SessionService(IDeadlockApi api, ICache cache, IPatchKeyProvider patch, IClock clock, IOptionsMonitor<ScoutSettings> settings,
                          TimeZoneInfo? zone = null, ILogger<SessionService>? log = null, IPaceBenchmark? gate = null)
    {
        _gate = gate;
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

        // Each hero's recent pace against the rank gate, and today's last games against it for the break nudge; the pool
        // and the loss-streak nudge still show without them.
        string? nudge = SessionMath.Nudge(today, cfg.Session.LossStreakNudge);
        if (_gate is not null)
            try
            {
                int tier = RankGoal.Resolve(cfg.Goal, today.BadgeNow);
                var curves = new Dictionary<int, PaceCurve?>();
                int slide = Math.Max(0, cfg.Session.PaceSlideNudge);
                var heroes = pool.Select(r => r.HeroId)
                    .Concat(history.OrderByDescending(h => h.StartTime).Take(slide * 2).Select(h => h.HeroId)).Distinct();
                foreach (var hero in heroes) curves[hero] = await _gate.GetAsync(hero, RankGoal.Bracket(tier), ct).ConfigureAwait(false);
                pool = SessionMath.WithPace(pool, history, h => curves.GetValueOrDefault(h), RankGoal.Name(tier));
                nudge ??= SessionMath.PaceNudge(SessionMath.RecentVersusGate(history, h => curves.GetValueOrDefault(h), slide), today.DayStart, slide, RankGoal.Name(tier));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogDebug(ex, "Hero pool pace unavailable"); }

        IReadOnlyList<TeammateRow> mates = Array.Empty<TeammateRow>();
        if (cfg.Session.Teammates > 0)
            try { mates = await TeammatesAsync(me, history, cfg, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogDebug(ex, "Teammate record unavailable"); }

        return new SessionView(today, nudge, pool, bracket, mates, RankForecasting.From(history));
    }

    /// <summary>
    /// Your party mates (cached a day) and their names: each name cached a week, fetched in one call for the ones not
    /// cached yet. Only the display name is kept.
    /// </summary>
    private async Task<IReadOnlyList<TeammateRow>> TeammatesAsync(uint me, IReadOnlyList<MatchHistoryEntry> history, ScoutSettings cfg, CancellationToken ct)
    {
        var party = await _cache.GetOrCreateAsync(CacheKeys.PartyMateGames(me), TimeSpan.FromDays(1),
            async c => (await _api.PartyMateGamesAsync(me, c).ConfigureAwait(false)).ToList(), staleWhileRevalidate: true, ct).ConfigureAwait(false);
        var rows = SessionMath.Teammates(party, history, new Dictionary<uint, string>(), cfg.Session.Teammates);
        if (rows.Count == 0) return rows;

        var names = new Dictionary<uint, string>();
        var missing = new List<uint>();
        foreach (var r in rows)
        {
            if (await _cache.GetAsync<string>(CacheKeys.PlayerName(r.AccountId), ct).ConfigureAwait(false) is { } cached) names[r.AccountId] = cached;
            else missing.Add(r.AccountId);
        }
        if (missing.Count > 0)
            foreach (var n in await _api.PlayerNamesAsync(missing, ct).ConfigureAwait(false))
            {
                names[n.AccountId] = n.Name;
                await _cache.SetAsync(CacheKeys.PlayerName(n.AccountId), n.Name, TimeSpan.FromDays(7), ct).ConfigureAwait(false);
            }
        return rows.Select(r => r with { Name = names.GetValueOrDefault(r.AccountId) }).ToList();
    }
}

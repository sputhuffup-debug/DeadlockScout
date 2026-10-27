using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Api.Benchmarks;

/// <summary>
/// Priority 3, optional view: your own last N games on the hero (match-history, then metadata per match, background
/// priority, each immutable and cached forever). Invalidated by a new match on the hero.
/// </summary>
public sealed class PersonalBenchmark : IPaceBenchmark
{
    public const string ProviderName = "personal";

    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly Func<uint> _me;

    public PersonalBenchmark(IDeadlockApi api, ICache cache, IOptionsMonitor<ScoutSettings> settings, Func<uint> myAccountId)
    {
        _api = api;
        _cache = cache;
        _settings = settings;
        _me = myAccountId;
    }

    public int Priority => 3;
    public string Name => ProviderName;

    public async Task<PaceCurve?> GetAsync(int heroId, RankBracket bracket, CancellationToken ct)
    {
        uint me = _me();
        if (me == 0) return null;
        var history = await _cache.GetOrCreateAsync(CacheKeys.MatchHistory(me), TimeSpan.FromMinutes(10),
            async c => (await _api.MatchHistoryAsync(me, c).ConfigureAwait(false)).ToList(), staleWhileRevalidate: true, ct).ConfigureAwait(false);
        var cfg = _settings.CurrentValue.Meter;
        var since = DateTimeOffset.UtcNow - TimeSpan.FromDays(Math.Max(1, cfg.PersonalMaxAgeDays));
        // Recent, completed games only: a game you left stops early and would drag the curve down.
        var onHero = history.Where(h => h.HeroId == heroId && h.StartTime >= since && !h.Abandoned && (h.IsWin || h.IsLoss))
            .Take(cfg.PersonalSampleSize).ToList();
        if (onHero.Count < 3) return null;

        var key = CacheKeys.PersonalCurve(heroId, me) + $":{onHero[0].MatchId}";
        var curve = await _cache.GetOrCreateAsync<PaceCurve?>(key, null, async c =>
        {
            var series = new List<IReadOnlyList<NetWorthPoint>>();
            foreach (var h in onHero)
            {
                MatchMetadata? meta = null;
                try
                {
                    meta = await _cache.GetOrCreateAsync(CacheKeys.MatchMetadata(h.MatchId), null, cc => _api.MatchMetadataAsync(h.MatchId, cc), false, c).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !c.IsCancellationRequested)
                {
                    continue; // one unavailable match (503, a long 429, a timeout) does not sink the curve
                }
                var mine = meta?.Players.FirstOrDefault(p => p.AccountId == me);
                if (mine is { NetWorthSeries.Count: >= 2 }) series.Add(mine.NetWorthSeries);
            }
            var built = CurveBuilder.FromSeries(heroId, RankBracket.All, "personal", ProviderName, series, minMatches: 3);
            return built.Points.Count > 1 ? built : null; // too few: not cached, so the next pass tries again
        }, staleWhileRevalidate: false, ct).ConfigureAwait(false);
        return curve;
    }
}

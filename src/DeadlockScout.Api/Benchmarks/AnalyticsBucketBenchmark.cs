using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Api.Benchmarks;

/// <summary>
/// Priority 1, instant: analytics/hero-stats filtered by badge range and duration buckets 10–15 … 45–50 min. Mean final
/// net worth per bucket, plotted at the bucket midpoint, gives a monotone mean-only curve. The endpoint has no hero
/// filter, so the 8 calls for one bracket produce curves for every hero at once; they are cached per hero for the patch.
/// </summary>
public sealed class AnalyticsBucketBenchmark : IPaceBenchmark
{
    public const string ProviderName = "analytics";

    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly IPatchKeyProvider _patch;
    private readonly IOptionsMonitor<ScoutSettings> _settings;

    public AnalyticsBucketBenchmark(IDeadlockApi api, ICache cache, IPatchKeyProvider patch, IOptionsMonitor<ScoutSettings> settings)
    {
        _api = api;
        _cache = cache;
        _patch = patch;
        _settings = settings;
    }

    public int Priority => 1;
    public string Name => ProviderName;

    public async Task<PaceCurve?> GetAsync(int heroId, RankBracket bracket, CancellationToken ct)
    {
        var patch = await _patch.GetAsync(ct).ConfigureAwait(false);
        var ttl = TimeSpan.FromDays(_settings.CurrentValue.Cache.BenchmarkTtlDays);
        var key = CacheKeys.AnalyticsCurve(heroId, bracket, patch);
        var cached = await _cache.GetAsync<PaceCurve>(key, ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        // One pass over the buckets fills the cache for every hero; the requested one is returned.
        var all = await _cache.GetOrCreateAsync($"pace:a:all:{bracket}:{patch}", ttl, c => FetchAllAsync(bracket, patch, c), staleWhileRevalidate: true, ct).ConfigureAwait(false);
        foreach (var curve in all)
            await _cache.SetAsync(CacheKeys.AnalyticsCurve(curve.HeroId, bracket, patch), curve, ttl, ct).ConfigureAwait(false);
        return all.FirstOrDefault(c => c.HeroId == heroId && c.Points.Count > 1);
    }

    private async Task<List<PaceCurve>> FetchAllAsync(RankBracket bracket, string patch, CancellationToken ct)
    {
        var byHero = new Dictionary<int, List<(int Minute, double Mean, int Matches)>>();
        foreach (var (from, to) in CurveBuilder.DurationBuckets)
        {
            var rows = await _api.AnalyticsHeroStatsAsync(new AnalyticsQuery(
                MinDurationSeconds: from * 60, MaxDurationSeconds: to * 60,
                MinAverageBadge: bracket.MinBadge, MaxAverageBadge: bracket.MaxBadge), ct).ConfigureAwait(false);
            int midpoint = (from + to) / 2;
            foreach (var r in rows)
            {
                if (r.Matches <= 0) continue;
                if (!byHero.TryGetValue(r.HeroId, out var list)) byHero[r.HeroId] = list = new();
                list.Add((midpoint, r.MeanNetWorth, r.Matches));
            }
        }
        return byHero.Select(kv => CurveBuilder.FromBucketMeans(kv.Key, bracket, patch, ProviderName, kv.Value)).ToList();
    }
}

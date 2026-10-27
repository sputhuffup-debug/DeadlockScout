using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Api.Benchmarks;

/// <summary>
/// Priority 2, accurate: ~40 recent matches of the hero in the bracket via one bulk metadata call with per-player
/// net-worth series, reduced to P25/P50/P75/mean per minute. Background priority, cached for the patch.
/// </summary>
public sealed class MetadataSampleBenchmark : IPaceBenchmark
{
    public const string ProviderName = "metadata";

    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly IPatchKeyProvider _patch;
    private readonly IOptionsMonitor<ScoutSettings> _settings;

    public MetadataSampleBenchmark(IDeadlockApi api, ICache cache, IPatchKeyProvider patch, IOptionsMonitor<ScoutSettings> settings)
    {
        _api = api;
        _cache = cache;
        _patch = patch;
        _settings = settings;
    }

    public int Priority => 2;
    public string Name => ProviderName;

    public async Task<PaceCurve?> GetAsync(int heroId, RankBracket bracket, CancellationToken ct)
    {
        var patch = await _patch.GetAsync(ct).ConfigureAwait(false);
        var cfg = _settings.CurrentValue;
        var curve = await _cache.GetOrCreateAsync(CacheKeys.MetadataCurve(heroId, bracket, patch), TimeSpan.FromDays(cfg.Cache.BenchmarkTtlDays),
            c => BuildAsync(heroId, bracket, patch, cfg.Meter.MetadataSampleSize, c), staleWhileRevalidate: true, ct).ConfigureAwait(false);
        return curve.Points.Count > 1 ? curve : null;
    }

    private async Task<PaceCurve> BuildAsync(int heroId, RankBracket bracket, string patch, int sampleSize, CancellationToken ct)
    {
        var matches = await _api.SampleMatchesAsync(heroId, bracket, sampleSize, ct).ConfigureAwait(false);
        return Build(heroId, bracket, patch, matches);
    }

    /// <summary>Pure reduction, for tests.</summary>
    public static PaceCurve Build(int heroId, RankBracket bracket, string patch, IReadOnlyList<MatchMetadata> matches)
    {
        var series = matches
            .SelectMany(m => m.Players.Where(p => p.HeroId == heroId && p.NetWorthSeries.Count >= 2).Select(p => p.NetWorthSeries))
            .ToList();
        return CurveBuilder.FromSeries(heroId, bracket, patch, ProviderName, series);
    }
}

using DeadlockScout.Api.Benchmarks;
using DeadlockScout.Api.Caching;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using DeadlockScout.Tests.Core;

namespace DeadlockScout.Tests.Api;

public class BenchmarkProviderTests
{
    private sealed class FixedPatch : IPatchKeyProvider { public ValueTask<string> GetAsync(CancellationToken ct) => ValueTask.FromResult("v1"); }

    private sealed class AnalyticsApi : ScoutingServiceTests.FakeApiBase
    {
        public readonly List<AnalyticsQuery> Queries = new();
        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<AnalyticsHeroStats>> AnalyticsHeroStatsAsync(AnalyticsQuery q, CancellationToken ct)
        {
            Queries.Add(q);
            int mid = (q.MinDurationSeconds!.Value + q.MaxDurationSeconds!.Value) / 120; // bucket midpoint in minutes
            // hero 1 earns 700/min, hero 2 earns 500/min; hero 2 has no games in the longest bucket
            var rows = new List<AnalyticsHeroStats> { new(1, 100, 50, 100L * 700 * mid, 0, 0, 0) };
            if (mid < 47) rows.Add(new(2, 30, 15, 30L * 500 * mid, 0, 0, 0));
            return Task.FromResult<IReadOnlyList<AnalyticsHeroStats>>(rows);
        }
        public override Task<IReadOnlyList<MatchMetadata>> SampleMatchesAsync(int heroId, RankBracket bracket, int limit, CancellationToken ct)
        {
            var matches = new List<MatchMetadata>();
            for (int i = 0; i < limit; i++)
            {
                var series = Enumerable.Range(0, 21).Select(m => new NetWorthPoint(TimeSpan.FromMinutes(m), (600 + 10 * i) * m)).ToList();
                var players = new List<MatchPlayer> { new(500 + (uint)i, heroId, Teams.Amber, 0, 0, 0, series[^1].NetWorth, null, null, series), new(900, 99, Teams.Sapphire, 0, 0, 0, 0, null, null, Array.Empty<NetWorthPoint>()) };
                matches.Add(new MatchMetadata(1000 + i, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(20), Teams.Amber, 70, 70, players));
            }
            return Task.FromResult<IReadOnlyList<MatchMetadata>>(matches);
        }
    }

    private static ApiClientTests.StaticOptions Options() => new(new ScoutSettings());

    [Fact]
    public async Task Analytics_benchmark_makes_eight_calls_per_bracket_and_serves_every_hero_from_cache()
    {
        var api = new AnalyticsApi();
        var cache = new ScoutingServiceTests.MemoryCacheStub();
        var bench = new AnalyticsBucketBenchmark(api, cache, new FixedPatch(), Options());
        var bracket = new RankBracket(60, 80);

        var c1 = await bench.GetAsync(1, bracket, default);
        Assert.Equal(8, api.Queries.Count);
        Assert.All(api.Queries, q => { Assert.Equal(60, q.MinAverageBadge); Assert.Equal(80, q.MaxAverageBadge); });
        Assert.Equal(new[] { 600, 900, 1200, 1500, 1800, 2100, 2400, 2700 }, api.Queries.Select(q => q.MinDurationSeconds!.Value));
        Assert.NotNull(c1);
        Assert.Equal(AnalyticsBucketBenchmark.ProviderName, c1!.Provider);
        Assert.Equal(800, c1.SampleSize);
        Assert.Equal(9, c1.Points.Count); // 0 + 8 buckets
        Assert.Equal(700 * 12, c1.Points[1].Mean);
        Assert.Equal(700 * 12, PaceMath.NetWorthAt(c1, TimeSpan.FromMinutes(12)), 1);
        Assert.Equal(700, PaceMath.ExpectedSpm(c1, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2)), 1);

        var c2 = await bench.GetAsync(2, bracket, default);
        Assert.Equal(8, api.Queries.Count); // no new calls
        Assert.Equal(8, c2!.Points.Count);   // hero 2 missing from the last bucket
        Assert.Equal(30 * 7, c2.SampleSize);
        Assert.Null(await bench.GetAsync(3, bracket, default));
    }

    [Fact]
    public async Task Metadata_benchmark_reduces_one_bulk_call_to_percentiles()
    {
        var api = new AnalyticsApi();
        var bench = new MetadataSampleBenchmark(api, new ScoutingServiceTests.MemoryCacheStub(), new FixedPatch(), Options());
        var curve = await bench.GetAsync(1, new RankBracket(60, 80), default);
        Assert.NotNull(curve);
        Assert.True(curve!.HasPercentiles);
        Assert.Equal(40, curve.SampleSize);
        var m10 = curve.Points.Single(p => p.Minute == 10);
        // rates 600..990 step 10 at minute 10 → P50 ≈ 7950, P25 ≈ 6975, P75 ≈ 8925
        Assert.InRange(m10.P50!.Value, 7900, 8000);
        Assert.InRange(m10.P25!.Value, 6900, 7050);
        Assert.InRange(m10.P75!.Value, 8850, 9000);
        Assert.Equal(20, curve.Points[^1].Minute);
        Assert.InRange(PaceMath.Percentile(curve, TimeSpan.FromMinutes(10), 7950)!.Value, 48, 52);
    }

    [Fact]
    public async Task Composite_returns_analytics_first_then_upgrades_to_metadata()
    {
        var api = new AnalyticsApi();
        var cache = new ScoutingServiceTests.MemoryCacheStub();
        var composite = new CompositeBenchmark(new IPaceBenchmark[]
        {
            new MetadataSampleBenchmark(api, cache, new FixedPatch(), Options()),
            new AnalyticsBucketBenchmark(api, cache, new FixedPatch(), Options()),
        });
        var providers = new List<string>();
        await foreach (var c in composite.WatchAsync(1, new RankBracket(60, 80), default)) providers.Add(c.Provider);
        Assert.Equal(new[] { "analytics", "metadata" }, providers);
    }

    [Fact]
    public async Task Client_version_patch_key_uses_steam_info_and_falls_back_to_the_date()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero));
        var good = new SteamInfoApi(new SteamInfo(5901, 5901, null));
        var key = await new ClientVersionPatchKeyProvider(good, new ScoutingServiceTests.MemoryCacheStub(), new DatePatchKeyProvider(clock)).GetAsync(default);
        Assert.Equal("v5901", key);
        var bad = new SteamInfoApi(null);
        var fallback = await new ClientVersionPatchKeyProvider(bad, new ScoutingServiceTests.MemoryCacheStub(), new DatePatchKeyProvider(clock)).GetAsync(default);
        Assert.StartsWith("w", fallback);
    }

    private sealed class SteamInfoApi(SteamInfo? info) : ScoutingServiceTests.FakeApiBase
    {
        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<SteamInfo?> SteamInfoAsync(CancellationToken ct) => info is null ? throw new HttpRequestException("down") : Task.FromResult<SteamInfo?>(info);
    }
}

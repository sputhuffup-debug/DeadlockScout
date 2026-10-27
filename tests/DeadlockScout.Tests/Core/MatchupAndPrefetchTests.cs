using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Scouting;
using DeadlockScout.Core.Settings;
using DeadlockScout.Tests.Api;

namespace DeadlockScout.Tests.Core;

public class MatchupAndPrefetchTests
{
    private sealed class Patch(string key) : IPatchKeyProvider
    {
        public string Key = key;
        public ValueTask<string> GetAsync(CancellationToken ct) => ValueTask.FromResult(Key);
    }

    private sealed class CounterApi : ScoutingServiceTests.FakeApiBase
    {
        public readonly List<RankBracket> CounterCalls = new();
        public int HistoryCalls, RankCalls;
        public IReadOnlyList<MatchHistoryEntry> History = Array.Empty<MatchHistoryEntry>();
        public int Badge = 75;

        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<HeroMatchup>> HeroCounterStatsAsync(RankBracket bracket, CancellationToken ct)
        {
            CounterCalls.Add(bracket);
            return Task.FromResult<IReadOnlyList<HeroMatchup>>(new[] { new HeroMatchup(13, 4, 2572, 1368), new HeroMatchup(4, 13, 2572, 1204) });
        }
        public override Task<IReadOnlyList<MatchHistoryEntry>> MatchHistoryAsync(uint accountId, CancellationToken ct) { HistoryCalls++; return Task.FromResult(History); }
        public override Task<IReadOnlyList<PlayerRank>> RanksAsync(IEnumerable<uint> accountIds, CancellationToken ct)
        {
            RankCalls++;
            return Task.FromResult<IReadOnlyList<PlayerRank>>(accountIds.Select(id => new PlayerRank(id, Badge, Badge / 10, Badge % 10)).ToList());
        }
    }

    private sealed class RecordingBenchmark(string name, int priority, bool fail = false) : IPaceBenchmark
    {
        public readonly List<(int Hero, RankBracket Bracket)> Calls = new();
        public int Priority => priority;
        public string Name => name;
        public Task<PaceCurve?> GetAsync(int heroId, RankBracket bracket, CancellationToken ct)
        {
            Calls.Add((heroId, bracket));
            if (fail) throw new HttpRequestException("boom");
            return Task.FromResult<PaceCurve?>(new PaceCurve(heroId, bracket, "v1", new[] { new PacePoint(0, null, null, null, 0), new PacePoint(10, null, null, null, 9000) }, 40, name));
        }
    }

    private static MatchHistoryEntry Game(long id, int hero, int daysAgo)
        => new(id, hero, DateTimeOffset.UtcNow.AddDays(-daysAgo).AddMinutes(-id), TimeSpan.FromMinutes(30), 2, 1, 1, 1, 20000, 1, null, null);

    // ---- BracketPolicy ----

    [Theory]
    [InlineData("auto", 75, 65, 85)]
    [InlineData("auto", 5, 0, 15)]       // clamped at the bottom
    [InlineData("auto", 112, 102, 116)]  // and at the top
    [InlineData("auto", null, 0, 116)]   // unknown badge: everyone
    [InlineData("all", 75, 0, 116)]
    [InlineData("60-80", 30, 60, 80)]
    [InlineData("80-60", 75, 65, 85)]    // reversed range is ignored, so auto applies
    [InlineData("junk", 75, 65, 85)]
    public void Bracket_policy(string setting, int? badge, int min, int max)
    {
        var cfg = new MeterSettings { BenchmarkBracket = setting, BracketWidth = 10 };
        Assert.Equal(new RankBracket(min, max), BracketPolicy.Resolve(cfg, badge));
    }

    // ---- MatchupService ----

    [Fact]
    public async Task Matchups_fetch_the_bracket_table_once_and_answer_every_pair_from_it()
    {
        var api = new CounterApi();
        var patch = new Patch("v1");
        var svc = new MatchupService(api, new ScoutingServiceTests.MemoryCacheStub(), patch, TimeSpan.FromDays(7));
        var bracket = new RankBracket(65, 85);

        Assert.Equal(new HeroMatchup(13, 4, 2572, 1368), await svc.GetAsync(13, 4, bracket, default));
        Assert.Equal(1204, (await svc.GetAsync(4, 13, bracket, default))!.Wins);
        Assert.Null(await svc.GetAsync(13, 7, bracket, default)); // below the sample floor, so absent
        Assert.Null(await svc.GetAsync(0, 4, bracket, default));  // unknown hero: no call needed
        Assert.Single(api.CounterCalls);

        patch.Key = "v2"; // a new patch reads a new table
        await svc.GetAsync(13, 4, bracket, default);
        Assert.Equal(2, api.CounterCalls.Count);
    }

    // ---- BenchmarkWarmer ----

    [Fact]
    public void Picks_the_most_played_recent_heroes_breaking_ties_by_recency()
    {
        var history = new[]
        {
            Game(1, 13, 0), Game(2, 13, 1), Game(3, 13, 2), // Haze ×3
            Game(4, 7, 0), Game(5, 7, 3),                   // Wraith ×2, played today
            Game(6, 2, 4), Game(7, 2, 5),                   // Seven ×2, older
            Game(8, 1, 6), Game(9, 0, 0),                   // Infernus ×1, hero 0 is ignored
        };
        Assert.Equal(new[] { 13, 7, 2 }, BenchmarkWarmer.PickHeroes(history, 3));
        Assert.Equal(new[] { 13 }, BenchmarkWarmer.PickHeroes(history, 1));
        Assert.Empty(BenchmarkWarmer.PickHeroes(history, 0));
        // only the most recent games count
        Assert.Equal(new[] { 13, 7 }, BenchmarkWarmer.PickHeroes(history, 5, recentGames: 4));
    }

    [Fact]
    public async Task Warmer_asks_every_provider_for_each_hero_in_your_bracket_and_survives_a_failing_one()
    {
        var api = new CounterApi { History = new[] { Game(1, 13, 0), Game(2, 13, 1), Game(3, 7, 2) }, Badge = 75 };
        var analytics = new RecordingBenchmark("analytics", 1);
        var metadata = new RecordingBenchmark("metadata", 2, fail: true);
        var personal = new RecordingBenchmark("personal", 3);
        var settings = new ScoutSettings { Meter = new MeterSettings { PrefetchHeroes = 2, BracketWidth = 10 } };
        var cache = new ScoutingServiceTests.MemoryCacheStub();
        var matchups = new MatchupService(api, cache, new Patch("v1"), TimeSpan.FromDays(7));
        var warmer = new BenchmarkWarmer(api, cache, new IPaceBenchmark[] { personal, metadata, analytics },
            new ApiClientTests.StaticOptions(settings), matchups);

        var result = await warmer.WarmAsync(1000, default);

        Assert.NotNull(result);
        var bracket = new RankBracket(65, 85);
        Assert.Equal(bracket, result!.Bracket);
        Assert.Equal(new[] { 13, 7 }, result.Heroes);
        Assert.Equal(4, result.CurvesReady); // analytics and personal for both heroes; metadata threw
        foreach (var p in new[] { analytics, metadata, personal })
            Assert.Equal(new[] { (13, bracket), (7, bracket) }, p.Calls);

        Assert.Equal(new[] { bracket }, api.CounterCalls); // the lane matchup table for the same bracket

        await warmer.WarmAsync(1000, default); // a second pass reads history, rank and matchups from the cache
        Assert.Equal(1, api.HistoryCalls);
        Assert.Equal(1, api.RankCalls);
        Assert.Single(api.CounterCalls);
    }

    [Fact]
    public async Task Warmer_does_nothing_without_an_account_or_when_turned_off()
    {
        var api = new CounterApi { History = new[] { Game(1, 13, 0) } };
        var provider = new RecordingBenchmark("analytics", 1);
        var on = new BenchmarkWarmer(api, new ScoutingServiceTests.MemoryCacheStub(), new[] { provider }, new ApiClientTests.StaticOptions(new ScoutSettings()));
        Assert.Null(await on.WarmAsync(0, default));

        var off = new BenchmarkWarmer(api, new ScoutingServiceTests.MemoryCacheStub(), new[] { provider },
            new ApiClientTests.StaticOptions(new ScoutSettings { Meter = new MeterSettings { PrefetchHeroes = 0 } }));
        Assert.Null(await off.WarmAsync(1000, default));

        Assert.Equal(0, api.HistoryCalls);
        Assert.Empty(provider.Calls);
    }
}

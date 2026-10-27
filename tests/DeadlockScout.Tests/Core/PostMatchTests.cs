using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.PostMatch;
using DeadlockScout.Core.Settings;
using DeadlockScout.Tests.Api;

namespace DeadlockScout.Tests.Core;

public class PostMatchTests
{
    private const uint Me = 1000;

    /// <summary>Net worth rising linearly by <paramref name="perMinute"/>, sampled every 3 minutes up to the duration.</summary>
    private static IReadOnlyList<NetWorthPoint> Series(int perMinute, int minutes)
        => Enumerable.Range(0, minutes / 3 + 1).Select(i => new NetWorthPoint(TimeSpan.FromMinutes(i * 3), i * 3 * perMinute)).ToList();

    private static MatchPlayer Player(uint id, int hero, int team, int finalNw, int? lane, int perMinute, int minutes = 33)
        => new(id, hero, team, 5, 2, 7, finalNw, null, lane, Series(perMinute, minutes));

    private static MatchMetadata Match(int winningTeam = Teams.Amber, int minutes = 33) => new(42, DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(minutes), winningTeam, 74, 76, new[]
    {
        Player(Me, 13, Teams.Amber, 30000, 1, 600, minutes),
        Player(1001, 1, Teams.Amber, 35000, 1, 650, minutes),
        Player(1002, 6, Teams.Amber, 20000, 4, 500, minutes),
        Player(2001, 4, Teams.Sapphire, 26000, 1, 480, minutes),
        Player(2002, 7, Teams.Sapphire, 28000, 1, 520, minutes),
        Player(2003, 2, Teams.Sapphire, 31000, 4, 560, minutes),
    });

    private static PaceCurve Curve(int perMinute = 550, bool percentiles = false)
        => new(13, new RankBracket(64, 86), "v1", Enumerable.Range(0, 9).Select(i => new PacePoint(i * 5,
            percentiles ? (int)(i * 5 * perMinute * 0.85) : null, percentiles ? i * 5 * perMinute : null, percentiles ? (int)(i * 5 * perMinute * 1.15) : null,
            i * 5 * perMinute)).ToList(), 40, "metadata");

    [Fact]
    public void Report_has_result_rank_pace_and_lane()
    {
        var r = PostMatchReporter.Build(Match(), Me, Curve(), new RankBracket(65, 85))!;
        Assert.True(r.Won);
        Assert.Equal((13, 30000, 2, 3), (r.HeroId, r.NetWorth, r.TeamNetWorthRank, r.TeamSize));
        Assert.Equal(30000 / 33.0, r.SoulsPerMinute, 6);
        Assert.Equal(new[] { 10, 20, 30 }, r.Pace.Select(p => p.Minute));
        var ten = r.Pace[0];
        Assert.Equal(6000, ten.NetWorth);
        Assert.Equal(5500, ten.Expected!.Value, 6);
        Assert.Equal(6000 / 5500.0 - 1, ten.Delta!.Value, 6);
        Assert.Null(ten.Percentile); // the curve has no spread
        Assert.Equal(("metadata", 40), (r.BenchmarkProvider, r.BenchmarkSampleSize));

        var lane = r.Lane!;
        Assert.Equal(new[] { 4, 7 }, lane.OpponentHeroIds); // the enemies in lane 1
        Assert.Equal(6000, lane.MyNetWorth);
        Assert.Equal(5000, lane.OpponentNetWorth); // average of 4,800 and 5,200
        Assert.Equal(1000, lane.Difference);
    }

    [Fact]
    public void Short_matches_and_losses_and_missing_curves_still_report()
    {
        var r = PostMatchReporter.Build(Match(winningTeam: Teams.Sapphire, minutes: 24), Me, curve: null, RankBracket.All)!;
        Assert.False(r.Won);
        Assert.Equal(new[] { 10, 20 }, r.Pace.Select(p => p.Minute)); // no 30:00 in a 24-minute match
        Assert.All(r.Pace, p => Assert.Null(p.Delta));
        Assert.Null(r.BenchmarkProvider);
        Assert.Null(PostMatchReporter.Build(Match(), 999, Curve(), RankBracket.All)); // not in the match
    }

    [Fact]
    public void Percentile_comes_from_a_curve_with_spread()
    {
        var r = PostMatchReporter.Build(Match(), Me, Curve(percentiles: true), RankBracket.All)!;
        Assert.InRange(r.Pace[0].Percentile!.Value, 51, 99); // 6,000 against a 5,500 median
    }

    [Fact]
    public void Net_worth_is_interpolated_and_never_extrapolated()
    {
        var s = new[] { new NetWorthPoint(TimeSpan.Zero, 0), new NetWorthPoint(TimeSpan.FromMinutes(3), 1800), new NetWorthPoint(TimeSpan.FromMinutes(6), 4200) };
        Assert.Equal(0, PostMatchReporter.NetWorthAt(s, TimeSpan.Zero));
        Assert.Equal(900, PostMatchReporter.NetWorthAt(s, TimeSpan.FromSeconds(90)));
        Assert.Equal(3000, PostMatchReporter.NetWorthAt(s, TimeSpan.FromSeconds(270)));
        Assert.Equal(4200, PostMatchReporter.NetWorthAt(s, TimeSpan.FromMinutes(6)));
        Assert.Null(PostMatchReporter.NetWorthAt(s, TimeSpan.FromMinutes(7)));
        Assert.Null(PostMatchReporter.NetWorthAt(Array.Empty<NetWorthPoint>(), TimeSpan.Zero));
    }

    [Fact]
    public void Lane_needs_a_lane_and_lane_opponents()
    {
        var meta = Match();
        Assert.Null(PostMatchReporter.Lane(meta, meta.Players[0] with { AssignedLane = null }));
        Assert.Null(PostMatchReporter.Lane(meta, meta.Players[0] with { AssignedLane = 7 }));
        Assert.Equal(75, PostMatchReporter.AverageBadge(meta));
        Assert.Null(PostMatchReporter.AverageBadge(meta with { AverageBadgeTeamAmber = 0, AverageBadgeTeamSapphire = 0 }));
    }

    // ---- service ----

    private sealed class MetaApi : ScoutingServiceTests.FakeApiBase
    {
        public MatchMetadata? Meta;
        public int MetaCalls;
        public readonly List<IReadOnlyList<MatchSalts>> Sent = new();
        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<MatchMetadata?> MatchMetadataAsync(long matchId, CancellationToken ct) { MetaCalls++; return Task.FromResult(Meta); }
        public override Task SubmitSaltsAsync(IEnumerable<MatchSalts> salts, CancellationToken ct) { Sent.Add(salts.ToList()); return Task.CompletedTask; }
    }

    private sealed class FixedBenchmark(PaceCurve? curve) : IPaceBenchmark
    {
        public RankBracket? Asked;
        public int Priority => 1;
        public string Name => "fixed";
        public Task<PaceCurve?> GetAsync(int heroId, RankBracket bracket, CancellationToken ct) { Asked = bracket; return Task.FromResult(curve); }
    }

    [Fact]
    public async Task Service_waits_for_the_api_to_have_the_match_then_caches_it()
    {
        var api = new MetaApi();
        var bench = new FixedBenchmark(Curve());
        var svc = new PostMatchService(api, new ScoutingServiceTests.MemoryCacheStub(), bench, new ApiClientTests.StaticOptions(new ScoutSettings()));

        Assert.Null(await svc.TryBuildAsync(42, Me, default)); // not processed yet
        api.Meta = Match();
        var r = await svc.TryBuildAsync(42, Me, default);
        Assert.NotNull(r);
        Assert.Equal(new RankBracket(65, 85), bench.Asked); // the match's average badge ± 10
        await svc.TryBuildAsync(42, Me, default);
        Assert.Equal(2, api.MetaCalls); // the null was not cached; the real metadata was
        Assert.Null(await svc.TryBuildAsync(42, 0, default));
    }

    // ---- salt ingest ----

    private sealed class Clock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow { get; set; } = now; }

    [Fact]
    public void Only_new_matches_with_a_salt_are_selected_newest_first()
    {
        var found = new[]
        {
            new MatchSalts(10, 1, 111, null), new MatchSalts(12, 1, null, 222), new MatchSalts(11, 1, null, null),
            new MatchSalts(13, 1, 333, 444), new MatchSalts(12, 1, 555, null),
        };
        var picked = SaltIngest.SelectNew(found, new long[] { 13 });
        Assert.Equal(new long[] { 12, 10 }, picked.Select(s => s.MatchId)); // 13 already sent, 11 has no salt, 12 once
    }

    [Fact]
    public async Task Ingest_sends_each_match_once_and_rescans_from_the_last_run()
    {
        var api = new MetaApi();
        var cache = new ScoutingServiceTests.MemoryCacheStub();
        var clock = new Clock(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
        var scans = new List<DateTimeOffset>();
        var inCache = new List<MatchSalts> { new(100, 1, 7, 8) };
        var ingest = new SaltIngest(api, cache, since => { scans.Add(since); return inCache.ToList(); }, clock, new ApiClientTests.StaticOptions(new ScoutSettings()));

        Assert.Equal(1, await ingest.RunAsync(default));
        Assert.Equal(clock.UtcNow - SaltIngest.FirstLookBack, scans[0]);

        clock.UtcNow += TimeSpan.FromHours(1);
        Assert.Equal(0, await ingest.RunAsync(default)); // the same match again: nothing sent
        Assert.Equal(clock.UtcNow - TimeSpan.FromHours(1) - SaltIngest.Overlap, scans[1]);

        inCache.Add(new MatchSalts(101, 1, 9, null));
        Assert.Equal(1, await ingest.RunAsync(default));
        Assert.Equal(new long[] { 100 }, api.Sent[0].Select(s => s.MatchId));
        Assert.Equal(new long[] { 101 }, api.Sent[1].Select(s => s.MatchId));
        Assert.Equal(2, api.Sent.Count);
    }

    [Fact]
    public async Task Ingest_does_nothing_when_turned_off()
    {
        var api = new MetaApi();
        bool scanned = false;
        var off = new ScoutSettings { Ingest = new IngestSettings { AutoSubmitSalts = false } };
        var ingest = new SaltIngest(api, new ScoutingServiceTests.MemoryCacheStub(), _ => { scanned = true; return Array.Empty<MatchSalts>(); },
            new Clock(DateTimeOffset.UtcNow), new ApiClientTests.StaticOptions(off));
        Assert.Equal(0, await ingest.RunAsync(default));
        Assert.False(scanned);
        Assert.Empty(api.Sent);
    }
}

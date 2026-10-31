using System.Text.Json;
using DeadlockScout.Api.Benchmarks;
using DeadlockScout.Api.Dto;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using DeadlockScout.Tests.Core;

namespace DeadlockScout.Tests.Api;

/// <summary>Fixes from the first run on a real machine (2026-10-27).</summary>
public class TestingFixesTests
{
    [Fact]
    public void Bulk_metadata_reads_the_flat_series_columns()
    {
        // The shape of /v1/matches/metadata?...&extra_player_columns=stats.time_stamp_s,stats.net_worth (live, trimmed).
        const string json = """
        [{"match_id": 9, "start_time": 1761500000, "duration_s": 1800, "winning_team": 1, "average_badge_team0": 70, "average_badge_team1": 72,
          "players": [
            {"account_id": 1, "hero_id": 13, "team": 0, "kills": 3, "deaths": 2, "assists": 5, "net_worth": 30000, "assigned_lane": 1,
             "stats_time_stamp_s": [180, 360, 540], "stats_net_worth": [1610, 3700, 5827], "player_tracked_stats": {}},
            {"account_id": 2, "hero_id": 7, "team": 1, "stats_time_stamp_s": [], "stats_net_worth": []}
          ]}]
        """;
        using var doc = JsonDocument.Parse(json);
        var m = MetadataParser.ParseBulk(doc.RootElement).Single();
        Assert.Equal(Teams.Sapphire, m.WinningTeam);
        var haze = m.Players.Single(p => p.HeroId == 13);
        // The first sample is three minutes in; the series starts at 0 souls at 0:00 so a soul timeline starts at the origin.
        Assert.Equal(new[] { new NetWorthPoint(TimeSpan.Zero, 0), new NetWorthPoint(TimeSpan.FromSeconds(180), 1610), new NetWorthPoint(TimeSpan.FromSeconds(360), 3700),
                             new NetWorthPoint(TimeSpan.FromSeconds(540), 5827) }, haze.NetWorthSeries);
        Assert.Empty(m.Players.Single(p => p.HeroId == 7).NetWorthSeries);
    }

    [Fact]
    public void Bulk_metadata_reads_teams_and_start_time_by_name()
    {
        // The bulk call names enums and dates (live, 2026-10-31); the single-match call gives 0/1 and unix seconds.
        const string json = """
        [{"match_id": 9, "start_time": "2026-10-31 00:06:01", "duration_s": 1800, "winning_team": "Team1", "match_outcome": "TeamWin",
          "players": [
            {"account_id": 1, "hero_id": 13, "team": "Team0", "player_match_outcome": "Loss"},
            {"account_id": 2, "hero_id": 7, "team": "Team1", "player_match_outcome": "Win"}
          ]}]
        """;
        using var doc = JsonDocument.Parse(json);
        var m = MetadataParser.ParseBulk(doc.RootElement).Single();
        Assert.Equal(Teams.Sapphire, m.WinningTeam);
        Assert.Equal(Teams.Amber, m.Players.Single(p => p.HeroId == 13).Team);
        Assert.Equal(Teams.Sapphire, m.Players.Single(p => p.HeroId == 7).Team);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 0, 6, 1, TimeSpan.Zero), m.StartTime);
    }

    private static MatchHistoryEntry Entry(long id, int team, int? winner, int outcome, TimeSpan? left = null, int hero = 50, int daysAgo = 1)
        => new(id, hero, DateTimeOffset.UtcNow.AddDays(-daysAgo).AddMinutes(-id), TimeSpan.FromMinutes(30), team, 0, 0, 0, 0, outcome, null, null, winner, left);

    [Fact]
    public void Wins_and_losses_come_from_the_winning_team_not_the_outcome_code()
    {
        Assert.True(Entry(1, Teams.Amber, Teams.Amber, outcome: 0).IsWin);   // most older games: outcome unset
        Assert.True(Entry(2, Teams.Amber, Teams.Sapphire, outcome: 0).IsLoss);
        var left = Entry(3, Teams.Sapphire, Teams.Amber, outcome: 3, left: TimeSpan.FromMinutes(19)); // you left: penalized
        Assert.True(left.IsLoss);
        Assert.True(left.Abandoned);
        var unscored = Entry(4, Teams.Amber, Teams.Amber, outcome: 5);
        Assert.False(unscored.IsWin);
        Assert.False(unscored.IsLoss);
        Assert.True(Entry(5, Teams.Amber, winner: null, outcome: 1).IsWin); // no match_result: fall back to the code
    }

    private sealed class Api : ScoutingServiceTests.FakeApiBase
    {
        public IReadOnlyList<MatchHistoryEntry> History = Array.Empty<MatchHistoryEntry>();
        public readonly List<long> Asked = new();
        public long Failing = -1;
        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<MatchHistoryEntry>> MatchHistoryAsync(uint accountId, CancellationToken ct) => Task.FromResult(History);
        public override Task<MatchMetadata?> MatchMetadataAsync(long matchId, CancellationToken ct)
        {
            Asked.Add(matchId);
            if (matchId == Failing) throw new HttpRequestException("429 from /v1/matches/x/metadata");
            var series = Enumerable.Range(0, 11).Select(i => new NetWorthPoint(TimeSpan.FromMinutes(i * 3), i * 1800)).ToList();
            return Task.FromResult<MatchMetadata?>(new MatchMetadata(matchId, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30), Teams.Amber, 0, 0,
                new[] { new MatchPlayer(77, 50, Teams.Amber, 0, 0, 0, 18000, null, 1, series) }));
        }
    }

    [Fact]
    public async Task Personal_curve_uses_recent_completed_games_and_skips_a_match_that_fails()
    {
        var api = new Api
        {
            History = new[]
            {
                Entry(1, Teams.Amber, Teams.Amber, 0), Entry(2, Teams.Amber, Teams.Sapphire, 0), Entry(3, Teams.Amber, Teams.Amber, 1),
                Entry(4, Teams.Amber, Teams.Amber, 0), Entry(5, Teams.Amber, Teams.Sapphire, 3, left: TimeSpan.FromMinutes(10)), // you left: skipped
                Entry(6, Teams.Amber, Teams.Amber, 5),                // not scored: skipped
                Entry(7, Teams.Amber, Teams.Amber, 0, daysAgo: 400),  // a year old: skipped
                Entry(8, Teams.Amber, Teams.Amber, 0, hero: 13),      // another hero
            },
            Failing = 2,
        };
        var bench = new PersonalBenchmark(api, new ScoutingServiceTests.MemoryCacheStub(), new ApiClientTests.StaticOptions(new ScoutSettings()), () => 77);
        var curve = await bench.GetAsync(50, RankBracket.All, default);
        Assert.NotNull(curve);
        Assert.Equal(3, curve!.SampleSize);                          // 1, 3 and 4; 2 failed
        Assert.Equal(new long[] { 1, 2, 3, 4 }, api.Asked.OrderBy(x => x)); // never 5, 6 or 7
    }

    [Fact]
    public async Task Too_few_personal_games_is_not_cached_so_the_next_pass_tries_again()
    {
        var api = new Api { History = new[] { Entry(1, Teams.Amber, Teams.Amber, 0), Entry(2, Teams.Amber, Teams.Amber, 0), Entry(3, Teams.Amber, Teams.Amber, 0) }, Failing = 2 };
        var cache = new ScoutingServiceTests.MemoryCacheStub();
        var bench = new PersonalBenchmark(api, cache, new ApiClientTests.StaticOptions(new ScoutSettings()), () => 77);
        Assert.Null(await bench.GetAsync(50, RankBracket.All, default)); // only two loaded
        api.Failing = -1;
        Assert.NotNull(await bench.GetAsync(50, RankBracket.All, default));
    }
}

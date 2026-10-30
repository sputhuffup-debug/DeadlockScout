using System.Text.Json;
using DeadlockScout.Api.Dto;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

/// <summary>Synthetic matches and made-up numbers; the JSON shapes are the API's, trimmed.</summary>
public class SoulSourceTests
{
    private static readonly SoulSources Game = new(Kills: 2400, Troopers: 4600, Denies: 140, Jungle: 1900, Bosses: 1400, Treasure: 900, LostOnDeath: 300);

    [Fact]
    public void A_game_splits_into_five_groups_that_add_up_to_its_souls()
    {
        var split = SoulSourceStats.Split(Game, netWorth: 19_000);
        Assert.Equal(4740, split[SoulSource.Troopers]); // denies count with troopers
        Assert.Equal(1900, split[SoulSource.Jungle]);
        Assert.Equal(2400, split[SoulSource.Kills]);
        Assert.Equal(2300, split[SoulSource.Objectives]); // bosses and the urn
        Assert.Equal(19_000 - 4740 - 1900 - 2400 - 2300, split[SoulSource.Other]);
        Assert.Equal(19_000, split.Values.Sum());
        Assert.Equal(0, Game.Other(5000)); // never negative
    }

    private static MatchPlayer P(int hero, int netWorth, SoulSources? s) => new(1, hero, 2, 0, 0, 0, netWorth, null, null, Array.Empty<NetWorthPoint>(), Sources: s);
    private static MatchMetadata M(int minutes, params MatchPlayer[] players) => new(1, DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(minutes), 2, 70, 70, players);

    [Fact]
    public void A_ranks_rates_pool_its_players_on_the_hero_and_a_game_is_set_against_them()
    {
        var sample = new[]
        {
            M(20, P(13, 19_000, Game), P(7, 50_000, Game)),        // another hero: left out
            M(30, P(13, 19_000, Game with { Troopers = 9_000 })),
            M(4, P(13, 5_000, Game)),                               // a remake: left out
            M(25, P(13, 20_000, null)),                             // no gold columns: left out
        };
        var rates = SoulSourceStats.Rates(13, sample)!;
        Assert.Equal(2, rates.Players);
        Assert.Equal((4740 + 9140) / 50.0, rates.PerMinute[SoulSource.Troopers], 6);
        Assert.Equal(600 / 50.0, rates.LostOnDeathPerMinute, 6);
        Assert.Null(SoulSourceStats.Rates(99, sample));

        var game = SoulSourceStats.Of(Game, 19_000, TimeSpan.FromMinutes(25), rates);
        Assert.Equal(SoulSourceStats.All, game.Select(r => r.Source));
        var troopers = game.Single(r => r.Source == SoulSource.Troopers);
        Assert.Equal(4740, troopers.Gained);
        Assert.Equal(rates.PerMinute[SoulSource.Troopers] * 25, troopers.Expected, 6);
        Assert.Empty(SoulSourceStats.Of(null, 19_000, TimeSpan.FromMinutes(25), rates));
        Assert.Empty(SoulSourceStats.Of(Game, 19_000, TimeSpan.FromMinutes(3), rates));
    }

    [Fact]
    public void The_costliest_source_is_the_most_souls_a_game_behind_with_enough_games()
    {
        SourceResult R(SoulSource s, int gained, double expected) => new(s, gained, expected, 30);
        var games = Enumerable.Range(0, 3).Select(_ => (IReadOnlyList<SourceResult>)new[]
        {
            R(SoulSource.Troopers, 7000, 8000),   // 1,000 behind: the costliest
            R(SoulSource.Objectives, 1000, 1500), // -33%, but only 500 souls
            R(SoulSource.Kills, 6000, 5000),
        }).ToList();
        var totals = SoulSourceStats.Total(games);
        Assert.Equal(new[] { SoulSource.Troopers, SoulSource.Kills, SoulSource.Objectives }, totals.Select(t => t.Source));
        var worst = SoulSourceStats.Costliest(totals)!;
        Assert.Equal(SoulSource.Troopers, worst.Source);
        Assert.Equal(1000, worst.ShortfallPerGame, 6);
        Assert.Null(SoulSourceStats.Costliest(SoulSourceStats.Total(games.Take(2)))); // two games are not enough
    }

    [Fact]
    public void The_parser_reads_the_gold_columns_in_every_shape()
    {
        // Bulk call with extra_player_columns: flat arrays, the last value is the end of the match.
        using var bulk = JsonDocument.Parse("""
            {"stats_gold_player": [0, 64, 2361], "stats_gold_lane_creep": [686, 1397, 4687], "stats_gold_denied": [26, 109, 137],
             "stats_gold_neutral_creep": [123, 123, 1920], "stats_gold_boss": [0, 0, 1399], "stats_gold_treasure": [0, 0, 870],
             "stats_gold_death_loss": [0, 0, 206]}
            """);
        var expected = new SoulSources(2361, 4687, 137, 1920, 1399, 870, 206);
        Assert.Equal(expected, MetadataParser.ParseSources(bulk.RootElement));
        // The single-match endpoint: stats[] of objects.
        using var single = JsonDocument.Parse("""
            {"stats": [{"time_stamp_s": 1406, "gold_player": 2361, "gold_lane_creep": 4687, "gold_denied": 137, "gold_neutral_creep": 1920,
                        "gold_boss": 1399, "gold_treasure": 870, "gold_death_loss": 206},
                       {"time_stamp_s": 180, "gold_player": 0, "gold_lane_creep": 686}]}
            """);
        Assert.Equal(expected, MetadataParser.ParseSources(single.RootElement));
        // include_player_stats: an object of arrays.
        using var columns = JsonDocument.Parse("""{"stats": {"gold_player": [0, 2361], "gold_lane_creep": [686, 4687]}}""");
        Assert.Equal(new SoulSources(2361, 4687, 0, 0, 0, 0, 0), MetadataParser.ParseSources(columns.RootElement));
        // None of the columns (a call without them, an older cached answer): no sources rather than zeros.
        using var none = JsonDocument.Parse("""{"stats_time_stamp_s": [180], "stats_net_worth": [1610]}""");
        Assert.Null(MetadataParser.ParseSources(none.RootElement));
    }

    [Fact]
    public async Task The_service_reduces_one_sample_per_hero_rank_and_patch()
    {
        var api = new SampleApi();
        var cache = new ScoutingServiceTests.MemoryCacheStub();
        var svc = new SoulSourceService(api, cache, new Patch(), TimeSpan.FromDays(7), 40);
        var bracket = new RankBracket(80, 89);
        var rates = await svc.GetAsync(13, bracket, CancellationToken.None);
        await svc.GetAsync(13, bracket, CancellationToken.None);
        Assert.Equal(1, api.Calls);
        Assert.Equal(1, rates!.Players);
        Assert.True(cache.Store.ContainsKey($"src:13:{bracket}:p1"));
        // A hero with no sample is remembered as none, not asked again.
        Assert.Null(await svc.GetAsync(99, bracket, CancellationToken.None));
        Assert.Null(await svc.GetAsync(99, bracket, CancellationToken.None));
        Assert.Equal(2, api.Calls);
    }

    private sealed class Patch : IPatchKeyProvider
    {
        public ValueTask<string> GetAsync(CancellationToken ct) => ValueTask.FromResult("p1");
    }

    private sealed class SampleApi : ScoutingServiceTests.FakeApiBase
    {
        public int Calls;
        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<MatchMetadata>> SampleMatchesAsync(int heroId, RankBracket bracket, int limit, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<MatchMetadata>>(new[] { M(25, P(13, 19_000, Game)) });
        }
    }
}

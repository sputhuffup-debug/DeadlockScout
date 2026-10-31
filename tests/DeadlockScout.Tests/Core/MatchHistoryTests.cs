using System.Net;
using System.Text.Json;
using DeadlockScout.Api.Dto;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.History;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

public class MatchHistoryTests
{
    private const uint Me = 42;
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    // Newest first, like the API: match ids fall as games get older.
    private static MatchHistoryEntry Game(long id, int hero, bool won, int k, int d, int a, int? badge = null, int daysAgo = 0)
        => new(id, hero, Now.AddDays(-daysAgo).AddHours(-id % 10), TimeSpan.FromMinutes(30), Teams.Amber, k, d, a, 15000, 0, badge, null,
            WinningTeam: won ? Teams.Amber : Teams.Sapphire);

    [Fact]
    public void Summary_counts_results_averages_and_main_heroes()
    {
        var games = new[]
        {
            Game(5, 13, true, 10, 2, 8), Game(4, 13, false, 4, 6, 4), Game(3, 1, true, 8, 4, 12),
            Game(2, 13, true, 6, 0, 6), Game(1, 7, false, 2, 8, 0),
        };
        var s = HistoryMath.Summarize(games);
        Assert.Equal((5, 3, 2), (s.Games, s.Wins, s.Losses));
        Assert.Equal(0.6, s.WinRate, 6);
        Assert.Equal(6, s.AvgKills, 6);
        Assert.Equal(4, s.AvgDeaths, 6);
        Assert.Equal((6 + 6) / 4.0, s.Kda, 6);
        Assert.Equal(13, s.TopHeroes[0].HeroId);
        Assert.Equal((3, 2), (s.TopHeroes[0].Games, s.TopHeroes[0].Wins));
        Assert.Equal(new bool?[] { true, false, true, true, false }, s.LastResults);
    }

    [Fact]
    public void Kda_counts_a_deathless_game_as_one_death()
        => Assert.Equal(12, new HistoryGame(Game(1, 1, true, 6, 0, 6), null).Kda);

    [Fact]
    public void Rank_trend_runs_oldest_first_inside_the_window_and_counts_subranks_across_tiers()
    {
        var history = new[]
        {
            Game(9, 1, true, 0, 0, 0, badge: 71, daysAgo: 1),   // Archon 1
            Game(8, 1, false, 0, 0, 0, badge: null, daysAgo: 2), // unranked mode: no point
            Game(7, 1, true, 0, 0, 0, badge: 66, daysAgo: 5),   // Emissary 6
            Game(6, 1, true, 0, 0, 0, badge: 64, daysAgo: 10),  // Emissary 4
            Game(5, 1, true, 0, 0, 0, badge: 51, daysAgo: 40),  // outside the window
        };
        var r = HistoryMath.Rank(history, Now - MatchHistoryService.RankWindow);
        Assert.Equal(new[] { 64, 66, 71 }, r.Points.Select(p => p.Badge));
        Assert.Equal(71, r.Current);
        Assert.Equal(71, r.Peak);
        Assert.Equal(3, r.NetChange); // Emissary 4 → 5 → 6 → Archon 1
    }

    private static MatchMetadata Meta(long id, params (uint Account, int Team, int Hero, int NetWorth)[] players)
        => new(id, Now, TimeSpan.FromMinutes(30), Teams.Amber, 70, 72,
            players.Select(p => new MatchPlayer(p.Account, p.Hero, p.Team, 1, 2, 3, p.NetWorth, null, null, Array.Empty<NetWorthPoint>(),
                LastHits: 180, Denies: 9, Level: 30,
                Items: p.Account == Me
                    ? new[] { new MatchItem(300, TimeSpan.FromMinutes(12), null), new MatchItem(100, TimeSpan.FromMinutes(2), null),
                              new MatchItem(200, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(20)) }
                    : null)).ToList());

    [Fact]
    public void Detail_is_from_your_side_with_held_items_in_buy_order()
    {
        var m = Meta(1, (Me, Teams.Amber, 13, 20000), (7, Teams.Amber, 1, 25000), (8, Teams.Amber, 2, 10000),
                        (9, Teams.Sapphire, 3, 30000), (10, Teams.Sapphire, 4, 5000));
        var d = HistoryMath.Detail(m, Me)!;
        Assert.Equal(new long[] { 100, 300 }, d.FinalItems); // 200 was sold
        Assert.Equal((2, 3), (d.TeamSoulsRank, d.TeamSize));
        Assert.Equal(71, d.AverageBadge);
        Assert.Equal(new[] { 1, 13, 2 }, d.Team.Select(p => p.HeroId));
        Assert.True(d.Team[1].IsMe);
        Assert.Equal(new[] { 3, 4 }, d.Enemies.Select(p => p.HeroId));
        Assert.Equal((180, 30), (d.LastHits!.Value, d.Level!.Value));
        Assert.Equal(new long[] { 100, 200, 300 }, d.Purchases!.Select(p => p.ItemId)); // the timeline keeps sold items
        Assert.Null(HistoryMath.Detail(m, 999));
    }

    [Fact]
    public void Souls_at_a_purchase_are_read_between_samples()
    {
        var souls = new[] { new NetWorthPoint(TimeSpan.Zero, 0), new NetWorthPoint(TimeSpan.FromMinutes(10), 6000), new NetWorthPoint(TimeSpan.FromMinutes(20), 14000) };
        Assert.Equal(3000, HistoryMath.SoulsAt(souls, TimeSpan.FromMinutes(5)));
        Assert.Equal(10000, HistoryMath.SoulsAt(souls, TimeSpan.FromMinutes(15)));
        Assert.Equal(14000, HistoryMath.SoulsAt(souls, TimeSpan.FromMinutes(40)));
        Assert.Null(HistoryMath.SoulsAt(Array.Empty<NetWorthPoint>(), TimeSpan.Zero));
    }

    private sealed class Api : ScoutingServiceTests.FakeApiBase
    {
        public List<MatchHistoryEntry> History = new();
        public Dictionary<long, MatchMetadata> Processed = new();
        public readonly List<long[]> Asked = new();
        public bool Fail;
        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<MatchHistoryEntry>> MatchHistoryAsync(uint accountId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MatchHistoryEntry>>(History);
        public override Task<IReadOnlyList<MatchMetadata>> MatchesAsync(IReadOnlyCollection<long> matchIds, CancellationToken ct)
        {
            Asked.Add(matchIds.ToArray());
            if (Fail) throw new HttpRequestException("down", null, HttpStatusCode.ServiceUnavailable);
            return Task.FromResult<IReadOnlyList<MatchMetadata>>(matchIds.Where(Processed.ContainsKey).Select(i => Processed[i]).ToList());
        }
    }

    [Fact]
    public async Task Details_are_fetched_once_in_bulk_and_unprocessed_games_are_asked_again()
    {
        var api = new Api { History = { Game(3, 13, true, 5, 1, 5), Game(2, 13, false, 1, 5, 1), Game(1, 1, true, 3, 3, 3) } };
        api.Processed[2] = Meta(2, (Me, Teams.Amber, 13, 9000));
        api.Processed[1] = Meta(1, (Me, Teams.Amber, 1, 8000));
        var cache = new ScoutingServiceTests.MemoryCacheStub();
        var service = new MatchHistoryService(api, cache, new TestClock(Now));

        var page = await service.GetAsync(Me, 20, default);
        Assert.Equal(new long[] { 3, 2, 1 }, api.Asked.Single());
        Assert.Null(page.Games[0].Detail); // match 3 is not processed yet
        Assert.NotNull(page.Games[1].Detail);
        Assert.True(cache.Store.ContainsKey(CacheKeys.MatchDetail(Me, 1)));

        api.Processed[3] = Meta(3, (Me, Teams.Amber, 13, 12000));
        page = await service.GetAsync(Me, 20, default);
        Assert.Equal(new long[] { 3 }, api.Asked[1]); // only the one still missing
        Assert.All(page.Games, g => Assert.NotNull(g.Detail));
    }

    [Fact]
    public async Task The_history_still_shows_when_details_fail()
    {
        var api = new Api { History = { Game(1, 13, true, 5, 1, 5) }, Fail = true };
        var page = await new MatchHistoryService(api, new ScoutingServiceTests.MemoryCacheStub(), new TestClock(Now)).GetAsync(Me, 20, default);
        Assert.Single(page.Games);
        Assert.Null(page.Games[0].Detail);
        Assert.Equal(1, page.Summary.Wins);
    }

    [Fact]
    public void Bulk_metadata_items_last_hits_and_level_are_read()
    {
        var json = """
            [{ "match_id": 7, "start_time": 1761700000, "duration_s": 1800, "winning_team": 0,
               "players": [{ "account_id": 42, "hero_id": 13, "team": 0, "kills": 1, "deaths": 2, "assists": 3, "net_worth": 9000,
                             "last_hits": 150, "denies": 7, "player_level": 28,
                             "items": [{ "item_id": 865846625, "game_time_s": 900, "sold_time_s": 0 },
                                       { "item_id": 1009965641, "game_time_s": 60, "sold_time_s": 700 }] },
                           { "account_id": 43, "hero_id": 1, "team": 1, "net_worth": 8000 }] }]
            """;
        using var doc = JsonDocument.Parse(json);
        var p = MetadataParser.ParseBulk(doc.RootElement).Single().Players;
        Assert.Equal((150, 7, 28), (p[0].LastHits!.Value, p[0].Denies!.Value, p[0].Level!.Value));
        Assert.Equal(2, p[0].Items!.Count);
        Assert.Null(p[0].Items![0].SoldAt);
        Assert.Equal(TimeSpan.FromSeconds(700), p[0].Items![1].SoldAt);
        Assert.Null(p[1].Items); // no item list: unknown, not empty
    }

    [Fact]
    public async Task A_played_match_the_history_does_not_list_is_processing_until_its_metadata_comes()
    {
        // The API's history ends at match 2; the app saw you play 4 (newest) and 2 (already listed).
        var api = new Api { History = { Game(2, 13, true, 5, 1, 5) } };
        api.Processed[2] = Meta(2, (Me, Teams.Amber, 13, 15000));
        var cache = new ScoutingServiceTests.MemoryCacheStub();
        var clock = new TestClock(Now.AddHours(2));
        var played = new PlayedMatches(cache, clock);
        await played.RecordAsync(Me, new PlayedMatch(2, Now.AddHours(-3), Now.AddHours(-2.5), 13), default);
        await played.RecordAsync(Me, new PlayedMatch(4, Now.AddMinutes(-5), Now.AddMinutes(30), 1), default);
        var service = new MatchHistoryService(api, cache, clock, played: played);

        var page = await service.GetAsync(Me, 20, default);
        Assert.Equal(new long[] { 4 }, page.Processing!.Select(p => p.MatchId));    // 2 is listed: no second card
        Assert.Equal(new long[] { 2 }, page.Games.Select(g => g.Entry.MatchId)); // and nothing counts the pending one
        Assert.Equal(1, page.Summary.Games);
        Assert.Contains(4L, api.Asked[0]);                                          // asked for in the same bulk call

        // Its metadata comes before the history lists it: a full card from the metadata, kept for next time.
        api.Processed[4] = Meta(4, (Me, Teams.Amber, 1, 21000), (8, Teams.Sapphire, 3, 20000));
        page = await service.GetAsync(Me, 20, default);
        Assert.Empty(page.Processing!);
        var game = page.Games[0];
        Assert.Equal((4L, 1, 21000), (game.Entry.MatchId, game.Entry.HeroId, game.Entry.NetWorth));
        Assert.True(game.Entry.IsWin);                                             // Amber won, from the winning team
        Assert.NotNull(game.Detail);
        Assert.Equal(2, page.Summary.Games);

        int asked = api.Asked.Count;
        page = await service.GetAsync(Me, 20, default);
        Assert.Equal(asked, api.Asked.Count);                                       // the row and detail come from the cache
        Assert.Equal(4L, page.Games[0].Entry.MatchId);
    }

    [Fact]
    public async Task Played_matches_keep_three_days_newest_first_one_row_each()
    {
        var cache = new ScoutingServiceTests.MemoryCacheStub();
        var clock = new TestClock(Now);
        var played = new PlayedMatches(cache, clock);
        await played.RecordAsync(Me, new PlayedMatch(1, Now.AddDays(-4), Now.AddDays(-4).AddMinutes(30), 13), default); // too old
        await played.RecordAsync(Me, new PlayedMatch(2, Now.AddHours(-5), Now.AddHours(-4.5), 13), default);
        await played.RecordAsync(Me, new PlayedMatch(3, Now.AddHours(-1), Now.AddMinutes(-25), null), default);
        await played.RecordAsync(Me, new PlayedMatch(2, Now.AddHours(-5), Now.AddHours(-4.4), 13), default);   // the same match again
        await played.RecordAsync(0, new PlayedMatch(9, Now, Now, 1), default);                                  // no account: ignored
        var kept = await played.RecentAsync(Me, default);
        Assert.Equal(new long[] { 3, 2 }, kept.Select(p => p.MatchId));
        Assert.Equal(Now.AddHours(-4.4), kept[1].EndedAt);
    }
}

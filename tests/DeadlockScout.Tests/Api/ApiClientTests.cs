using System.Net;
using System.Text;
using System.Text.Json;
using DeadlockScout.Api;
using DeadlockScout.Api.Dto;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Tests.Api;

public class ApiClientTests
{
    private sealed class Router : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new();
        public readonly List<string> Bodies = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return Respond(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (DeadlockApiClient Client, Router Router) Make()
    {
        var router = new Router();
        var http = new HttpClient(router);
        var monitor = new StaticOptions(new ScoutSettings());
        return (new DeadlockApiClient(http, monitor), router);
    }

    public sealed class StaticOptions(ScoutSettings v) : IOptionsMonitor<ScoutSettings>
    {
        public ScoutSettings CurrentValue => v;
        public ScoutSettings Get(string? name) => v;
        public IDisposable? OnChange(Action<ScoutSettings, string?> listener) => null;
    }

    [Fact]
    public async Task Hero_stats_uses_one_comma_separated_call_and_tolerates_extra_and_string_fields()
    {
        var (client, router) = Make();
        router.Respond = _ => Json(Fixtures.Read("api", "hero-stats.json"));
        var rows = await client.HeroStatsAsync(new uint[] { 101, 102, 101 }, default);
        Assert.Single(router.Requests);
        Assert.Equal("/v1/players/hero-stats", router.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?account_ids=101,102", Uri.UnescapeDataString(router.Requests[0].RequestUri!.Query));
        Assert.Equal(3, rows.Count);
        Assert.Equal(new PlayerHeroStats(101, 1, 40, 22, 300, 200, 400) { NetWorthPerMin = 610.2 }, rows[0]);
        var recent = await client.RecentHeroStatsAsync(new uint[] { 101, 102 }, 30, default);
        Assert.Equal(3, recent.Count);
        var query = System.Web.HttpUtility.ParseQueryString(router.Requests[^1].RequestUri!.Query);
        Assert.Equal("101,102", query["account_ids"]);
        long since = long.Parse(query["min_unix_timestamp"]!);
        Assert.Equal(0, since % 86400); // a whole day, so the URL stays the same all day
        Assert.InRange(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - since, 30 * 86400, 31 * 86400);
        Assert.Equal(12, rows[2].Matches);
        Assert.Equal(0.75, rows[2].WinRate, 3);
    }

    [Fact]
    public async Task Forbidden_from_hero_stats_means_every_account_is_private_and_yields_no_rows()
    {
        var (client, router) = Make();
        router.Respond = _ => Json("{\"error\":\"all protected\"}", HttpStatusCode.Forbidden);
        Assert.Empty(await client.HeroStatsAsync(new uint[] { 1 }, default));
        Assert.Empty(await client.HeroStatsAsync(Array.Empty<uint>(), default));
        Assert.Single(router.Requests);
    }

    [Fact]
    public async Task Server_errors_throw_with_status()
    {
        var (client, router) = Make();
        router.Respond = _ => Json("boom", HttpStatusCode.InternalServerError);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.AnalyticsHeroStatsAsync(new AnalyticsQuery(), default));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task Enemy_and_mate_stats_map_ids_and_losses()
    {
        var (client, router) = Make();
        router.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("enemy-stats")
            ? Json("[{\"enemy_id\": 107, \"wins\": 1, \"matches_played\": 3, \"matches\": [1,2,3]}]")
            : Json("[{\"mate_id\": 108, \"wins\": 4, \"matches_played\": 4}]");
        var enemies = await client.EnemyStatsAsync(101, default);
        var mates = await client.MateStatsAsync(101, default);
        Assert.Equal(new SharedHistory(107, 3, 1, 2, true), enemies.Single());
        Assert.Equal(new SharedHistory(108, 4, 4, 0, false), mates.Single());
        Assert.Equal("/v1/players/101/enemy-stats", router.Requests[0].RequestUri!.AbsolutePath);
        router.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("item-stats")
            ? Json("[{\"item_id\": 2739107182, \"bucket\": 0, \"wins\": 60, \"matches\": 100}, {\"item_id\": 0, \"matches\": 5}]")
            : r.RequestUri!.AbsolutePath.EndsWith("enemy-stats")
                ? Json("[{\"enemy_id\": 107, \"wins\": 1, \"matches_played\": 3, \"matches\": [1,2,3]}]")
                : Json("[{\"mate_id\": 108, \"wins\": 4, \"matches_played\": 4}]");
        var counters = await client.ItemWinStatsAsync(13, 4, default);
        Assert.Equal(new ItemWinStat(2739107182, 100, 60), counters.Single());
        Assert.Equal("/v1/analytics/item-stats?hero_ids=13&min_matches=50&enemy_hero_ids=4", router.Requests[^1].RequestUri!.PathAndQuery);
        await client.ItemWinStatsAsync(13, null, default);
        Assert.Equal("/v1/analytics/item-stats?hero_ids=13&min_matches=50", router.Requests[^1].RequestUri!.PathAndQuery);
        var party = await client.PartyMatesAsync(101, default);
        Assert.Equal(new SharedHistory(108, 4, 4, 0, false), party.Single());
        Assert.Equal("/v1/players/101/mate-stats?same_party=true", router.Requests[^1].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task Ranks_and_live_url_and_active_match()
    {
        var (client, router) = Make();
        router.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            "/v1/players/rank" => Json("[{\"account_id\": 101, \"badge\": 73, \"rank\": 7, \"subrank\": 3, \"last_match\": null}]"),
            "/v1/matches/5/live/url" => Json("{\"broadcast_url\": \"http://dist1-ord1.steamcontent.com/tv/123\", \"lobby_id\": 99}"),
            "/v1/matches/active" => Json("[{\"match_id\": 5, \"lobby_id\": 99, \"start_time\": 1761000000, \"duration_s\": 120, \"players\": [{\"account_id\": 101, \"team\": 0, \"hero_id\": 1}, {\"account_id\": 102, \"team\": 1, \"hero_id\": 7}]}]"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var ranks = await client.RanksAsync(new uint[] { 101 }, default);
        Assert.Equal(new PlayerRank(101, 73, 7, 3), ranks.Single());
        Assert.Equal("http://dist1-ord1.steamcontent.com/tv/123", await client.LiveUrlAsync(5, default));
        var active = await client.ActiveMatchAsync(5, default);
        Assert.Equal(2, active!.Players.Count);
        Assert.Equal(Teams.Amber, active.Players[0].Team);
        Assert.Equal(Teams.Sapphire, active.Players[1].Team);
        Assert.Null(await client.ActiveMatchAsync(6, default));
    }

    [Fact]
    public async Task Match_history_reads_the_body_even_on_429()
    {
        var (client, router) = Make();
        router.Respond = _ => Json("[{\"match_id\": 10, \"hero_id\": 1, \"start_time\": 1761000000, \"match_duration_s\": 1500, \"player_team\": 1, \"player_kills\": 3, \"player_deaths\": 2, \"player_assists\": 7, \"net_worth\": 18000, \"player_match_outcome\": 1, \"ranked_display_badge\": 73, \"ranked_delta\": 12}, {\"match_id\": 11, \"hero_id\": 2, \"start_time\": 1761003600, \"match_duration_s\": 900, \"player_team\": 0, \"player_match_outcome\": 2}]", HttpStatusCode.TooManyRequests);
        var h = await client.MatchHistoryAsync(101, default);
        Assert.Equal(new long[] { 11, 10 }, h.Select(e => e.MatchId));
        Assert.True(h[1].IsWin);
        Assert.True(h[0].IsLoss);
        Assert.Equal(73, h[1].RankedBadge);
        Assert.Equal(Teams.Sapphire, h[1].Team);
    }

    [Fact]
    public async Task Metadata_and_bulk_sample_parse_both_series_shapes()
    {
        var (client, router) = Make();
        router.Respond = r => r.RequestUri!.AbsolutePath == "/v1/matches/41223344/metadata"
            ? Json(Fixtures.Read("api", "match-metadata.json"))
            : r.RequestUri.AbsolutePath == "/v1/matches/metadata" ? Json(Fixtures.Read("api", "bulk-metadata.json")) : new HttpResponseMessage(HttpStatusCode.NotFound);

        var meta = await client.MatchMetadataAsync(41223344, default);
        Assert.Equal(41223344, meta!.MatchId);
        Assert.Equal(TimeSpan.FromMinutes(30), meta.Duration);
        Assert.Equal(Teams.Amber, meta.WinningTeam);
        Assert.Equal(71, meta.AverageBadgeTeamAmber);
        var me = meta.Players.Single(p => p.AccountId == 101);
        Assert.Equal(Teams.Amber, me.Team);
        Assert.Equal(1, me.AssignedLane);
        Assert.Equal(6, me.NetWorthSeries.Count);
        Assert.Equal(new NetWorthPoint(TimeSpan.FromSeconds(600), 6000), me.NetWorthSeries[3]);
        Assert.Null(meta.Players.Single(p => p.AccountId == 102).AssignedLane);
        Assert.Null(await client.MatchMetadataAsync(1, default));

        Assert.True(await client.MatchProcessedAsync(41223344, default));
        Assert.Contains("match_ids=41223344", router.Requests[^1].RequestUri!.Query);
        var respond = router.Respond;
        router.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"status\":404,\"error\":\"No matches found\"}") };
        Assert.False(await client.MatchProcessedAsync(1, default)); // not processed yet
        router.Respond = respond;

        var sample = await client.SampleMatchesAsync(1, new RankBracket(60, 80), 40, default);
        var q = Uri.UnescapeDataString(router.Requests[^1].RequestUri!.Query);
        Assert.Contains("hero_ids=1", q);
        Assert.Contains("min_average_badge=60", q);
        Assert.Contains("max_average_badge=80", q);
        Assert.Contains("extra_player_columns=stats.time_stamp_s,stats.net_worth", q);
        Assert.DoesNotContain("only_filtered_players", q); // now 400s without account_ids
        Assert.DoesNotContain("include_player_stats", q);  // every stat array of every player: far too big
        Assert.Contains("limit=40", q);
        Assert.Equal(2, sample.Count); // the match_id 0 entry is dropped
        Assert.Equal(6, sample[0].Players[0].NetWorthSeries.Count);
        Assert.Equal(9000, sample[0].Players[0].NetWorthSeries[3].NetWorth);
        Assert.Equal(Teams.Sapphire, sample[0].WinningTeam);
    }

    [Fact]
    public async Task Analytics_filters_heroes_client_side_and_encodes_bucket_filters()
    {
        var (client, router) = Make();
        router.Respond = _ => Json("[{\"hero_id\": 1, \"matches\": 100, \"wins\": 55, \"total_net_worth\": 1500000, \"total_kills\": 1, \"total_deaths\": 2, \"total_assists\": 3}, {\"hero_id\": 2, \"matches\": 50, \"wins\": 20, \"total_net_worth\": 600000}]");
        var rows = await client.AnalyticsHeroStatsAsync(new AnalyticsQuery(HeroIds: new[] { 1 }, MinDurationSeconds: 600, MaxDurationSeconds: 900, MinAverageBadge: 60, MaxAverageBadge: 80), default);
        var q = router.Requests[0].RequestUri!.Query;
        Assert.Equal("?min_duration_s=600&max_duration_s=900&min_average_badge=60&max_average_badge=80", q);
        Assert.DoesNotContain("hero_ids", q);
        Assert.Single(rows);
        Assert.Equal(15000, rows[0].MeanNetWorth, 3);
    }

    [Fact]
    public async Task Counter_stats_item_stats_assets_and_steam_info()
    {
        var (client, router) = Make();
        router.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            // Shape from a live call (2026-10-27): every pair in the bracket, with per-side totals the client ignores.
            "/v1/analytics/hero-counter-stats" => Json("[{\"hero_id\": 1, \"enemy_hero_id\": 7, \"wins\": 60, \"matches_played\": 100, \"kills\": 700, \"enemy_kills\": 650, \"networth\": 4000000, \"enemy_networth\": 3900000}," +
                                                       "{\"hero_id\": 7, \"enemy_hero_id\": 1, \"wins\": 40, \"matches_played\": 100}, {\"hero_id\": 0, \"enemy_hero_id\": 1, \"wins\": 1, \"matches_played\": 0}]"),
            // Shapes from live calls (2026-10-27). Item and ability ids above int.MaxValue are real: 2739107182, 2948410412.
            "/v1/analytics/item-stats" => Json("[{\"item_id\": 2739107182, \"bucket\": 12000, \"wins\": 10, \"losses\": 5, \"matches\": 15, \"players\": 9, \"avg_buy_time_s\": 1054.5, \"sold\": 2}," +
                                               "{\"item_id\": 7, \"bucket\": 0, \"wins\": 0, \"losses\": 0, \"matches\": 0}]"),
            "/v1/analytics/ability-order-stats" => Json("[{\"abilities\": [1080948381, 1080948381, 2948410412], \"avg_upgrade_times_s\": [13.2, 60.1, 105.0], \"wins\": 3110, \"losses\": 2391, \"matches\": 5501, \"players\": 900, \"total_kills\": 1}," +
                                                        "{\"abilities\": [], \"avg_upgrade_times_s\": [], \"wins\": 1, \"losses\": 0, \"matches\": 1}]"),
            "/v1/assets/heroes" => Json("[{\"id\": 1, \"class_name\": \"hero_inferno\", \"name\": \"Infernus\", \"player_selectable\": true, \"disabled\": false, \"images\": {\"icon_image_small\": \"https://x/inf.png\"}}, {\"id\": 99, \"class_name\": \"hero_test\", \"name\": \"Test\", \"player_selectable\": false}]"),
            "/v1/assets/items" => Json("[{\"id\": 2521299219, \"class_name\": \"upgrade_x\", \"name\": \"X\", \"type\": \"upgrade\", \"item_slot_type\": \"weapon\", \"item_tier\": 2, \"cost\": 1250, \"shop_image_small\": \"https://x/x.png\"}, " +
                                       "{\"id\": 2948410412, \"class_name\": \"ability_y\", \"name\": \"Y\", \"type\": \"ability\", \"hero\": 13}, {\"id\": 3, \"class_name\": \"weapon_z\", \"name\": \"Z\", \"type\": \"weapon\"}]"),
            "/v1/assets/steam-info" => Json("{\"client_version\": 5901, \"server_version\": 5901, \"version_date\": \"Oct 20 2026\"}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var table = await client.HeroCounterStatsAsync(new RankBracket(60, 80), default);
        Assert.Equal(new[] { new HeroMatchup(1, 7, 100, 60), new HeroMatchup(7, 1, 100, 40) }, table);
        var query = router.Requests[0].RequestUri!.Query;
        Assert.Contains("min_average_badge=60", query);
        Assert.Contains("max_average_badge=80", query);
        Assert.Contains("same_lane_filter=true", query);
        Assert.DoesNotContain("comb_size", query); // hero-comb-stats rejected comb_size=1 with a 400

        var items = await client.ItemStatsByNetWorthAsync(13, new RankBracket(60, 80), default);
        Assert.Equal(new ItemBucketStat(2739107182, 12000, 15, 10, 1054.5), items.Single()); // the zero-match row is dropped
        Assert.Contains("bucket=net_worth_by_2000", router.Requests[1].RequestUri!.Query);
        Assert.DoesNotContain("min_networth", router.Requests[1].RequestUri!.Query); // that filters final net worth, not net worth at purchase

        var orders = await client.AbilityOrderStatsAsync(13, new RankBracket(60, 80), default);
        var order = orders.Single();
        Assert.Equal(new long[] { 1080948381, 1080948381, 2948410412 }, order.Abilities);
        Assert.Equal(new[] { 13.2, 60.1, 105.0 }, order.AvgUpgradeTimesSeconds);
        Assert.Equal((5501, 3110), (order.Matches, order.Wins));
        Assert.Contains("hero_id=13", router.Requests[2].RequestUri!.Query);

        var timings = await client.ItemTimingsAsync(13, new RankBracket(80, 89), default);
        Assert.Equal(new ItemTiming(2739107182, 15, 1054.5), timings.Single()); // the zero-match row is dropped
        Assert.DoesNotContain("bucket", router.Requests[^1].RequestUri!.Query); // one row per item, not per net worth
        Assert.Contains("min_average_badge=80", router.Requests[^1].RequestUri!.Query);

        var heroes = await client.HeroesAsync(default);
        Assert.Equal(new HeroAsset(1, "hero_inferno", "Infernus", "https://x/inf.png", true), heroes[0]);
        Assert.False(heroes[1].Playable);

        var assets = await client.ItemsAsync(default);
        Assert.Equal(2, assets.Count); // upgrades and abilities; weapons are left out
        Assert.Equal(new ItemAsset(2521299219, "upgrade_x", "X", "weapon", 2, 1250, "https://x/x.png"), assets[0]);
        Assert.Equal((2948410412L, "ability"), (assets[1].Id, assets[1].Slot));

        var info = await client.SteamInfoAsync(default);
        Assert.Equal(5901, info!.ClientVersion);
    }

    [Fact]
    public async Task Images_come_only_from_the_assets_bucket_over_https()
    {
        var (client, router) = Make();
        router.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("missing.png")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) };
        var bucket = "https://assets-bucket.deadlock-api.com/assets-api-res/images/upgrades/x.png";
        Assert.Equal(4, (await client.ImageAsync(bucket, default))!.Length);
        Assert.Null(await client.ImageAsync("https://assets-bucket.deadlock-api.com/missing.png", default));
        Assert.Null(await client.ImageAsync("http://assets-bucket.deadlock-api.com/x.png", default));
        Assert.Null(await client.ImageAsync("https://example.com/x.png", default));
        Assert.Null(await client.ImageAsync("not a url", default));
        Assert.Equal(2, router.Requests.Count); // the refused ones never left the machine
    }

    [Fact]
    public async Task Oversized_images_are_refused()
    {
        var (client, router) = Make();
        router.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[300 * 1024]) };
        Assert.Null(await client.ImageAsync("https://assets-bucket.deadlock-api.com/big.png", default));
    }

    [Fact]
    public async Task Salts_are_posted_as_an_array_in_the_documented_shape()
    {
        var (client, router) = Make();
        router.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
        await client.SubmitSaltsAsync(new[] { new MatchSalts(41223344, 182, 12345, 67890) }, default);
        Assert.Equal(HttpMethod.Post, router.Requests[0].Method);
        Assert.Equal("/v1/matches/salts", router.Requests[0].RequestUri!.AbsolutePath);
        using var doc = JsonDocument.Parse(router.Bodies[0]);
        var item = doc.RootElement[0];
        Assert.Equal(41223344, item.GetProperty("match_id").GetInt64());
        Assert.Equal(12345, item.GetProperty("metadata_salt").GetInt64());
        Assert.Equal(67890, item.GetProperty("replay_salt").GetInt64());
        Assert.Equal(182, item.GetProperty("cluster_id").GetInt64());
        await client.SubmitSaltsAsync(Array.Empty<MatchSalts>(), default);
        Assert.Single(router.Requests);
    }

    [Fact]
    public void Metadata_parser_survives_garbage()
    {
        using var doc = JsonDocument.Parse("{\"match_info\": {\"match_id\": \"7\", \"players\": [{\"account_id\": 1, \"stats\": {\"time_stamp_s\": [0, 60], \"net_worth\": [0]}}, 5]}}");
        var m = MetadataParser.ParseSingle(doc.RootElement);
        Assert.Equal(7, m!.MatchId);
        Assert.Single(m.Players);
        Assert.Single(m.Players[0].NetWorthSeries);
        Assert.Null(MetadataParser.ParseSingle(JsonDocument.Parse("{}").RootElement));
    }
}

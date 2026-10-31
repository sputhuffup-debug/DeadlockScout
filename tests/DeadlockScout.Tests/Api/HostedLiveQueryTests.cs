using System.Net;
using System.Text;
using System.Text.Json;
using DeadlockScout.Api.Live;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using DeadlockScout.Tests.Core;

namespace DeadlockScout.Tests.Api;

public class HostedLiveQueryTests
{
    private static JsonElement Row(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Queries_quote_the_columns_but_not_the_tables()
    {
        // Live, Oct 31, 2026: a quoted table name is "not found", the bare one resolves; the columns need their quotes.
        Assert.Contains("\"m_PlayerDataGlobal__m_iGoldNetWorth\"", LiveQueryTranslator.ControllerQuery);
        Assert.EndsWith("FROM CCitadelPlayerController", LiveQueryTranslator.ControllerQuery);
        Assert.Contains("\"m_pGameRules__m_flGameStartTime\"", LiveQueryTranslator.RulesQuery);
        Assert.EndsWith("FROM CCitadelGameRulesProxy", LiveQueryTranslator.RulesQuery);
        var url = HostedLiveQueryStream.QueryUrl("https://api.deadlock-api.com/", "http://dist1.steamcontent.com/tv/1", LiveQueryTranslator.RulesQuery);
        Assert.StartsWith("https://api.deadlock-api.com/v1/matches/demo/live/query?broadcast_url=http%3A%2F%2F", url);
        Assert.DoesNotContain(" ", url);
    }

    [Fact]
    public void Clock_follows_the_live_events_formula_and_is_unknown_until_the_rules_row()
    {
        var t = new LiveQueryTranslator(64);
        Assert.False(t.ClockKnown);
        Assert.Null(t.GameTimeAt(6400));
        t.OnRulesRow(Row("{\"tick\":1,\"m_pGameRules__m_flGameStartTime\":40.0,\"m_pGameRules__m_nTotalPausedTicks\":640}"));
        Assert.True(t.ClockKnown);
        // (19200 - 640) / 64 - 40 = 250
        Assert.Equal(250, t.GameTimeAt(19200)!.Value, 6);
        // a rules row without the start time (null columns are omitted) keeps the known clock
        t.OnRulesRow(Row("{\"tick\":2,\"delta_type\":\"update\"}"));
        Assert.Equal(250, t.GameTimeAt(19200)!.Value, 6);
    }

    [Fact]
    public void Controller_rows_become_player_events_with_account_ids_and_tick_ends_when_the_tick_advances()
    {
        var t = new LiveQueryTranslator(64);
        t.OnRulesRow(Row("{\"m_pGameRules__m_flGameStartTime\":40.0,\"m_pGameRules__m_nTotalPausedTicks\":0}"));
        var first = t.OnControllerRow(Row("{\"tick\":6400,\"entity_index\":5,\"delta_type\":\"create\",\"m_steamID\":76561197960265829,\"m_iszPlayerName\":\"Me\",\"m_iTeamNum\":2,\"m_nAssignedLane\":4,\"m_PlayerDataGlobal__m_nHeroID\":13,\"m_PlayerDataGlobal__m_iGoldNetWorth\":500,\"m_PlayerDataGlobal__m_iPlayerKills\":1,\"m_PlayerDataGlobal__m_iDeaths\":2,\"m_PlayerDataGlobal__m_iPlayerAssists\":3}")).ToList();
        var e = Assert.Single(first);
        Assert.True(e.IsPlayerController);
        Assert.Equal(101u, e.SteamId);
        Assert.Equal("Me", e.SteamName);
        Assert.Equal(Teams.Amber, e.Team);
        Assert.Equal(13, e.HeroId);
        Assert.Equal(4, e.AssignedLane);
        Assert.Equal(500, e.NetWorth);
        Assert.Equal((1, 2, 3), (e.Kills, e.Deaths, e.Assists));
        Assert.Equal(60, e.GameTime!.Value, 6);
        Assert.Equal("create", e.Delta);

        // same tick: no tick_end; next tick: tick_end for the previous one first
        Assert.Single(t.OnControllerRow(Row("{\"tick\":6400,\"entity_index\":6,\"delta_type\":\"create\",\"m_steamID\":102}")));
        var next = t.OnControllerRow(Row("{\"tick\":6464,\"entity_index\":5,\"delta_type\":\"update\"}")).ToList();
        Assert.Equal(2, next.Count);
        Assert.True(next[0].IsTickEnd);
        Assert.Equal(60, next[0].GameTime!.Value, 6);
        Assert.Equal(61, next[1].GameTime!.Value, 6);

        var end = t.OnEnd().ToList();
        Assert.True(end[0].IsTickEnd);
        Assert.True(end[^1].IsEnd);
    }

    [Fact]
    public void No_tick_end_before_the_clock_is_known()
    {
        var t = new LiveQueryTranslator(64);
        t.OnControllerRow(Row("{\"tick\":100,\"entity_index\":5,\"m_steamID\":101}")).ToList();
        var rows = t.OnControllerRow(Row("{\"tick\":200,\"entity_index\":5,\"m_steamID\":101}")).ToList();
        Assert.Single(rows);
        Assert.Null(rows[0].GameTime);
        Assert.Equal(new[] { "end" }, t.OnEnd().Select(e => e.EventType));
    }

    private sealed class SseServer : HttpMessageHandler
    {
        public readonly List<Uri> Requests = new();
        public HttpStatusCode ControllerStatus = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            bool rules = query.Contains("CCitadelGameRulesProxy");
            if (!rules)
            {
                await Task.Delay(100, ct); // let the clock arrive first, as it does in practice within a second
                if (ControllerStatus != HttpStatusCode.OK)
                    return new HttpResponseMessage(ControllerStatus) { Content = new StringContent("{\"error\":\"bad query\"}") };
            }
            var body = Fixtures.Read("api", rules ? "live-query-rules.sse" : "live-query-controllers.sse");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))) };
        }
    }

    private sealed class LiveUrlApi : ScoutingServiceTests.FakeApiBase
    {
        public int LiveUrlCalls;
        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<string> LiveUrlAsync(long matchId, CancellationToken ct) { LiveUrlCalls++; return Task.FromResult("http://dist1-ord1.steamcontent.com/tv/42"); }
        public override Task<IReadOnlyList<PlayerRank>> RanksAsync(IEnumerable<uint> accountIds, CancellationToken ct) => Task.FromResult<IReadOnlyList<PlayerRank>>(new List<PlayerRank>());
    }

    private static HostedLiveQueryStream Make(SseServer server, LiveUrlApi api, ScoutingServiceTests.MemoryCacheStub cache)
        => new(new HttpClient(server), new BroadcastUrlResolver(api, cache), new ApiClientTests.StaticOptions(new ScoutSettings())) { Backoff = _ => TimeSpan.FromMilliseconds(10) };

    [Fact]
    public async Task One_live_url_call_and_two_query_streams_feed_the_existing_live_match_feed()
    {
        var server = new SseServer();
        var api = new LiveUrlApi();
        var cache = new ScoutingServiceTests.MemoryCacheStub();
        var feed = new LiveMatchFeed(Make(server, api, cache), api, cache, rosterTimeout: TimeSpan.FromSeconds(1));
        var ctx = new MatchContext(42, 0, 101, DateTimeOffset.UtcNow);

        var ticks = new List<NetWorthTick>();
        await foreach (var t in feed.TicksAsync(ctx, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token)) ticks.Add(t);

        Assert.Equal(1, api.LiveUrlCalls);
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, u => Assert.Contains("broadcast_url=", u.Query));
        // clock: start 40 s, 640 paused ticks after the second rules row; tick 19200 → (19200-640)/64-40 = 250 s
        var mine = ticks.Where(t => t.AccountId == 101).ToList();
        Assert.Contains(mine, t => t.NetWorth == 1200 && t.Kills == 1 && Math.Abs(t.GameTime.TotalSeconds - 250) < 0.01);
        Assert.Contains(mine, t => t.NetWorth == 2600 && Math.Abs(t.GameTime.TotalSeconds - 370) < 0.01);
        Assert.Contains(ticks, t => t.AccountId == 102 && t.NetWorth == 900);
    }

    [Fact]
    public async Task Roster_comes_from_the_controller_rows()
    {
        var server = new SseServer();
        var api = new LiveUrlApi();
        var cache = new ScoutingServiceTests.MemoryCacheStub();
        var feed = new LiveMatchFeed(Make(server, api, cache), api, cache, rosterTimeout: TimeSpan.FromMilliseconds(500));
        var roster = await feed.WaitForRosterAsync(new MatchContext(42, 0, 101, DateTimeOffset.UtcNow), new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        var me = roster.Find(101)!;
        Assert.Equal(("Me", 1, Teams.Amber, 1), (me.Name, me.HeroId, me.Team, me.Lane));
        Assert.Equal(Teams.Sapphire, roster.Find(102)!.Team);
    }

    [Fact]
    public async Task A_rejected_query_ends_the_stream_instead_of_retrying_forever()
    {
        var server = new SseServer { ControllerStatus = HttpStatusCode.BadRequest };
        var api = new LiveUrlApi();
        var stream = Make(server, api, new ScoutingServiceTests.MemoryCacheStub());
        var events = new List<LiveEventDto>();
        await foreach (var e in stream.ReadAsync(42, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token)) events.Add(e);
        Assert.True(events[^1].IsEnd);
        Assert.Equal(1, server.Requests.Count(u => !Uri.UnescapeDataString(u.Query).Contains("CCitadelGameRulesProxy")));
    }
}

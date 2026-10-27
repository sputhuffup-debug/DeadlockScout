using System.Text;
using DeadlockScout.Api.Http;
using DeadlockScout.Api.Live;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Models;
using DeadlockScout.Tests.Core;

namespace DeadlockScout.Tests.Api;

public class LiveFeedTests
{
    private sealed class FixtureStream : ILiveEventStream
    {
        public async IAsyncEnumerable<LiveEventDto> ReadAsync(long matchId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(Fixtures.Read("api", "live-events.sse")));
            await foreach (var sse in SseReader.ReadAsync(ms, ct))
            {
                var dto = LiveEventDto.TryParse(sse.Data, sse.Event);
                if (dto is not null) yield return dto;
            }
        }
    }

    private sealed class RankApi : ScoutingServiceTests.FakeApiBase
    {
        public int ActiveCalls;
        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<PlayerRank>> RanksAsync(IEnumerable<uint> accountIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<PlayerRank>>(accountIds.Select(id => new PlayerRank(id, id == 101 ? 73 : 0, 7, 3)).ToList());
        public override Task<ActiveMatch?> ActiveMatchAsync(long matchId, CancellationToken ct)
        {
            ActiveCalls++;
            var players = Enumerable.Range(103, 10).Select(i => new ActiveMatchPlayer((uint)i, i % 2 == 0 ? Teams.Amber : Teams.Sapphire, i)).ToList();
            return Task.FromResult<ActiveMatch?>(new ActiveMatch(matchId, 99, DateTimeOffset.UtcNow, TimeSpan.Zero, players));
        }
    }

    private static readonly MatchContext Ctx = new(42, 99, 101, DateTimeOffset.UtcNow);

    [Fact]
    public void Event_parsing_tolerates_connection_banner_and_end()
    {
        Assert.Null(LiveEventDto.TryParse("not json", "message"));
        Assert.True(LiveEventDto.TryParse("end", "end")!.IsEnd);
        var e = LiveEventDto.TryParse("{\"tick\":1,\"game_time\":2.5,\"event_type\":\"entity_update\",\"entity_type\":\"player_controller\",\"entity_index\":5,\"steam_id\":\"101\",\"net_worth\":10,\"future_field\":[1]}", "player_controller_entity_updated")!;
        Assert.True(e.IsPlayerController);
        Assert.Equal(101u, e.SteamId);
        Assert.Equal(2.5, e.GameTime);
        Assert.NotNull(e.Extra);
    }

    [Fact]
    public async Task Ticks_are_emitted_per_tick_end_for_changed_net_worth_and_the_stream_ends()
    {
        var feed = new LiveMatchFeed(new FixtureStream(), new RankApi(), new ScoutingServiceTests.MemoryCacheStub(), rosterTimeout: TimeSpan.FromSeconds(1));
        var ticks = new List<NetWorthTick>();
        await foreach (var t in feed.TicksAsync(Ctx, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token)) ticks.Add(t);
        // first tick_end (game_time -5 → clamped 0 since negative is ignored): both players at 0; second: 1200/900; third tick_end: no change; fourth: 2600
        Assert.Contains(ticks, t => t.AccountId == 101 && t.NetWorth == 1200 && t.GameTime == TimeSpan.FromSeconds(100) && t.Kills == 1);
        Assert.Contains(ticks, t => t.AccountId == 102 && t.NetWorth == 900);
        Assert.Contains(ticks, t => t.AccountId == 101 && t.NetWorth == 2600 && t.GameTime == TimeSpan.FromSeconds(220));
        Assert.DoesNotContain(ticks, t => t.GameTime == TimeSpan.FromSeconds(101)); // unchanged values are not re-emitted
        Assert.True(ticks.Select(t => t.GameTime).Zip(ticks.Skip(1).Select(t => t.GameTime)).All(p => p.First <= p.Second));
    }

    [Fact]
    public async Task Roster_falls_back_to_active_matches_and_takes_ranks_from_the_rank_endpoint()
    {
        var api = new RankApi();
        var feed = new LiveMatchFeed(new FixtureStream(), api, new ScoutingServiceTests.MemoryCacheStub(), rosterTimeout: TimeSpan.FromMilliseconds(500));
        var roster = await feed.WaitForRosterAsync(Ctx, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.Equal(12, roster.Players.Count); // 2 from the feed + 10 from matches/active
        Assert.Equal(1, api.ActiveCalls);
        var me = roster.Find(101)!;
        Assert.Equal("Me", me.Name);
        Assert.Equal(1, me.HeroId);
        Assert.Equal(Teams.Amber, me.Team);
        Assert.Equal(1, me.Lane);
        Assert.Equal(73, me.Rank);
        Assert.Null(roster.Find(102)!.Rank); // badge 0 = unranked → null
    }

    [Fact]
    public async Task Soul_source_filters_my_ticks()
    {
        var feed = new LiveMatchFeed(new FixtureStream(), new RankApi(), new ScoutingServiceTests.MemoryCacheStub());
        var src = new LiveFeedSoulSource(feed);
        var samples = new List<SoulSample>();
        await foreach (var s in src.ReadAsync(Ctx, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token)) samples.Add(s);
        Assert.All(samples, s => Assert.Equal(SampleSource.LiveFeed, s.Source));
        Assert.Equal(new[] { 0, 1200, 2600 }, samples.Select(s => s.Souls));
        Assert.Equal(SampleSource.LiveFeed, src.Kind);
        Assert.True(src.ExpectedLatency > TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_subscriber_that_joins_after_the_end_event_completes_instead_of_waiting()
    {
        var feed = new LiveMatchFeed(new FixtureStream(), new RankApi(), new ScoutingServiceTests.MemoryCacheStub());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var _ in feed.TicksAsync(Ctx, timeout.Token)) { } // drains to the fixture's end event
        var late = new List<NetWorthTick>();
        await foreach (var t in feed.TicksAsync(Ctx, timeout.Token)) late.Add(t);
        Assert.Empty(late);
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public void Events_url_is_the_live_events_service_shape()
    {
        Assert.Equal("http://localhost:3000/v1/matches/42/live/demo/events?subscribed_entities=player_controller", SseLiveEventStream.EventsUrl("http://localhost:3000/", 42));
    }
}

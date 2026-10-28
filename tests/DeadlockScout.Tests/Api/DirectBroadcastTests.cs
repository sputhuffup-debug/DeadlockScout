using System.Net;
using System.Runtime.CompilerServices;
using DeadlockScout.Api.Live;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using DeadlockScout.Tests.Core;

namespace DeadlockScout.Tests.Api;

public class DirectBroadcastTests
{
    private const double Tick = 1.0 / 64;

    [Fact]
    public void Clock_prefers_the_match_clock_and_freezes_while_paused()
    {
        // match clock read 300 s at tick 64000; 640 ticks later it reads 310 s
        Assert.Equal(310, BroadcastClock.GameTimeSeconds(new ClockState(64640, Tick, 40, 64000, 300f, false))!.Value, 6);
        Assert.Equal(300, BroadcastClock.GameTimeSeconds(new ClockState(64640, Tick, 40, 64000, 300f, true))!.Value, 6);
    }

    [Fact]
    public void Clock_falls_back_to_game_start_time_and_is_unknown_before_the_rules()
    {
        Assert.Equal(60, BroadcastClock.GameTimeSeconds(new ClockState(6400, Tick, 40, 0, 0, false))!.Value, 6);
        Assert.Null(BroadcastClock.GameTimeSeconds(new ClockState(6400, Tick, 0, 0, 0, false)));
        Assert.Null(BroadcastClock.GameTimeSeconds(new ClockState(0, Tick, 40, 0, 0, false)));
        Assert.Null(BroadcastClock.GameTimeSeconds(new ClockState(6400, 0, 40, 0, 0, false)));
    }

    [Fact]
    public void Snapshot_maps_players_skips_bots_and_closes_with_tick_end()
    {
        var players = new[]
        {
            new PlayerState(5, 76561197960265829UL, "Me", Teams.Amber, 13, 4, 2600, 3, 1, 5),
            new PlayerState(6, 0, "Bot", Teams.Sapphire, 7, 1, 100, 0, 0, 0),
            new PlayerState(7, 76561197960265830UL, "Foe", Teams.Sapphire, 0, -1, 900, 0, 2, 0),
        };
        var events = BroadcastEventMapper.Snapshot(players, 20000, 250).ToList();
        Assert.Equal(3, events.Count);
        var me = events[0];
        Assert.True(me.IsPlayerController);
        Assert.Equal((101u, "Me", Teams.Amber, 13, 4), (me.SteamId!.Value, me.SteamName!, me.Team!.Value, me.HeroId!.Value, me.AssignedLane!.Value));
        Assert.Equal((2600, 3, 1, 5), (me.NetWorth!.Value, me.Kills!.Value, me.Deaths!.Value, me.Assists!.Value));
        Assert.Equal(250, me.GameTime);
        Assert.Null(events[1].HeroId);        // hero not picked yet
        Assert.Null(events[1].AssignedLane);  // lane not assigned yet
        Assert.True(events[2].IsTickEnd);

        // before the clock is known: players only, no tick_end, no game time
        var early = BroadcastEventMapper.Snapshot(players, 100, null).ToList();
        Assert.Equal(2, early.Count);
        Assert.All(early, e => Assert.Null(e.GameTime));
    }

    [Fact]
    public async Task Snapshots_drive_the_existing_feed_to_ticks_and_a_roster()
    {
        var stream = new ScriptedStream(Snapshots());
        var api = new RankFreeApi();
        var cache = new ScoutingServiceTests.MemoryCacheStub();
        var feed = new LiveMatchFeed(stream, api, cache, rosterTimeout: TimeSpan.FromMilliseconds(500));
        var ctx = new MatchContext(42, 0, 101, DateTimeOffset.UtcNow);
        var ticks = new List<NetWorthTick>();
        await foreach (var t in feed.TicksAsync(ctx, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token)) ticks.Add(t);
        Assert.Equal(new[] { 0, 600, 1200 }, ticks.Where(t => t.AccountId == 101).Select(t => t.NetWorth));
        Assert.Equal(TimeSpan.FromSeconds(2), ticks.Last(t => t.AccountId == 101).GameTime);
    }

    [Fact]
    public async Task A_purchase_emits_a_tick_with_the_items_even_when_net_worth_stands_still()
    {
        // Buying turns souls into an item: net worth is the same, the item list is not.
        const long monsterRounds = 1009965641, leech = 865846625;
        var stream = new ScriptedStream(Purchases());
        var feed = new LiveMatchFeed(stream, new RankFreeApi(), new ScoutingServiceTests.MemoryCacheStub(), rosterTimeout: TimeSpan.FromMilliseconds(500));
        var ticks = new List<NetWorthTick>();
        await foreach (var t in feed.TicksAsync(new MatchContext(42, 0, 101, DateTimeOffset.UtcNow), new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token)) ticks.Add(t);
        Assert.Equal(3, ticks.Count);
        Assert.Equal(new[] { monsterRounds }, ticks[0].Items);
        Assert.Equal(new[] { monsterRounds, leech }, ticks[1].Items);
        Assert.Equal(TimeSpan.FromSeconds(1), ticks[1].GameTime);
        Assert.Equal(true, ticks[2].UltimateTrained);

        static IEnumerable<LiveEventDto> Purchases()
        {
            var steps = new (long[] Items, bool Ult)[] { (new[] { monsterRounds }, false), (new[] { monsterRounds, leech }, false), (new[] { monsterRounds, leech }, true) };
            for (int s = 0; s < steps.Length; s++)
                foreach (var e in BroadcastEventMapper.Snapshot(new[] { new PlayerState(5, 76561197960265829UL, "Foe", Teams.Sapphire, 1, 1, 7000, 0, 0, 0, steps[s].Items, steps[s].Ult) }, 6400 + 64 * s, s))
                    yield return e;
            yield return new LiveEventDto { EventType = "end" };
        }
    }

    private static IEnumerable<LiveEventDto> Snapshots()
    {
        for (int s = 0; s <= 2; s++)
            foreach (var e in BroadcastEventMapper.Snapshot(new[] { new PlayerState(5, 76561197960265829UL, "Me", Teams.Amber, 1, 1, 600 * s, 0, 0, 0) }, 6400 + 64 * s, s))
                yield return e;
        yield return new LiveEventDto { EventType = "end" };
    }

    [Fact]
    public async Task Selector_falls_back_to_the_hosted_feed_when_the_broadcast_cannot_be_parsed()
    {
        var hosted = new ScriptedStream(new[] { new LiveEventDto { EventType = "tick_end", GameTime = 9 }, new LiveEventDto { EventType = "end" } });
        var direct = new ScriptedStream(new[] { new LiveEventDto { EventType = "tick_end", GameTime = 1 } }, throwAfter: new BroadcastParseException("unknown field type", new InvalidOperationException()));
        var selector = new LiveEventStreamSelector(hosted, new ScriptedStream(Array.Empty<LiveEventDto>()), direct,
            new ApiClientTests.StaticOptions(new ScoutSettings { Api = new ApiSettings { LiveSource = LiveSource.Direct } }));
        var times = new List<double?>();
        await foreach (var e in selector.ReadAsync(42, default)) times.Add(e.GameTime);
        Assert.Equal(new double?[] { 1, 9, null }, times);

        var strict = new LiveEventStreamSelector(hosted, hosted, new ScriptedStream(Array.Empty<LiveEventDto>(), throwAfter: new BroadcastParseException("x", new InvalidOperationException())),
            new ApiClientTests.StaticOptions(new ScoutSettings { Api = new ApiSettings { LiveSource = LiveSource.Direct, DirectFallbackToHosted = false } }));
        await Assert.ThrowsAsync<BroadcastParseException>(async () => { await foreach (var _ in strict.ReadAsync(42, default)) { } });
    }

    [Fact]
    public async Task Direct_is_the_default_source_and_falls_back_to_hosted_by_default()
    {
        var defaults = new ScoutSettings();
        Assert.Equal(LiveSource.Direct, defaults.Api.LiveSource);
        Assert.True(defaults.Api.DirectFallbackToHosted);

        var hosted = new ScriptedStream(new[] { new LiveEventDto { EventType = "tick_end", GameTime = 9 } });
        var direct = new ScriptedStream(new[] { new LiveEventDto { EventType = "tick_end", GameTime = 3 } });
        var selector = new LiveEventStreamSelector(hosted, new ScriptedStream(Array.Empty<LiveEventDto>()), direct, new ApiClientTests.StaticOptions(defaults));
        var got = new List<double?>();
        await foreach (var e in selector.ReadAsync(1, default)) got.Add(e.GameTime);
        Assert.Equal(new double?[] { 3 }, got);
    }

    [Fact]
    public void WithLiveSource_changes_only_the_live_source()
    {
        var before = new ScoutSettings { AccountId = 42, Api = new ApiSettings { ApiKey = "k", LiveUrl = "http://box:3000" } };
        var after = before.WithLiveSource(LiveSource.Hosted);
        Assert.Equal(LiveSource.Hosted, after.Api.LiveSource);
        Assert.Equal(LiveSource.Direct, before.Api.LiveSource);
        Assert.Equal(before with { Api = before.Api with { LiveSource = LiveSource.Hosted } }, after);
        Assert.Same(before, before.WithLiveSource(LiveSource.Direct));
    }

    [Fact]
    public async Task Selector_routes_each_source()
    {
        LiveEventDto Mark(double t) => new() { EventType = "tick_end", GameTime = t };
        var hosted = new ScriptedStream(new[] { Mark(1) });
        var self = new ScriptedStream(new[] { Mark(2) });
        var direct = new ScriptedStream(new[] { Mark(3) });
        foreach (var (source, expected) in new[] { (LiveSource.Hosted, 1.0), (LiveSource.SelfHosted, 2.0), (LiveSource.Direct, 3.0) })
        {
            var sel = new LiveEventStreamSelector(hosted, self, direct, new ApiClientTests.StaticOptions(new ScoutSettings { Api = new ApiSettings { LiveSource = source } }));
            var got = new List<double?>();
            await foreach (var e in sel.ReadAsync(1, default)) got.Add(e.GameTime);
            Assert.Equal(new double?[] { expected }, got);
        }
    }

    [Fact]
    public async Task Resolver_uses_the_api_and_gives_up_without_guessing()
    {
        var cache = new ScoutingServiceTests.MemoryCacheStub();
        var ok = new UrlApi(_ => Task.FromResult("http://dist1-ord1.steamcontent.com/tv/99_333968351"));
        Assert.Equal("http://dist1-ord1.steamcontent.com/tv/99_333968351", await new BroadcastUrlResolver(ok, cache).ResolveAsync(99, default));
        Assert.Equal(1, ok.Calls);
        Assert.Equal("http://dist1-ord1.steamcontent.com/tv/99_333968351", await new BroadcastUrlResolver(ok, cache).ResolveAsync(99, default));
        Assert.Equal(1, ok.Calls); // cached

        var notLive = new UrlApi(_ => throw new HttpRequestException("cannot be live", null, HttpStatusCode.BadRequest));
        Assert.Null(await new BroadcastUrlResolver(notLive, new ScoutingServiceTests.MemoryCacheStub()).ResolveAsync(99, default));
        Assert.Equal(1, notLive.Calls); // a definite answer is not retried

        var down = new UrlApi(_ => throw new HttpRequestException("timeout"));
        var r = new BroadcastUrlResolver(down, new ScoutingServiceTests.MemoryCacheStub()) { Backoff = _ => TimeSpan.Zero };
        Assert.Null(await r.ResolveAsync(99, default));
        Assert.Equal(4, down.Calls);
    }

    [Fact]
    public void Relay_is_read_over_https()
    {
        Assert.Equal("https://dist1-ord1.steamcontent.com/tv/99_1", DirectBroadcastStream.RelayUrl("http://dist1-ord1.steamcontent.com/tv/99_1"));
        Assert.Equal("https://dist1-ord1.steamcontent.com/tv/99_1", DirectBroadcastStream.RelayUrl("https://dist1-ord1.steamcontent.com/tv/99_1"));
    }

    /// <summary>A relay that refuses (405, as on the first real match) a few times before the broadcast is up.</summary>
    private static (DirectBroadcastStream stream, List<string> urls, List<TimeSpan> waits) RefusingRelay(int refusals, TimeSpan patience)
    {
        var urls = new List<string>(); var waits = new List<TimeSpan>();
        var now = DateTimeOffset.UnixEpoch;
        async IAsyncEnumerable<LiveEventDto> Session(string url, [EnumeratorCancellation] CancellationToken ct)
        {
            urls.Add(url);
            await Task.Yield();
            if (urls.Count <= refusals) throw new HttpRequestException("Response status code does not indicate success: 405 (Method Not Allowed).", null, HttpStatusCode.MethodNotAllowed);
            yield return new LiveEventDto { EventType = "tick_end", GameTime = 1 }; // then the broadcast finishes
        }
        var resolver = new BroadcastUrlResolver(new UrlApi(_ => Task.FromResult("http://dist1-ord1.steamcontent.com/tv/99_1")), new ScoutingServiceTests.MemoryCacheStub());
        var stream = new DirectBroadcastStream(resolver, new ApiClientTests.StaticOptions(new ScoutSettings()))
        {
            Session = Session,
            NotStartedPatience = patience,
            Delay = (d, _) => { waits.Add(d); now += d; return Task.CompletedTask; },
            Now = () => now,
        };
        return (stream, urls, waits);
    }

    [Fact]
    public async Task Direct_keeps_asking_a_relay_that_has_not_started_the_broadcast()
    {
        var (stream, urls, waits) = RefusingRelay(refusals: 12, TimeSpan.FromMinutes(6)); // two minutes of refusals
        var got = new List<string?>();
        await foreach (var e in stream.ReadAsync(99, default)) got.Add(e.EventType);
        Assert.Equal(new[] { "tick_end", "end" }, got);
        Assert.Equal(13, urls.Count);
        Assert.All(urls, u => Assert.StartsWith("https://", u));
        Assert.All(waits, w => Assert.Equal(TimeSpan.FromSeconds(10), w)); // never faster than the polling budget
    }

    [Fact]
    public async Task Direct_gives_up_on_a_relay_that_never_serves_and_the_selector_switches_to_hosted()
    {
        var (stream, urls, _) = RefusingRelay(refusals: int.MaxValue, TimeSpan.FromMinutes(6));
        await Assert.ThrowsAsync<BroadcastUnavailableException>(async () => { await foreach (var _ in stream.ReadAsync(99, default)) { } });
        Assert.Equal(37, urls.Count); // every 10 s for 6 minutes

        var (never, _, _) = RefusingRelay(refusals: int.MaxValue, TimeSpan.FromMinutes(1));
        var hosted = new ScriptedStream(new[] { new LiveEventDto { EventType = "tick_end", GameTime = 9 } });
        var selector = new LiveEventStreamSelector(hosted, hosted, never, new ApiClientTests.StaticOptions(new ScoutSettings()));
        var times = new List<double?>();
        await foreach (var e in selector.ReadAsync(99, default)) times.Add(e.GameTime);
        Assert.Equal(new double?[] { 9 }, times);
    }

    [Fact]
    public void Network_failures_are_told_apart_from_parse_failures()
    {
        Assert.True(DirectBroadcastStream.IsNetwork(new HttpRequestException("reset")));
        Assert.True(DirectBroadcastStream.IsNetwork(new InvalidOperationException("wrapped", new IOException("eof"))));
        Assert.False(DirectBroadcastStream.IsNetwork(new IndexOutOfRangeException()));
        Assert.False(DirectBroadcastStream.IsNetwork(new InvalidOperationException("bad field")));
    }

    private sealed class ScriptedStream(IEnumerable<LiveEventDto> events, Exception? throwAfter = null) : ILiveEventStream
    {
        public async IAsyncEnumerable<LiveEventDto> ReadAsync(long matchId, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var e in events) { await Task.Yield(); yield return e; }
            if (throwAfter is not null) throw throwAfter;
        }
    }

    private sealed class RankFreeApi : ScoutingServiceTests.FakeApiBase
    {
        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<PlayerRank>> RanksAsync(IEnumerable<uint> accountIds, CancellationToken ct) => Task.FromResult<IReadOnlyList<PlayerRank>>(new List<PlayerRank>());
    }

    private sealed class UrlApi(Func<long, Task<string>> liveUrl) : ScoutingServiceTests.FakeApiBase
    {
        public int Calls;
        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<string> LiveUrlAsync(long matchId, CancellationToken ct) { Calls++; return liveUrl(matchId); }
    }
}

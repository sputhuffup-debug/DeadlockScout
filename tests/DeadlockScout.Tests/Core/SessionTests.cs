using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Session;
using DeadlockScout.Core.Settings;
using DeadlockScout.Tests.Api;

namespace DeadlockScout.Tests.Core;

public class SessionTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2");

    /// <summary>A game that started at the given local (UTC+2) time.</summary>
    private static MatchHistoryEntry Game(long id, string localStart, int outcome, int? badge = null, int hero = 13)
        => new(id, hero, DateTimeOffset.Parse(localStart + "+02:00"), TimeSpan.FromMinutes(30), Teams.Amber, 1, 1, 1, 20000, outcome, badge, null);

    private static readonly MatchHistoryEntry[] History =
    {
        Game(1, "2026-10-26T21:00", 1, badge: 71),  // yesterday: the rank the day started at
        Game(2, "2026-10-27T18:00", 1, badge: 72),
        Game(3, "2026-10-27T19:00", 1),
        Game(4, "2026-10-27T20:00", 2),
        Game(5, "2026-10-27T21:00", 5),             // not scored: neither a win nor a loss, and does not break the streak
        Game(6, "2026-10-27T22:00", 2),
        Game(7, "2026-10-28T00:30", 2, badge: 73),  // after midnight, still the same day
    };

    [Theory]
    [InlineData("2026-10-27T22:00", "2026-10-27T05:00")]
    [InlineData("2026-10-28T01:30", "2026-10-27T05:00")] // past midnight still counts with the evening
    [InlineData("2026-10-28T05:00", "2026-10-28T05:00")]
    public void Day_starts_at_the_configured_local_hour(string localNow, string localDayStart)
        => Assert.Equal(DateTimeOffset.Parse(localDayStart + "+02:00"), SessionMath.DayStart(DateTimeOffset.Parse(localNow + "+02:00"), Zone, 5));

    [Fact]
    public void Summary_counts_today_finds_the_streak_and_the_rank_change()
    {
        var s = SessionMath.Summarize(History, DateTimeOffset.Parse("2026-10-28T01:00+02:00"), Zone, 5);
        Assert.Equal((2, 3), (s.Wins, s.Losses));
        Assert.Equal(-3, s.Streak);      // games 7, 6 and 4 lost; 5 is skipped; 3 was a win
        Assert.Equal(73, s.BadgeNow);
        Assert.Equal(71, s.BadgeAtStart);
        Assert.Equal(2, s.BadgeChange);
    }

    [Fact]
    public void A_new_day_starts_empty_but_keeps_the_streak_and_rank()
    {
        var s = SessionMath.Summarize(History, DateTimeOffset.Parse("2026-10-28T12:00+02:00"), Zone, 5);
        Assert.Equal(0, s.Games);
        Assert.Equal(-3, s.Streak);
        Assert.Equal(73, s.BadgeNow);
        Assert.Equal(0, s.BadgeChange);
        var empty = SessionMath.Summarize(Array.Empty<MatchHistoryEntry>(), DateTimeOffset.UtcNow, Zone, 5);
        Assert.Equal((0, 0, (int?)null, (int?)null), (empty.Games, empty.Streak, empty.BadgeNow, empty.BadgeChange));
    }

    [Theory]
    [InlineData(-3, 3, true)]
    [InlineData(-2, 3, false)]
    [InlineData(-5, 0, false)] // turned off
    [InlineData(4, 3, false)]  // a win streak never nudges
    public void Nudge_only_after_enough_losses_in_a_row(int streak, int threshold, bool expected)
        => Assert.Equal(expected, SessionMath.Nudge(new SessionSummary(0, 0, streak, null, null, DateTimeOffset.UtcNow), threshold) is not null);

    [Fact]
    public void Hero_pool_is_your_most_played_heroes_against_the_meta()
    {
        var mine = new[]
        {
            new PlayerHeroStats(1, 13, 214, 118, 0, 0, 0), new PlayerHeroStats(1, 1, 96, 45, 0, 0, 0),
            new PlayerHeroStats(1, 7, 51, 27, 0, 0, 0), new PlayerHeroStats(1, 2, 3, 1, 0, 0, 0), new PlayerHeroStats(1, 4, 0, 0, 0, 0, 0),
        };
        var meta = new[] { new AnalyticsHeroStats(13, 1000, 512, 0, 0, 0, 0), new AnalyticsHeroStats(1, 1000, 497, 0, 0, 0, 0) };
        var pool = SessionMath.HeroPool(mine, meta, 3);
        Assert.Equal(new[] { 13, 1, 7 }, pool.Select(p => p.HeroId));
        Assert.Equal(118.0 / 214 - 0.512, pool[0].VersusMeta!.Value, 6);
        Assert.Null(pool[2].MetaWinRate); // no meta row for that hero
        Assert.Empty(SessionMath.HeroPool(mine, meta, 0));
    }

    private sealed class SessionApi : ScoutingServiceTests.FakeApiBase
    {
        public bool FailHeroStats;
        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct)
            => FailHeroStats ? throw new HttpRequestException("500") : Task.FromResult<IReadOnlyList<PlayerHeroStats>>(new[] { new PlayerHeroStats(1000, 13, 50, 30, 0, 0, 0) });
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<MatchHistoryEntry>> MatchHistoryAsync(uint accountId, CancellationToken ct) => Task.FromResult<IReadOnlyList<MatchHistoryEntry>>(History);
        public RankBracket? MetaBracket;
        public override Task<IReadOnlyList<AnalyticsHeroStats>> AnalyticsHeroStatsAsync(AnalyticsQuery q, CancellationToken ct)
        {
            MetaBracket = new RankBracket(q.MinAverageBadge ?? -1, q.MaxAverageBadge ?? -1);
            return Task.FromResult<IReadOnlyList<AnalyticsHeroStats>>(new[] { new AnalyticsHeroStats(13, 100, 50, 0, 0, 0, 0) });
        }
    }

    private sealed class Patch : IPatchKeyProvider { public ValueTask<string> GetAsync(CancellationToken ct) => ValueTask.FromResult("v1"); }
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-10-28T01:00+02:00"); }

    [Fact]
    public async Task Service_builds_the_view_and_keeps_today_when_the_hero_pool_fails()
    {
        var api = new SessionApi();
        var svc = new SessionService(api, new ScoutingServiceTests.MemoryCacheStub(), new Patch(), new Clock(), new ApiClientTests.StaticOptions(new ScoutSettings()), Zone);
        var view = (await svc.RefreshAsync(1000, default))!;
        Assert.Equal((2, 3), (view.Today.Wins, view.Today.Losses));
        Assert.NotNull(view.Nudge);                               // three losses in a row
        Assert.Equal(new RankBracket(63, 83), api.MetaBracket);   // badge 73 ± 10
        Assert.Equal(0.6 - 0.5, view.HeroPool.Single().VersusMeta!.Value, 6);
        Assert.Null(await svc.RefreshAsync(0, default));

        var failing = new SessionService(new SessionApi { FailHeroStats = true }, new ScoutingServiceTests.MemoryCacheStub(), new Patch(), new Clock(),
            new ApiClientTests.StaticOptions(new ScoutSettings()), Zone);
        var partial = (await failing.RefreshAsync(1000, default))!;
        Assert.Empty(partial.HeroPool);
        Assert.Equal(5, partial.Today.Games);
    }

    private static PaceCurve Gate(int heroId) => new(heroId, new RankBracket(80, 89), "v1",
        Enumerable.Range(0, 51).Select(m => new PacePoint(m, null, null, null, m * 1000)).ToList(), 1000, "analytics"); // 1,000 souls a minute

    private static MatchHistoryEntry Game(long id, int hero, int minutes, int netWorth) => History[0] with
    {
        MatchId = id, HeroId = hero, Duration = TimeSpan.FromMinutes(minutes), NetWorth = netWorth,
        StartTime = DateTimeOffset.Parse("2026-10-20T20:00Z").AddHours(id),
    };

    [Fact]
    public void Hero_pace_is_each_recent_game_against_the_gate_at_its_length_with_a_trend()
    {
        var games = new[]
        {
            Game(1, 13, 30, 27_000), Game(2, 13, 30, 27_000),   // older: 10% under the gate
            Game(3, 13, 20, 22_000), Game(4, 13, 40, 44_000),   // newer: 10% over, whatever the game's length
            Game(5, 13, 5, 1_000),                              // a remake: left out
            Game(6, 1, 30, 30_000),                             // another hero
        };
        var pace = SessionMath.Pace(13, games, Gate(13), "Oracle")!;
        Assert.Equal(4, pace.Games);
        Assert.Equal(0.0, pace.VersusGate, 6);
        Assert.Equal(0.2, pace.Trend!.Value, 6);
        Assert.Equal("pace +0% vs Oracle, rising", SessionMath.PaceText(pace));

        var few = SessionMath.Pace(13, games.Take(3).ToList(), Gate(13), "Oracle")!;
        Assert.Null(few.Trend);                                  // three games: no trend yet
        Assert.Equal("pace −3% vs Oracle", SessionMath.PaceText(few));
        Assert.Null(SessionMath.Pace(13, games, null, "Oracle"));  // no gate curve for the hero
        Assert.Null(SessionMath.Pace(7, games, Gate(7), "Oracle")); // no games on it
        Assert.Equal("pace −12% vs Oracle, falling", SessionMath.PaceText(new HeroPace(6, -0.12, -0.05, "Oracle")));
        Assert.Equal("pace +4% vs Oracle, steady", SessionMath.PaceText(new HeroPace(6, 0.04, 0.01, "Oracle")));
    }

    private sealed class GateStub : IPaceBenchmark
    {
        public RankBracket? Asked;
        public int Priority => 1;
        public string Name => "analytics";
        public Task<PaceCurve?> GetAsync(int heroId, RankBracket bracket, CancellationToken ct) { Asked = bracket; return Task.FromResult<PaceCurve?>(Gate(heroId)); }
    }

    [Fact]
    public async Task Service_adds_the_gate_pace_to_the_hero_pool()
    {
        var gate = new GateStub();
        var svc = new SessionService(new SessionApi(), new ScoutingServiceTests.MemoryCacheStub(), new Patch(), new Clock(),
            new ApiClientTests.StaticOptions(new ScoutSettings()), Zone, gate: gate);
        var row = (await svc.RefreshAsync(1000, default))!.HeroPool.Single();
        Assert.NotNull(gate.Asked);
        Assert.Equal(RankGoal.Bracket(RankGoal.Resolve(new GoalSettings(), 73)), gate.Asked);  // the tier above badge 73
        Assert.Equal(RankGoal.Name(RankGoal.Resolve(new GoalSettings(), 73)), row.Pace?.GateRank);
    }

    [Fact]
    public void Pace_nudge_after_enough_games_today_all_under_the_gate()
    {
        var dayStart = DateTimeOffset.Parse("2026-10-27T05:00+02:00");
        var gate = Gate(13);
        var today = new[]
        {
            Game(103, 13, 30, 27_000) with { StartTime = dayStart.AddHours(6) },    // newest: 10% under
            Game(102, 13, 30, 29_000) with { StartTime = dayStart.AddHours(5) },    // 3% under
            Game(101, 13, 5, 1_000) with { StartTime = dayStart.AddHours(4.5) },    // remake: skipped
            Game(100, 13, 20, 19_000) with { StartTime = dayStart.AddHours(4) },    // 5% under
            Game(99, 13, 30, 33_000) with { StartTime = dayStart.AddHours(-3) },    // yesterday, over
        };
        var recent = SessionMath.RecentVersusGate(today, _ => gate, 3);
        Assert.Equal(new long[] { 103, 102, 100 }, recent.Select(g => g.MatchId));
        Assert.Equal("Under Oracle's pace 3 games running. A short break usually helps more than the next queue.",
            SessionMath.PaceNudge(recent, dayStart, 3, "Oracle"));
        Assert.Null(SessionMath.PaceNudge(SessionMath.RecentVersusGate(today, _ => gate, 4), dayStart, 4, "Oracle")); // the 4th was yesterday
        Assert.Null(SessionMath.PaceNudge(recent, dayStart, 0, "Oracle"));                                             // off
        var oneOver = SessionMath.RecentVersusGate(today.Select(g => g.MatchId == 102 ? g with { NetWorth = 31_000 } : g).ToList(), _ => gate, 3);
        Assert.Null(SessionMath.PaceNudge(oneOver, dayStart, 3, "Oracle"));
        Assert.Empty(SessionMath.RecentVersusGate(today, _ => null, 3));                                               // no curves
    }

    [Fact]
    public void Teammates_are_the_friends_you_queued_with_most_with_your_pace_together()
    {
        var history = new[]
        {
            Game(1, 13, 30, 18_000), Game(2, 13, 30, 18_000),    // 600 spm with Ava
            Game(3, 13, 30, 15_000), Game(4, 13, 30, 15_000),    // 500 spm alone
            Game(5, 13, 5, 500),                                 // a remake with Ava: left out
        };
        var mates = new[]
        {
            new PartyMateGames(7, 4, 3, new long[] { 1, 2, 5, 99 }),  // 99 is older than the history: not counted for pace
            new PartyMateGames(8, 2, 1, new long[] { 3, 4 }),         // under three games together
            new PartyMateGames(9, 6, 2, Array.Empty<long>()),
        };
        var rows = SessionMath.Teammates(mates, history, new Dictionary<uint, string> { [7] = "Ava" }, 3);
        Assert.Equal(new uint[] { 9, 7 }, rows.Select(r => r.AccountId));     // most games first
        var ava = rows[1];
        Assert.Equal((3, 1), (ava.Wins, ava.Losses));
        Assert.Equal(600, ava.Spm!.Value, 6);
        Assert.Equal(600.0 / 550 - 1, ava.VersusUsual!.Value, 6);               // usual: 550 over the four full games
        Assert.Equal("Ava 3–1 · 600 spm (+9% on your usual)", SessionMath.TeammateText(ava));
        Assert.Equal("a friend 2–4", SessionMath.TeammateText(rows[0]));         // no name, no games in the history
        Assert.Empty(SessionMath.Teammates(mates, history, new Dictionary<uint, string>(), 0));
    }

    private sealed class MatesApi : ScoutingServiceTests.FakeApiBase, IDeadlockApi
    {
        public int NameCalls;
        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct) => Task.FromResult<IReadOnlyList<PlayerHeroStats>>(Array.Empty<PlayerHeroStats>());
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<MatchHistoryEntry>> MatchHistoryAsync(uint accountId, CancellationToken ct) => Task.FromResult<IReadOnlyList<MatchHistoryEntry>>(History);
        public override Task<IReadOnlyList<AnalyticsHeroStats>> AnalyticsHeroStatsAsync(AnalyticsQuery q, CancellationToken ct) => Task.FromResult<IReadOnlyList<AnalyticsHeroStats>>(Array.Empty<AnalyticsHeroStats>());
        public Task<IReadOnlyList<PartyMateGames>> PartyMateGamesAsync(uint accountId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<PartyMateGames>>(new[] { new PartyMateGames(7, 5, 3, Array.Empty<long>()) });
        public Task<IReadOnlyList<PlayerName>> PlayerNamesAsync(IEnumerable<uint> accountIds, CancellationToken ct)
        {
            NameCalls++;
            return Task.FromResult<IReadOnlyList<PlayerName>>(accountIds.Select(id => new PlayerName(id, "Ava")).ToList());
        }
    }

    [Fact]
    public async Task Service_names_the_teammates_once_and_then_from_the_cache()
    {
        var api = new MatesApi();
        var svc = new SessionService(api, new ScoutingServiceTests.MemoryCacheStub(), new Patch(), new Clock(), new ApiClientTests.StaticOptions(new ScoutSettings()), Zone);
        Assert.Equal("Ava", (await svc.RefreshAsync(1000, default))!.Teammates!.Single().Name);
        Assert.Equal("Ava", (await svc.RefreshAsync(1000, default))!.Teammates!.Single().Name);
        Assert.Equal(1, api.NameCalls);

        var off = new SessionService(api, new ScoutingServiceTests.MemoryCacheStub(), new Patch(), new Clock(),
            new ApiClientTests.StaticOptions(new ScoutSettings { Session = new SessionSettings { Teammates = 0 } }), Zone);
        Assert.Empty((await off.RefreshAsync(1000, default))!.Teammates!);
    }

    private static IReadOnlyList<MatchHistoryEntry> Ranked(params int[] badgesOldestFirst)
        => badgesOldestFirst.Select((b, i) => History[0] with
        {
            MatchId = 1000 + i, RankedBadge = b, StartTime = DateTimeOffset.Parse("2026-10-01T20:00Z").AddHours(i),
        }).ToList();

    [Fact]
    public void Rank_forecast_follows_the_last_ranked_games_line()
    {
        // Archon 1 → Archon 3 over 20 games (seven, seven, then six games at each): about one subrank every eight to ten games.
        var rising = RankForecasting.From(Ranked(Enumerable.Repeat(71, 7).Concat(Enumerable.Repeat(72, 7)).Concat(Enumerable.Repeat(73, 6)).ToArray()))!;
        Assert.Equal(RankDirection.Rising, rising.Trend);
        Assert.Equal(73, rising.Badge);
        Assert.Equal(74, rising.NextBadge);
        Assert.InRange(rising.Games!.Value, 7, 11);
        Assert.StartsWith("Rank: at this rate, B74 in about", RankForecasting.Text(rising, b => $"B{b}"));

        var steady = RankForecasting.From(Ranked(73, 74, 73, 73, 74, 73, 73, 74, 73, 73))!;
        Assert.Equal(RankDirection.Steady, steady.Trend);
        Assert.Null(steady.Games);
        Assert.Equal("Rank: holding steady at B73 over your last 10 ranked games", RankForecasting.Text(steady, b => $"B{b}"));

        var falling = RankForecasting.From(Ranked(81, 76, 75, 74, 73, 72, 71, 66))!;  // across a tier boundary
        Assert.Equal(RankDirection.Falling, falling.Trend);
        Assert.Equal(65, falling.NextBadge);
        Assert.Equal(1, falling.Games);

        Assert.Null(RankForecasting.From(Ranked(73, 74, 75)));                      // too few ranked games
        Assert.Null(RankForecasting.From(History.Select(h => h with { RankedBadge = null }).ToList()));
        Assert.Equal(61, RankForecasting.FromIndex(DeadlockScout.Core.History.HistoryMath.BadgeIndex(61)));
        Assert.Equal(76, RankForecasting.FromIndex(DeadlockScout.Core.History.HistoryMath.BadgeIndex(76)));
    }
}

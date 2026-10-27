using DeadlockScout.Core.Abstractions;
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
}

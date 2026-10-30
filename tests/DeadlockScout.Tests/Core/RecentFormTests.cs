using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Scouting;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Tests.Core;

/// <summary>Synthetic history rows; made-up account and match ids.</summary>
public class RecentFormTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static MatchHistoryEntry E(long id, int hero, int hoursAgo, int minutes, int netWorth, bool won)
        => new(id, hero, T0.AddHours(-hoursAgo), TimeSpan.FromMinutes(minutes), Teams.Amber, 1, 1, 1, netWorth, won ? 1 : 2, null, null,
            won ? Teams.Amber : Teams.Sapphire);

    [Fact]
    public void Form_is_the_newest_ten_full_games_on_the_hero_with_pooled_souls_per_minute()
    {
        var rows = new List<MatchHistoryEntry>();
        for (int i = 0; i < 12; i++) rows.Add(E(i, 4, i + 1, 30, 24_000, won: i % 3 != 0)); // newest first by hours ago
        rows.Add(E(100, 4, 0, 3, 900, won: false));     // a 3-minute remake: left out
        rows.Add(E(101, 13, 0, 30, 40_000, won: true));   // another hero: left out
        rows.Add(E(102, 4, 2, 20, 20_000, won: true));     // shorter game, pooled by minutes
        var f = RecentForm.From(rows, 4, careerSpm: 750);
        Assert.Equal(10, f.Games);
        // The ten newest: hours ago 1..9 of the 30-minute games plus the 20-minute one at 2 hours.
        Assert.Equal((10 - 3, 3), (f.Wins, f.Losses));
        Assert.Equal((9 * 24_000 + 20_000) / (9 * 30.0 + 20), f.SoulsPerMinute, 3);
        Assert.Equal(f.SoulsPerMinute / 750 - 1, f.Delta!.Value, 6);

        var none = RecentForm.From(rows, 99, 700);
        Assert.Equal(0, none.Games);
        Assert.Null(none.Delta);
        Assert.Null(RecentForm.From(rows, 4, null).Delta);
    }

    [Fact]
    public async Task The_service_makes_one_history_call_per_opponent_and_hero_and_caches_only_their_games()
    {
        var api = new HistoryApi();
        var cache = new ScoutingServiceTests.MemoryCacheStub();
        var svc = new RecentFormService(api, cache, new Monitor());
        var f = await svc.GetAsync(77, 4, 800, CancellationToken.None);
        await svc.GetAsync(77, 4, 800, CancellationToken.None);
        Assert.Equal(1, api.Calls);
        Assert.Equal((2, 1, 1), (f.Games, f.Wins, f.Losses));
        var cached = Assert.IsType<List<MatchHistoryEntry>>(cache.Store["form:77:4"]);
        Assert.All(cached, e => Assert.Equal(4, e.HeroId));
    }

    private sealed class Monitor : IOptionsMonitor<ScoutSettings>
    {
        public ScoutSettings CurrentValue { get; } = new();
        public ScoutSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ScoutSettings, string?> listener) => null;
    }

    private sealed class HistoryApi : ScoutingServiceTests.FakeApiBase
    {
        public int Calls;
        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<MatchHistoryEntry>> MatchHistoryAsync(uint accountId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<MatchHistoryEntry>>(new[] { E(1, 4, 1, 30, 24_000, true), E(2, 4, 3, 32, 25_000, false), E(3, 6, 2, 30, 20_000, true) });
        }
    }
}

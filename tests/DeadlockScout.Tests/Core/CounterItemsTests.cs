using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Builds;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

/// <summary>Synthetic win counts; item ids are made up.</summary>
public class CounterItemsTests
{
    private static ItemWinStat S(long id, int matches, double winRate) => new(id, matches, (int)Math.Round(matches * winRate));

    [Fact]
    public void The_matchup_shift_is_taken_out_and_only_clear_items_are_kept()
    {
        // Your hero beats this enemy anyway: every item is about 3 points better against them. Item 1 is 3 points better
        // than that on lots of games (a counter); item 2 only rides the matchup; item 3 is 6 points better on 120 games
        // (too few); item 4 is worse.
        var overall = new[] { S(1, 40_000, 0.50), S(2, 80_000, 0.52), S(3, 3_000, 0.48), S(4, 30_000, 0.55), S(5, 60_000, 0.50) };
        var versus = new[] { S(1, 6_000, 0.56), S(2, 12_000, 0.55), S(3, 120, 0.57), S(4, 5_000, 0.56), S(5, 9_000, 0.53) };
        var counters = CounterItems.Rank(7, overall, versus);
        var only = Assert.Single(counters);
        Assert.Equal((1L, 7), (only.ItemId, only.EnemyHeroId));
        Assert.InRange(only.Lift, 0.02, 0.035);
        Assert.True(only.Z >= 2.5);
        Assert.Equal(6_000, only.Matches);
    }

    [Fact]
    public void No_data_or_nothing_above_noise_gives_no_counter()
    {
        Assert.Empty(CounterItems.Rank(7, Array.Empty<ItemWinStat>(), new[] { S(1, 5_000, 0.6) }));
        var flat = new[] { S(1, 40_000, 0.50), S(2, 40_000, 0.52) };
        Assert.Empty(CounterItems.Rank(7, flat, new[] { S(1, 2_000, 0.505), S(2, 2_000, 0.52) }));
    }

    [Fact]
    public async Task The_service_caches_the_baseline_once_and_each_pair_once()
    {
        var api = new CounterApi();
        var svc = new CounterItemService(api, new ScoutingServiceTests.MemoryCacheStub(), new Patch(), TimeSpan.FromDays(7));
        var first = await svc.GetAsync(13, 2, CancellationToken.None);
        await svc.GetAsync(13, 2, CancellationToken.None);
        await svc.GetAsync(13, 4, CancellationToken.None);
        Assert.Equal(new[] { "13:-", "13:2", "13:4" }, api.Calls);
        Assert.Equal(1L, Assert.Single(first).ItemId);
        Assert.Empty(await svc.GetAsync(0, 4, CancellationToken.None));
    }

    private sealed class Patch : IPatchKeyProvider { public ValueTask<string> GetAsync(CancellationToken ct) => ValueTask.FromResult("v1"); }

    private sealed class CounterApi : ScoutingServiceTests.FakeApiBase, IDeadlockApi
    {
        public readonly List<string> Calls = new();
        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ItemWinStat>> ItemWinStatsAsync(int heroId, int? enemyHeroId, CancellationToken ct)
        {
            Calls.Add($"{heroId}:{(enemyHeroId is { } e ? e.ToString() : "-")}");
            IReadOnlyList<ItemWinStat> rows = enemyHeroId is null
                ? new[] { S(1, 40_000, 0.50), S(2, 80_000, 0.52) }
                : new[] { S(1, 6_000, 0.55), S(2, 12_000, 0.52) };
            return Task.FromResult(rows);
        }
    }
}

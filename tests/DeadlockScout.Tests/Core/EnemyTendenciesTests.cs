using DeadlockScout.Core.Builds;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Tests.Core;

/// <summary>Synthetic item rows: made-up item ids and costs, a made-up account id.</summary>
public class EnemyTendenciesTests
{
    private static readonly Dictionary<long, int> Cost = new() { [1] = 500, [2] = 3200, [3] = 6400, [4] = 3200, [5] = 9999, [6] = 6400, [7] = 3200 };
    private static int CostOf(long id) => Cost.TryGetValue(id, out var c) ? c : 0;

    private static readonly ItemTiming[] Rows =
    {
        new(1, 10, 120),   // a cheap opener in every game: sets the game count, never listed
        new(3, 9, 1500),   // 25:00, 9 of 10
        new(2, 8, 800),    // 13:20, 8 of 10
        new(4, 4, 1100),   // 4 of 10: under half, not usual
        new(5, 5, 2400),   // 40:00, exactly half
        new(6, 6, 1900),
        new(7, 7, 0),      // no buy time reported: left out
    };

    [Fact]
    public void Usual_items_are_big_ones_in_half_their_games_in_buy_order()
    {
        var usual = EnemyTendencies.Usual(Rows, CostOf);
        Assert.Equal(new long[] { 2, 3, 6, 5 }, usual.Select(u => u.ItemId));
        Assert.All(usual, u => Assert.Equal(10, u.Games));
        Assert.Equal(0.8, usual[0].Share, 6);
        Assert.Equal(TimeSpan.FromSeconds(800), usual[0].AvgBuyTime);

        Assert.Equal(new long[] { 2, 3 }, EnemyTendencies.Usual(Rows, CostOf, max: 2).Select(u => u.ItemId));
        // Two games say nothing about habits.
        Assert.Empty(EnemyTendencies.Usual(new ItemTiming[] { new(1, 2, 100), new(3, 2, 900) }, CostOf));
        Assert.Empty(EnemyTendencies.Usual(Array.Empty<ItemTiming>(), CostOf));
    }

    [Fact]
    public void Next_skips_what_they_hold_and_without_items_goes_by_the_clock()
    {
        var usual = EnemyTendencies.Usual(Rows, CostOf);
        var now = TimeSpan.FromMinutes(20);
        Assert.Equal(2, EnemyTendencies.Next(usual, Array.Empty<long>(), now).Single().ItemId);       // still owes the 13:20 one
        Assert.Equal(new long[] { 3, 6 }, EnemyTendencies.Next(usual, new long[] { 2, 99 }, now, 2).Select(u => u.ItemId));
        Assert.Empty(EnemyTendencies.Next(usual, new long[] { 2, 3, 5, 6 }, now));
        // No item data (Hosted feed): the first usual item not more than a minute overdue.
        Assert.Equal(3, EnemyTendencies.Next(usual, null, now).Single().ItemId);
        Assert.Equal(6, EnemyTendencies.Next(usual, null, TimeSpan.FromMinutes(26)).Single().ItemId);
    }

    [Fact]
    public async Task The_service_makes_one_call_per_player_and_hero_and_caches_it()
    {
        var api = new ItemsApi();
        var cache = new ScoutingServiceTests.MemoryCacheStub();
        var svc = new EnemyTendencyService(api, cache, new Monitor());
        var rows = await svc.GetAsync(77, 4, CancellationToken.None);
        await svc.GetAsync(77, 4, CancellationToken.None);
        Assert.Equal(1, api.Calls);
        Assert.Equal(Rows.Length, rows.Count);
        Assert.True(cache.Store.ContainsKey("pitems:77:4"));
        Assert.Empty(await svc.GetAsync(0, 4, CancellationToken.None)); // no account id (a bot, a private slot): no call
        Assert.Equal(1, api.Calls);
    }

    private sealed class Monitor : IOptionsMonitor<ScoutSettings>
    {
        public ScoutSettings CurrentValue { get; } = new();
        public ScoutSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ScoutSettings, string?> listener) => null;
    }

    private sealed class ItemsApi : ScoutingServiceTests.FakeApiBase, DeadlockScout.Core.Abstractions.IDeadlockApi
    {
        public int Calls;
        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ItemTiming>> PlayerItemTimingsAsync(uint accountId, int heroId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ItemTiming>>(Rows);
        }
    }
}

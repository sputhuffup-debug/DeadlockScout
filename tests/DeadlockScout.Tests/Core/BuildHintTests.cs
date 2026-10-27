using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Builds;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

public class BuildHintTests
{
    private const long A = 1080948381, B = 2948410412, C = 2414191464, D = 731943444;

    private static ItemBucketStat Item(long id, int bucket, int matches, int wins = 0) => new(id, bucket, matches, wins, 600);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1999, 0)]
    [InlineData(2000, 2000)]
    [InlineData(8772, 8000)]
    [InlineData(-50, 0)]
    public void Net_worth_maps_to_its_2000_soul_bucket(int netWorth, int bucket) => Assert.Equal(bucket, BuildHints.BucketOf(netWorth));

    [Fact]
    public void Common_buys_are_the_most_bought_items_in_your_bucket()
    {
        var items = new[] { Item(1, 8000, 50), Item(2, 8000, 900, 500), Item(3, 8000, 400), Item(4, 8000, 700), Item(5, 10000, 5000), Item(6, 6000, 9000) };
        var buys = BuildHints.CommonBuys(items, 9100);
        Assert.Equal(new long[] { 2, 4, 3 }, buys.Select(b => b.ItemId));
        Assert.Equal(500.0 / 900, buys[0].WinRate, 6);
        Assert.Equal(new long[] { 2 }, BuildHints.CommonBuys(items, 9100, count: 1).Select(b => b.ItemId));
    }

    [Fact]
    public void Common_buys_fall_back_to_the_nearest_lower_bucket_and_are_empty_below_the_table()
    {
        var items = new[] { Item(1, 4000, 10), Item(2, 8000, 20) };
        Assert.Equal(new long[] { 2 }, BuildHints.CommonBuys(items, 30000).Select(b => b.ItemId)); // past the end of the table
        Assert.Equal(new long[] { 1 }, BuildHints.CommonBuys(items, 7999).Select(b => b.ItemId));  // a gap at 6000
        Assert.Empty(BuildHints.CommonBuys(items, 3000));
        Assert.Empty(BuildHints.CommonBuys(Array.Empty<ItemBucketStat>(), 3000));
    }

    [Fact]
    public void Ability_plan_takes_the_most_played_order_with_ranks_times_and_the_first_maxed_ability()
    {
        var popular = new AbilityOrderStat(new[] { A, A, B, C, A, B, D, A, B, B }, new double[] { 13, 60, 105, 199, 254, 315, 365, 478, 600, 700 }, 5501, 3110);
        var rare = new AbilityOrderStat(new[] { B, B, B, B }, new double[] { 10, 20, 30, 40 }, 300, 200);
        var plan = BuildHints.Plan(new[] { rare, popular })!;

        Assert.Equal(5501, plan.Matches);
        Assert.Equal(new[] { 1, 2, 1, 1, 3, 2, 1, 4, 3, 4 }, plan.Steps.Select(s => s.Rank));
        Assert.Equal(A, plan.MaxFirst); // A reaches rank 4 at step 8, before B at step 10
        Assert.Equal(new AbilityStep(B, 2, TimeSpan.FromSeconds(315)), plan.NextAfter(TimeSpan.FromSeconds(260)));
        Assert.Equal(A, plan.NextAfter(TimeSpan.Zero)!.AbilityId);
        Assert.Null(plan.NextAfter(TimeSpan.FromMinutes(20))); // the order is done
    }

    [Fact]
    public void Ability_plan_handles_no_data_short_orders_and_missing_times()
    {
        Assert.Null(BuildHints.Plan(Array.Empty<AbilityOrderStat>()));
        var shortOrder = BuildHints.Plan(new[] { new AbilityOrderStat(new[] { A, B, A }, new double[] { 10 }, 5, 3) })!;
        Assert.Null(shortOrder.MaxFirst);                   // nothing reached rank 4
        Assert.Equal(TimeSpan.MaxValue, shortOrder.Steps[2].TypicalTime);
        Assert.Equal(B, shortOrder.NextAfter(TimeSpan.FromSeconds(11))!.AbilityId); // untimed steps still come after
    }

    private sealed class BuildApi : ScoutingServiceTests.FakeApiBase
    {
        public int ItemCalls, AbilityCalls;
        public bool FailItems;
        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public override Task<IReadOnlyList<ItemBucketStat>> ItemStatsByNetWorthAsync(int heroId, RankBracket bracket, CancellationToken ct)
        {
            ItemCalls++;
            if (FailItems) throw new HttpRequestException("500");
            return Task.FromResult<IReadOnlyList<ItemBucketStat>>(new[] { Item(1, 0, 10) });
        }
        public override Task<IReadOnlyList<AbilityOrderStat>> AbilityOrderStatsAsync(int heroId, RankBracket bracket, CancellationToken ct)
        {
            AbilityCalls++;
            return Task.FromResult<IReadOnlyList<AbilityOrderStat>>(new[] { new AbilityOrderStat(new[] { A }, new double[] { 10 }, 5, 3) });
        }
    }

    private sealed class Patch : IPatchKeyProvider { public ValueTask<string> GetAsync(CancellationToken ct) => ValueTask.FromResult("v1"); }

    [Fact]
    public async Task Service_caches_both_tables_and_keeps_one_when_the_other_fails()
    {
        var api = new BuildApi();
        var svc = new BuildHintService(api, new ScoutingServiceTests.MemoryCacheStub(), new Patch(), TimeSpan.FromDays(7));
        var bracket = new RankBracket(60, 80);
        var first = await svc.GetAsync(13, bracket, default);
        await svc.GetAsync(13, bracket, default);
        Assert.Single(first.Items);
        Assert.Single(first.Abilities);
        Assert.Equal((1, 1), (api.ItemCalls, api.AbilityCalls));
        Assert.Same(BuildTables.Empty, await svc.GetAsync(0, bracket, default));

        var failing = new BuildApi { FailItems = true };
        var partial = await new BuildHintService(failing, new ScoutingServiceTests.MemoryCacheStub(), new Patch(), TimeSpan.FromDays(7)).GetAsync(13, bracket, default);
        Assert.Empty(partial.Items);
        Assert.Single(partial.Abilities);
    }
}

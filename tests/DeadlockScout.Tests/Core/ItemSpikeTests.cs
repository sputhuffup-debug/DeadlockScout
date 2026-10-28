using DeadlockScout.Core.Builds;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

public class ItemSpikeTests
{
    private const long Cheap = 1, Good = 2, Meh = 3, Big = 4, Bad = 5, Rare = 6;

    private static readonly ItemAsset[] Shop =
    {
        Item(Cheap, 800), Item(Good, 3200), Item(Meh, 3200), Item(Big, 6400), Item(Bad, 3200), Item(Rare, 3200),
        new(99, "ability_x", "Some Ability", "ability", 0, 6400, null),
    };

    private static ItemAsset Item(long id, int cost) => new(id, $"upgrade_{id}", $"Item {id}", "weapon", cost / 1600 + 1, cost, null);

    // Two net-worth buckets with a 50% baseline each; Good wins 60% in both, Bad 40%, Cheap 70% (but costs 800),
    // Meh 51% (under the lift floor), Rare 90% on too few games.
    private static readonly ItemBucketStat[] Stats =
    {
        new(Good, 4000, 500, 300, 0), new(Good, 8000, 500, 300, 0),
        new(Bad, 4000, 500, 200, 0), new(Bad, 8000, 500, 200, 0),
        new(Cheap, 4000, 500, 350, 0), new(Meh, 4000, 500, 255, 0),
        new(Rare, 8000, 50, 45, 0),
        new(Big, 8000, 1000, 400, 0),
        // fillers that put each bucket's baseline at 50%
        new(100, 4000, 2000, 895, 0), new(100, 8000, 1450, 805, 0),
    };

    [Fact]
    public void Lift_compares_buyers_with_everyone_else_at_the_same_net_worth()
    {
        var lifts = ItemSpikes.Lifts(Stats);
        Assert.Equal(0.10, lifts[Good], 2);
        Assert.Equal(-0.10, lifts[Bad], 2);
        Assert.False(lifts.ContainsKey(Rare)); // under MinMatches
    }

    [Fact]
    public void Spikes_are_big_items_plus_the_best_lifts_plus_the_ultimate()
    {
        var set = ItemSpikes.Pick(Stats, Shop);
        Assert.Contains(Big, set);   // 6,400 souls, even though its buyers lose more
        Assert.Contains(Good, set);
        Assert.Contains(ItemSpikes.UltimateId, set);
        Assert.DoesNotContain(Cheap, set); // 800 souls is not a spike, whatever its lift
        Assert.DoesNotContain(Meh, set);   // lift under 2 points
        Assert.DoesNotContain(Bad, set);
        Assert.DoesNotContain(Rare, set);
        Assert.DoesNotContain(99L, set);   // abilities are not shop items
    }

    [Fact]
    public void Without_stats_only_big_items_and_the_ultimate_count()
        => Assert.Equal(new[] { ItemSpikes.UltimateId, Big }, ItemSpikes.Pick(Array.Empty<ItemBucketStat>(), Shop).OrderBy(x => x));

    [Fact]
    public void Timeline_dates_purchases_but_not_what_was_held_when_the_feed_started()
    {
        var t = new ItemTimeline();
        t.Observe(new[] { Cheap, Good }, TimeSpan.FromMinutes(10));
        t.Observe(new[] { Cheap, Good, Big }, TimeSpan.FromMinutes(12));
        t.Observe(new[] { Good, Big }, TimeSpan.FromMinutes(13)); // sold Cheap
        var owned = t.Snapshot();
        Assert.Null(owned[Good]);
        Assert.Equal(TimeSpan.FromMinutes(12), owned[Big]);
        Assert.False(owned.ContainsKey(Cheap));
    }

    [Fact]
    public void Latest_spike_flashes_for_half_a_minute_then_stays_as_a_chip()
    {
        var spikes = new HashSet<long> { Good, Big, ItemSpikes.UltimateId };
        var owned = new Dictionary<long, TimeSpan?> { [Cheap] = TimeSpan.FromMinutes(9), [Good] = null, [Big] = TimeSpan.FromSeconds(600) };
        int Cost(long id) => Shop.First(i => i.Id == id).Cost;

        var now = ItemSpikes.Latest(owned, spikes, Cost, TimeSpan.FromSeconds(610))!;
        Assert.Equal(Big, now.ItemId);
        Assert.True(now.Flash);

        var later = ItemSpikes.Latest(owned, spikes, Cost, TimeSpan.FromSeconds(630))!;
        Assert.Equal(Big, later.ItemId);
        Assert.False(later.Flash);

        // held from the start: shown, never flashed
        var start = ItemSpikes.Latest(new Dictionary<long, TimeSpan?> { [Good] = null }, spikes, Cost, TimeSpan.FromSeconds(5))!;
        Assert.Equal(Good, start.ItemId);
        Assert.False(start.Flash);

        Assert.Null(ItemSpikes.Latest(new Dictionary<long, TimeSpan?> { [Cheap] = TimeSpan.Zero }, spikes, Cost, TimeSpan.Zero));
    }

    [Fact]
    public void Same_moment_purchases_show_the_dearer_item()
    {
        var spikes = new HashSet<long> { Good, Big };
        var owned = new Dictionary<long, TimeSpan?> { [Good] = TimeSpan.FromSeconds(600), [Big] = TimeSpan.FromSeconds(600) };
        Assert.Equal(Big, ItemSpikes.Latest(owned, spikes, id => Shop.First(i => i.Id == id).Cost, TimeSpan.FromSeconds(601))!.ItemId);
    }
}

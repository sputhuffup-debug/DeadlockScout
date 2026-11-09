using DeadlockScout.Core.Builds;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

public class BuildReviewTests
{
    // Items 1-5 cost 3,000; item 9 is a 500-soul item, item 0 an ability.
    private static int Cost(long id) => id switch { 0 => 0, 9 => 500, _ => 3000 };
    private static MatchItem Bought(long id, double minute) => new(id, TimeSpan.FromMinutes(minute), null);

    private static readonly Dictionary<long, ItemTiming> Rank = new()
    {
        [1] = new ItemTiming(1, 1000, 8 * 60),     // the most bought
        [2] = new ItemTiming(2, 700, 12 * 60),     // core
        [3] = new ItemTiming(3, 500, 15 * 60),     // core
        [4] = new ItemTiming(4, 50, 20 * 60),      // rare: 5% of item 1
        [5] = new ItemTiming(5, 450, 40 * 60),     // core, but bought after this game's end
        [9] = new ItemTiming(9, 2000, 2 * 60),     // cheap: never counted, even as the most bought
    };

    [Fact]
    public void A_game_gets_the_core_items_it_skipped_or_bought_late_and_the_ones_the_rank_rarely_buys()
    {
        var stats = new List<ItemBucketStat>
        {
            new(1, 0, 1000, 500, 480), new(2, 0, 1000, 500, 720), new(3, 0, 1000, 560, 900), new(4, 0, 1000, 440, 1200),
        };
        var s = BuildReview.Review(new[] { Bought(1, 11), Bought(4, 18), Bought(9, 1), Bought(0, 3) }, TimeSpan.FromMinutes(32), Rank, stats, Cost);

        // Usual win rate 50%: item 3 wins 56% (+6 points), item 2 50%, item 4 44%.
        var skipped = s.Where(x => x.Kind == BuildAdvice.Skipped).ToList();
        Assert.Equal(new long[] { 3, 2 }, skipped.Select(x => x.ItemId));          // the bigger lift first; item 5 comes after the end
        Assert.Equal(0.06, skipped[0].Lift!.Value, 6);
        Assert.Equal(TimeSpan.FromMinutes(15), skipped[0].RankAt);
        var late = Assert.Single(s, x => x.Kind == BuildAdvice.Late);              // item 1 at 11:00, the rank at 8:00
        Assert.Equal((1L, TimeSpan.FromMinutes(11)), (late.ItemId, late.YouAt!.Value));
        var rare = Assert.Single(s, x => x.Kind == BuildAdvice.Rare);
        Assert.Equal(4, rare.ItemId);
        Assert.Equal(0.05, rare.Share, 6);
        Assert.Equal(-0.06, rare.Lift!.Value, 6);
    }

    [Fact]
    public void Without_item_stats_suggestions_have_no_lift_and_a_skipped_item_that_loses_is_left_out()
    {
        var s = BuildReview.Review(new[] { Bought(1, 8) }, TimeSpan.FromMinutes(32), Rank, null, Cost);
        Assert.Equal(new long[] { 2, 3 }, s.Where(x => x.Kind == BuildAdvice.Skipped).Select(x => x.ItemId)); // by share without lifts
        Assert.All(s, x => Assert.Null(x.Lift));

        var losing = new List<ItemBucketStat> { new(1, 0, 1000, 520, 480), new(2, 0, 1000, 420, 720), new(3, 0, 1000, 560, 900) };
        var withStats = BuildReview.Review(new[] { Bought(1, 8) }, TimeSpan.FromMinutes(32), Rank, losing, Cost);
        Assert.Equal(new long[] { 3 }, withStats.Where(x => x.Kind == BuildAdvice.Skipped).Select(x => x.ItemId)); // item 2 loses: no advice to buy it
        Assert.Empty(BuildReview.Review(new[] { Bought(1, 8) }, TimeSpan.FromMinutes(32), new Dictionary<long, ItemTiming>(), null, Cost));
    }

    [Fact]
    public void The_recurring_change_is_the_item_skipped_or_late_in_the_most_games()
    {
        BuildSuggestion Skip(long id, int min) => new(id, BuildAdvice.Skipped, TimeSpan.FromMinutes(min), null, 0.7, null);
        BuildSuggestion Rare(long id) => new(id, BuildAdvice.Rare, TimeSpan.Zero, TimeSpan.FromMinutes(5), 0.05, null);
        var games = new IReadOnlyList<BuildSuggestion>[]
        {
            new[] { Skip(3, 15), Skip(2, 12) }, new[] { Skip(3, 15) }, new[] { Skip(2, 12), Skip(3, 15) }, new[] { Rare(4), Rare(4) },
        };
        var r = BuildReview.Recurring(13, games)!;
        Assert.Equal((13, 3L, 3, 4), (r.HeroId, r.ItemId, r.Times, r.Games));
        Assert.Equal(TimeSpan.FromMinutes(15), r.RankAt);
        Assert.Null(BuildReview.Recurring(13, new IReadOnlyList<BuildSuggestion>[] { new[] { Skip(3, 15) } }));          // one game says little
        Assert.Null(BuildReview.Recurring(13, new IReadOnlyList<BuildSuggestion>[] { new[] { Rare(4) }, new[] { Rare(4) } })); // rare items are no habit to fix
    }
}

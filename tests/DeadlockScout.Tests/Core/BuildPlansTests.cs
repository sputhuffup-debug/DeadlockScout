using DeadlockScout.Core.Builds;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Tests.Core;

public class BuildPlansTests
{
    private static int Cost(long id) => id >= 100 ? 500 : 3000;   // ids from 100 up are cheap items, never in a plan
    private static MatchItem[] Bought(params long[] ids) => ids.Select((id, i) => new MatchItem(id, TimeSpan.FromMinutes(3 + i * 3), null)).ToArray();

    [Fact]
    public void A_plan_is_distinct_items_in_order_and_an_empty_one_removes_it()
    {
        var plans = BuildPlans.With(null, 13, new long[] { 1, 2, 2, 3, 0 });
        Assert.Equal(new long[] { 1, 2, 3 }, plans[13]);
        plans = BuildPlans.With(plans, 7, Enumerable.Range(1, 20).Select(i => (long)i));
        Assert.Equal(BuildPlans.MaxItems, plans[7].Count);
        Assert.Single(BuildPlans.With(plans, 13, Array.Empty<long>()));

        var s = new ScoutSettings().WithBuildPlan(13, new long[] { 1, 2 });
        Assert.Same(s, s.WithBuildPlan(13, new long[] { 1, 2, 2 }));      // the same plan: no change, no save
        Assert.Empty(s.WithBuildPlan(13, null).BuildPlans);
    }

    [Fact]
    public void A_game_is_checked_against_as_much_of_the_plan_as_it_had_room_for()
    {
        long[] plan = { 1, 2, 3, 4, 5, 6, 7, 8 };
        // Five big items bought (and a cheap one): the plan's first five are considered; 4 was skipped.
        var c = BuildPlans.Check(plan, Bought(1, 2, 3, 100, 9, 5), Cost)!;
        Assert.Equal((5, 4, 4L), (c.Considered, c.Kept, c.OffAt));           // 1, 2, 3, 5 of 1–5
        Assert.True(c.Followed);                                               // 4 of 5 is 80%
        // Bought in another order: off plan where the order broke.
        var swapped = BuildPlans.Check(plan, Bought(2, 1, 3, 4), Cost)!;
        Assert.Equal((4, 4, 1L), (swapped.Considered, swapped.Kept, swapped.OffAt));
        Assert.True(swapped.Followed);
        // A different build.
        var other = BuildPlans.Check(plan, Bought(9, 10, 11, 1), Cost)!;
        Assert.False(other.Followed);
        Assert.Null(BuildPlans.Check(plan, Bought(100, 101), Cost));          // no big item: nothing to judge
        Assert.Null(BuildPlans.Check(null, Bought(1, 2), Cost));
        Assert.Null(BuildPlans.Check(plan, Bought(1, 2, 3), Cost)!.OffAt);    // kept to it
        Assert.Equal(new long[] { 1, 2, 9 }, BuildPlans.FromGame(Bought(1, 100, 2, 9), Cost));
    }

    [Fact]
    public void The_record_splits_each_heros_games_by_whether_they_followed_the_plan()
    {
        var yes = new PlanCheck(4, 4, null);
        var no = new PlanCheck(4, 1, 2);
        var r = BuildPlans.Records(new (int, bool?, PlanCheck?)[]
        {
            (13, true, yes), (13, true, yes), (13, false, yes), (13, false, no), (13, null, yes), (7, true, null),
        });
        var haze = Assert.Single(r);
        Assert.Equal((13, 2, 3, 0, 1), (haze.HeroId, haze.FollowedWins, haze.FollowedGames, haze.OtherWins, haze.OtherGames));
    }

    [Fact]
    public void Plans_survive_a_settings_backup()
    {
        var s = new ScoutSettings().WithBuildPlan(13, new long[] { 3077079169, 1548066885 });
        var back = SettingsBackup.Read(SettingsBackup.Write(s, "0.7.0", DateTimeOffset.UnixEpoch), new ScoutSettings()).Settings!;
        Assert.Equal(new long[] { 3077079169, 1548066885 }, back.BuildPlans[13]);
    }
}

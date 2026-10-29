using DeadlockScout.Core.Builds;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

public class NextItemTests
{
    private const long Small = 1, Headhunter = 2, Leech = 3, Rare = 4;
    private static int Cost(long id) => id switch { Small => 1250, Headhunter => 3000, Leech => 6200, Rare => 6200, _ => 0 };
    private static TimeSpan T(int m, int s = 0) => new(0, m, s);
    private static MatchItem Buy(long id, int m, int s = 0) => new(id, T(m, s), null);

    private static readonly IReadOnlyList<IReadOnlyList<MatchItem>> Games = new IReadOnlyList<MatchItem>[]
    {
        new[] { Buy(Small, 3), Buy(Headhunter, 14), Buy(Leech, 23), Buy(Headhunter, 30) },   // a rebuy counts once
        new[] { Buy(Small, 4), Buy(Headhunter, 15), Buy(Leech, 25) },
        new[] { Buy(Headhunter, 13), Buy(Rare, 20) },
    };

    [Fact]
    public void The_usual_build_is_the_big_items_in_most_games_in_median_order()
    {
        var usual = NextItem.UsualBuild(Games, Cost);
        Assert.Equal(new[] { Headhunter, Leech }, usual.Select(u => u.ItemId));      // small too cheap, rare in one game
        Assert.Equal((3, T(14)), (usual[0].Games, usual[0].MedianAt));
        Assert.Equal(T(24), usual[1].MedianAt);
        Assert.Empty(NextItem.UsualBuild(Array.Empty<IReadOnlyList<MatchItem>>(), Cost));
    }

    [Fact]
    public void Next_is_the_first_usual_item_not_owned_against_the_rank_time()
    {
        var usual = NextItem.UsualBuild(Games, Cost);
        var rank = new Dictionary<long, ItemTiming> { [Headhunter] = new(Headhunter, 900, 727), [Leech] = new(Leech, 400, 1274) };
        var lead = TimeSpan.FromSeconds(30);

        var ahead = NextItem.Next(usual, new HashSet<long>(), T(10, 27), rank, lead)!;
        Assert.Equal((Headhunter, NudgeState.Ahead, T(1, 40)), (ahead.ItemId, ahead.State, ahead.In!.Value));

        Assert.Equal(NudgeState.Soon, NextItem.Next(usual, new HashSet<long>(), T(11, 50), rank, lead)!.State);
        Assert.Equal(NudgeState.Late, NextItem.Next(usual, new HashSet<long>(), T(13), rank, lead)!.State);

        var leech = NextItem.Next(usual, new HashSet<long> { Headhunter }, T(13), rank, lead)!;
        Assert.Equal((Leech, T(21, 14)), (leech.ItemId, leech.RankAt!.Value));
        Assert.Null(NextItem.Next(usual, new HashSet<long> { Headhunter, Leech }, T(26), rank, lead));

        // No items from the feed: go by your usual times instead.
        Assert.Equal(Leech, NextItem.Next(usual, null, T(16), rank, lead)!.ItemId);
        Assert.Equal(NudgeState.NoRank, NextItem.Next(usual, new HashSet<long>(), T(5), null, lead)!.State);
    }
}

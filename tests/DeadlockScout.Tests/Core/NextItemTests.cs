using DeadlockScout.Core.Builds;
using DeadlockScout.Core.Meter;
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

    // A rank curve of 600 souls a minute: 9,000 souls at 15:00.
    private static readonly PaceCurve Rank600 = new(1, RankBracket.All, "test",
        Enumerable.Range(0, 7).Select(m => new PacePoint(m * 5, null, null, null, m * 5 * 600)).ToList(), 100, "test");

    private static MeterReading Reading(int m, int souls, double spm) => new(T(m), souls, spm, spm, TimeSpan.FromMinutes(2), SampleSource.LiveFeed, 50);

    [Fact]
    public void At_your_pace_counts_down_to_the_souls_the_rank_has_when_it_buys()
    {
        // The rank buys at 15:00 with 9,000 souls; you have 8,000 at 13:00 and make 500 a minute: two minutes.
        Assert.Equal(T(2), NextItem.AtYourPace(Reading(13, 8000, 500), Rank600, T(15)));
        // Already past their souls: you have them now.
        Assert.Equal(TimeSpan.Zero, NextItem.AtYourPace(Reading(13, 9100, 500), Rank600, T(15)));
        // The window's rate is empty (just started): the match's rate stands in.
        Assert.Equal(T(4), NextItem.AtYourPace(new MeterReading(T(13), 8000, 0, 250, TimeSpan.Zero, SampleSource.LiveFeed, 3), Rank600, T(15)));
    }

    [Fact]
    public void No_countdown_without_a_rate_or_too_far_off()
    {
        Assert.Null(NextItem.AtYourPace(Reading(13, 8000, 0), Rank600, T(15)));
        Assert.Null(NextItem.AtYourPace(Reading(5, 1000, 400), Rank600, T(30)));   // 17,000 short at 400 a minute: 42 minutes
        Assert.Null(NextItem.AtYourPace(Reading(13, 8000, 500), Rank600 with { Points = Array.Empty<PacePoint>() }, T(15)));
    }
}

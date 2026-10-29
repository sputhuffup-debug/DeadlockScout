using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using Xunit;

namespace DeadlockScout.Tests.Core;

public class RankGoalTests
{
    [Fact]
    public void Choices_run_highest_first_and_stop_at_alchemist_unless_low_ranks_are_on()
    {
        var normal = RankGoal.Choices(includeLowRanks: false);
        Assert.Equal(RankGoal.Eternus, normal[0]);
        Assert.Equal(RankGoal.Alchemist, normal[^1]);
        Assert.Equal(9, normal.Count);
        Assert.Equal(new[] { "Eternus", "Ascendant", "Phantom" }, normal.Take(3).Select(RankGoal.Name));

        var all = RankGoal.Choices(includeLowRanks: true);
        Assert.Equal(11, all.Count);
        Assert.Equal("Initiate", RankGoal.Name(all[^1]));
    }

    [Theory]
    [InlineData(74, false, 8)]   // Archon 4 → Oracle
    [InlineData(116, false, 11)] // Eternus stays Eternus
    [InlineData(12, false, 3)]   // Initiate 2 → held at Alchemist
    [InlineData(12, true, 2)]    // … or Seeker with the low ranks on
    [InlineData(0, false, 3)]    // unknown → the lowest choice
    public void Next_is_the_tier_above_held_at_the_lowest_choice(int badge, bool low, int expected)
        => Assert.Equal(expected, RankGoal.Next(badge, low));

    [Fact]
    public void A_named_rank_in_settings_wins_over_next_unless_it_is_off_the_list()
    {
        Assert.Equal(10, RankGoal.Resolve(new GoalSettings { Rank = "ascendant" }, 74));
        Assert.Equal(8, RankGoal.Resolve(new GoalSettings(), 74));
        Assert.Equal(3, RankGoal.Resolve(new GoalSettings { Rank = "Seeker" }, 12)); // Seeker is hidden by default
        Assert.Equal(2, RankGoal.Resolve(new GoalSettings { Rank = "Seeker", IncludeLowRanks = true }, 12));
    }

    [Fact]
    public void Bracket_covers_every_badge_of_the_tier()
    {
        var oracle = RankGoal.Bracket(8);
        Assert.True(oracle.Contains(81) && oracle.Contains(86));
        Assert.False(oracle.Contains(76) || oracle.Contains(91));
    }

    [Fact]
    public void Line_samples_the_curve_each_minute_and_ends_at_the_match_length()
    {
        var curve = new PaceCurve(13, RankGoal.Bracket(8), "p", new[] { new PacePoint(0, null, null, null, 0), new PacePoint(10, null, null, null, 8000) }, 100, "analytics");
        var line = RankGoal.Line(curve, new TimeSpan(0, 4, 30));
        Assert.Equal(new[] { 0, 800, 1600, 2400, 3200, 3600 }, line.Select(p => p.NetWorth));
        Assert.Equal(new TimeSpan(0, 4, 30), line[^1].GameTime);
    }
}

using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Tests.Core;

public class UnspentSoulsTests
{
    private static SoulSample At(int seconds, int? unspent, SampleSource source = SampleSource.Ocr) =>
        new(TimeSpan.FromSeconds(seconds), 20_000, source, unspent);

    [Fact]
    public void Over_the_threshold_the_line_says_to_spend()
    {
        Assert.Equal("3.4k unspent: spend before you fight", UnspentSouls.Line(At(860, 3_420), 3_000));
        Assert.Equal("3.0k unspent: spend before you fight", UnspentSouls.Line(At(860, 3_000), 3_000));
        Assert.Null(UnspentSouls.Line(At(860, 2_999), 3_000));
    }

    [Fact]
    public void From_the_broadcast_it_names_the_game_time_since_it_is_late()
    {
        Assert.Equal("4.1k unspent at 14:20: spend before you fight", UnspentSouls.Line(At(860, 4_100, SampleSource.LiveFeed), 3_000));
    }

    [Fact]
    public void Off_unknown_or_without_a_sample_there_is_no_line()
    {
        Assert.Null(UnspentSouls.Line(At(860, 9_000), 0));
        Assert.Null(UnspentSouls.Line(At(860, null), 3_000)); // the broadcast before the item prices loaded
        Assert.Null(UnspentSouls.Line(null, 3_000));
        Assert.Equal(UnspentSouls.DefaultThreshold, new ScoutSettings().Coaching.UnspentSouls);
    }

    private static SoulSample Haunting(int unspent, int haunted) => new(TimeSpan.FromSeconds(860), 20_000, SampleSource.Ocr, unspent, haunted);

    [Fact]
    public void Haunted_souls_at_risk_join_the_line_or_make_their_own()
    {
        Assert.Equal("3.4k unspent: spend before you fight · 1.1k of it haunted, lost if you die", UnspentSouls.Line(Haunting(3_420, 1_100), 3_000, 1_000));
        Assert.Equal("1.2k haunted souls: lost if you die, spend or secure them", UnspentSouls.Line(Haunting(2_000, 1_200), 3_000, 1_000));
        Assert.Equal("3.4k unspent: spend before you fight", UnspentSouls.Line(Haunting(3_420, 400), 3_000, 1_000)); // too few to warn about
        Assert.Null(UnspentSouls.Line(Haunting(2_000, 1_200), 3_000, 0));            // the warning off
        Assert.Equal("1.2k haunted souls: lost if you die, spend or secure them", UnspentSouls.Line(Haunting(2_000, 1_200), 0, 1_000)); // unspent part off
        Assert.Equal(UnspentSouls.DefaultHauntedThreshold, new ScoutSettings().Coaching.HauntedSouls);
    }
}

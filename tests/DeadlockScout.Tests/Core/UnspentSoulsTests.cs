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
}

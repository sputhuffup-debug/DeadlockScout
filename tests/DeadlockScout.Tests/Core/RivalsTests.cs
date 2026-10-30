using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

public class RivalsTests
{
    // Synthetic lobby: you (1) in lane 2; Sapphire has one player in lane 2 and three elsewhere.
    private static readonly Roster Lobby = new(1, new List<RosterPlayer>
    {
        new(1, "you", Teams.Amber, 1, null, 2),
        new(2, "mate", Teams.Amber, 2, null, 2),
        new(11, "lane", Teams.Sapphire, 3, null, 2),
        new(12, "rich", Teams.Sapphire, 4, null, 1),
        new(13, "richer", Teams.Sapphire, 5, null, 3),
        new(14, "poor", Teams.Sapphire, 6, null, 4),
    }, TimeSpan.Zero);

    [Fact]
    public void Lane_opponents_come_first_then_the_richest_enemies()
    {
        var nw = new Dictionary<uint, int> { [2] = 99_000, [11] = 5_000, [12] = 9_000, [13] = 12_000, [14] = 3_000 };
        Assert.Equal(new uint[] { 11, 13 }, Rivals.Pick(Lobby, 1, nw));
        Assert.Equal(new uint[] { 11, 13, 12 }, Rivals.Pick(Lobby, 1, nw, count: 3));
        // An enemy with no broadcast souls yet is skipped; so is your own team (the 99k teammate).
        nw.Remove(11);
        Assert.Equal(new uint[] { 13, 12 }, Rivals.Pick(Lobby, 1, nw));
        // Not in the roster (spectating someone else's match): nobody.
        Assert.Empty(Rivals.Pick(Lobby, 999, nw));
    }

    [Theory]
    [InlineData(9_200, 8_000, "+1.2k")]
    [InlineData(7_700, 8_000, "−300")]
    [InlineData(8_000, 8_000.4, "±0")]
    [InlineData(31_000, 18_400, "+13k")]
    public void Gap_reads_their_souls_against_yours(int theirs, double mine, string expected)
        => Assert.Equal(expected, Rivals.Gap(theirs, mine));
}

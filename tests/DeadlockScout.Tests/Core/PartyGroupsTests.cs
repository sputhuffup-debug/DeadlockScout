using DeadlockScout.Core.Models;
using DeadlockScout.Core.Scouting;

namespace DeadlockScout.Tests.Core;

public class PartyGroupsTests
{
    // Synthetic lobby: Sapphire players listed first in the roster, to check that Amber's groups still number first.
    private static Roster Lobby() => new(1, new List<RosterPlayer>
    {
        new(201, "s1", Teams.Sapphire, 1, null, null),
        new(202, "s2", Teams.Sapphire, 2, null, null),
        new(203, "s3", Teams.Sapphire, 3, null, null),
        new(204, "s4", Teams.Sapphire, 4, null, null),
        new(101, "a1", Teams.Amber, 5, null, null),
        new(102, "a2", Teams.Amber, 6, null, null),
        new(103, "a3", Teams.Amber, 7, null, null),
        new(104, "a4", Teams.Amber, 8, null, null),
    }, TimeSpan.Zero);

    private static SharedHistory Mate(uint id, int games) => new(id, games, 0, games, false);

    [Fact]
    public void Links_are_one_sided_transitive_and_numbered_amber_first()
    {
        var mates = new Dictionary<uint, IReadOnlyList<SharedHistory>>
        {
            [203] = new[] { Mate(204, 2) },
            [202] = new[] { Mate(201, 1) },
            [201] = new[] { Mate(203, 3) },   // s1-s2-s3-s4 are one group of four through a chain
            [104] = new[] { Mate(102, 5) },   // only a4's history knows a2
        };
        var groups = PartyGroups.Detect(Lobby(), mates);
        Assert.Equal(1, groups[102]);
        Assert.Equal(1, groups[104]);
        Assert.All(new uint[] { 201, 202, 203, 204 }, id => Assert.Equal(2, groups[id]));
        Assert.False(groups.ContainsKey(101));
        Assert.False(groups.ContainsKey(103));
    }

    [Fact]
    public void Other_team_strangers_self_and_rare_mates_are_ignored()
    {
        var mates = new Dictionary<uint, IReadOnlyList<SharedHistory>>
        {
            [101] = new[] { Mate(201, 9), Mate(101, 9), Mate(999, 9), Mate(102, 1) },
        };
        Assert.Empty(PartyGroups.Detect(Lobby(), mates, minMatches: 2)); // a1-a2 has one match, the rest are not teammates
        Assert.Equal(new Dictionary<uint, int> { [101] = 1, [102] = 1 }, PartyGroups.Detect(Lobby(), mates, minMatches: 1));
        Assert.Empty(PartyGroups.Detect(Lobby(), new Dictionary<uint, IReadOnlyList<SharedHistory>>()));
    }
}

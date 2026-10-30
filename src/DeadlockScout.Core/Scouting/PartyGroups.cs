using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Scouting;

/// <summary>
/// Likely parties in a lobby. The broadcast carries no party ids, so they are inferred from each player's public party
/// history (players/mate-stats with same_party, where the API counts two players as partied when they were on the same
/// team and Steam friends at the match's start). Two players on the same team now are linked when either one's history
/// lists the other at least <c>minMatches</c> times; linked players form a group. Pure, so it is unit tested.
/// </summary>
public static class PartyGroups
{
    /// <summary>The most groups a lobby can hold: six players a team, at least two a group.</summary>
    public const int MaxGroups = 6;

    /// <summary>
    /// Account id → group number (1, 2, ...) for every player in a group of two or more. Groups are numbered in roster
    /// order (Amber's first, then Sapphire's), so the numbers stay put when the roster is scouted again. Players missing
    /// from <paramref name="partyMates"/> (a failed or private lookup) can still be linked through a teammate's history.
    /// </summary>
    public static IReadOnlyDictionary<uint, int> Detect(Roster roster, IReadOnlyDictionary<uint, IReadOnlyList<SharedHistory>> partyMates, int minMatches = 1)
    {
        var players = roster.Players
            .OrderBy(p => p.Team == Teams.Sapphire ? 1 : 0)
            .ToList();
        var index = new Dictionary<uint, int>();
        for (int i = 0; i < players.Count; i++) index.TryAdd(players[i].AccountId, i);

        var parent = Enumerable.Range(0, players.Count).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            // The earlier roster slot stays the root, so a group's number follows its first member.
            if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
        }

        int threshold = Math.Max(1, minMatches);
        foreach (var (accountId, mates) in partyMates)
        {
            if (!index.TryGetValue(accountId, out var a)) continue;
            foreach (var m in mates)
            {
                if (m.Matches < threshold || m.AccountId == accountId) continue;
                if (!index.TryGetValue(m.AccountId, out var b)) continue;
                if (players[a].Team != players[b].Team) continue;
                Union(a, b);
            }
        }

        var sizes = new Dictionary<int, int>();
        for (int i = 0; i < players.Count; i++) sizes[Find(i)] = sizes.GetValueOrDefault(Find(i)) + 1;

        var numbers = new Dictionary<int, int>();
        var result = new Dictionary<uint, int>();
        for (int i = 0; i < players.Count; i++)
        {
            int root = Find(i);
            if (sizes[root] < 2) continue;
            if (!numbers.TryGetValue(root, out var n)) numbers[root] = n = numbers.Count + 1;
            result[players[i].AccountId] = n;
        }
        return result;
    }
}

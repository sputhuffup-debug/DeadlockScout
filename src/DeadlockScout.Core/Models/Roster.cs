namespace DeadlockScout.Core.Models;

/// <summary>Team numbers as the game reports them.</summary>
public static class Teams
{
    public const int Amber = 2;
    public const int Sapphire = 3;
    public static string Name(int team) => team switch { Amber => "Amber", Sapphire => "Sapphire", _ => $"Team {team}" };
}

/// <summary>One player in the live roster.</summary>
/// <param name="Rank">Badge level (rank*10 + tier) when the feed exposes it, else null.</param>
/// <param name="Lane">Lane index when the feed exposes it (1..4, or null).</param>
public sealed record RosterPlayer(uint AccountId, string Name, int Team, int HeroId, int? Rank, int? Lane)
{
    /// <summary>Party id when the source exposes one; 0 or null means solo or unknown.</summary>
    public int? PartyId { get; init; }
}

/// <summary>The lobby as seen by the live feed at <paramref name="AsOfGameTime"/>.</summary>
public sealed record Roster(long MatchId, IReadOnlyList<RosterPlayer> Players, TimeSpan AsOfGameTime)
{
    public RosterPlayer? Find(uint accountId)
    {
        foreach (var p in Players)
            if (p.AccountId == accountId) return p;
        return null;
    }

    public IEnumerable<RosterPlayer> Team(int team) => Players.Where(p => p.Team == team);
}

/// <summary>A net-worth sample for one player from the live feed, stamped with the feed's game clock.</summary>
public sealed record NetWorthTick(TimeSpan GameTime, uint AccountId, int NetWorth, int Kills, int Deaths, int Assists);

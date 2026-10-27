namespace DeadlockScout.Core.Models;

/// <summary>Valve-reported rank from players/rank. Badge = rank*10 + subrank; 0 = unranked or in placement.</summary>
public sealed record PlayerRank(uint AccountId, int Badge, int Rank, int Subrank)
{
    public bool IsRanked => Badge > 0;
}

/// <summary>One row of your match history (players/{id}/match-history), newest first.</summary>
public sealed record MatchHistoryEntry(
    long MatchId,
    int HeroId,
    DateTimeOffset StartTime,
    TimeSpan Duration,
    int Team,
    int Kills,
    int Deaths,
    int Assists,
    int NetWorth,
    /// <summary>1 win, 2 loss, 3/4 penalized, 5 not scored, 0 invalid.</summary>
    int Outcome,
    int? RankedBadge,
    int? RankedDelta)
{
    public bool IsWin => Outcome == 1;
    public bool IsLoss => Outcome == 2;
}

/// <summary>A match from matches/active (the in-game watch tab): a free roster source when ours is listed.</summary>
public sealed record ActiveMatch(long MatchId, long? LobbyId, DateTimeOffset StartTime, TimeSpan Duration, IReadOnlyList<ActiveMatchPlayer> Players);

public sealed record ActiveMatchPlayer(uint AccountId, int Team, int HeroId);

/// <summary>Current client build from assets/steam-info; the preferred patch key.</summary>
public sealed record SteamInfo(long ClientVersion, long ServerVersion, string? VersionDate);

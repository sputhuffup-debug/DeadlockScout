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
    /// <summary>
    /// How the game scored it: 1 win, 2 loss, 3 penalized (you left), 4 penalized party, 5 not scored, 0 unset. 0 for
    /// most games before late 2026, so wins and losses come from <see cref="WinningTeam"/> instead.
    /// </summary>
    int Outcome,
    int? RankedBadge,
    int? RankedDelta,
    /// <summary>The winning team (2 Amber, 3 Sapphire), from match_result; the API's own win test compares it with your team.</summary>
    int? WinningTeam = null,
    /// <summary>When you left the match, if you did.</summary>
    TimeSpan? AbandonedAt = null)
{
    /// <summary>Your team won. Games the game did not score (outcome 5) are neither won nor lost.</summary>
    public bool IsWin => Outcome != 5 && (WinningTeam is { } w ? w == Team : Outcome == 1);
    /// <summary>Your team lost, including games you left.</summary>
    public bool IsLoss => Outcome != 5 && (WinningTeam is { } w ? w != Team : Outcome is 2 or 3 or 4);
    public bool Abandoned => AbandonedAt is not null;
}

/// <summary>A match from matches/active (the in-game watch tab): a free roster source when ours is listed.</summary>
public sealed record ActiveMatch(long MatchId, long? LobbyId, DateTimeOffset StartTime, TimeSpan Duration, IReadOnlyList<ActiveMatchPlayer> Players);

public sealed record ActiveMatchPlayer(uint AccountId, int Team, int HeroId);

/// <summary>Current client build from assets/steam-info; the preferred patch key.</summary>
public sealed record SteamInfo(long ClientVersion, long ServerVersion, string? VersionDate);

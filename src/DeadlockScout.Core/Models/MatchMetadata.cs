namespace DeadlockScout.Core.Models;

/// <summary>Trimmed match metadata: only what the benchmarks and the post-match report read.</summary>
public sealed record MatchMetadata(
    long MatchId,
    DateTimeOffset StartTime,
    TimeSpan Duration,
    int WinningTeam,
    int AverageBadgeTeamAmber,
    int AverageBadgeTeamSapphire,
    IReadOnlyList<MatchPlayer> Players);

public sealed record MatchPlayer(
    uint AccountId,
    int HeroId,
    int Team,
    int Kills,
    int Deaths,
    int Assists,
    int NetWorth,
    int? PartyId,
    int? AssignedLane,
    // Net worth sampled over the match; game time is the feed's own clock.
    IReadOnlyList<NetWorthPoint> NetWorthSeries);

public sealed record NetWorthPoint(TimeSpan GameTime, int NetWorth);

/// <summary>Replay salts; the pair the ingest endpoint wants.</summary>
public sealed record MatchSalts(long MatchId, long? ClusterId, long? MetadataSalt, long? ReplaySalt);

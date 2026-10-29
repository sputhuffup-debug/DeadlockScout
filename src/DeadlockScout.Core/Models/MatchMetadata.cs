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
    IReadOnlyList<NetWorthPoint> NetWorthSeries,
    // Only from the match history's bulk call (include_player_info, include_player_items); null elsewhere.
    int? LastHits = null,
    int? Denies = null,
    int? Level = null,
    IReadOnlyList<MatchItem>? Items = null);

/// <summary>An item a player bought, and when they sold it (null while they still held it at the end).</summary>
public sealed record MatchItem(long ItemId, TimeSpan BoughtAt, TimeSpan? SoldAt);

public sealed record NetWorthPoint(TimeSpan GameTime, int NetWorth);

/// <summary>Replay salts; the pair the ingest endpoint wants.</summary>
public sealed record MatchSalts(long MatchId, long? ClusterId, long? MetadataSalt, long? ReplaySalt);

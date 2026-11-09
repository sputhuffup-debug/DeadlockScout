using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.History;

/// <summary>
/// The match history page: a summary of your recent games, your rank over time, one card per game, and the history rows
/// the trends are drawn from (newest first, up to <see cref="MatchHistoryService.TrendGames"/>).
/// </summary>
public sealed record MatchHistoryPage(HistorySummary Summary, RankTrend Rank, IReadOnlyList<HistoryGame> Games, IReadOnlyList<MatchHistoryEntry>? History = null,
    /// <summary>Matches the app saw you play that the API has neither listed nor processed yet, newest first.</summary>
    IReadOnlyList<PlayedMatch>? Processing = null);

/// <summary>Your recent games in numbers. Averages are per game; <see cref="Kda"/> is (kills + assists) / deaths.</summary>
public sealed record HistorySummary(
    int Games,
    int Wins,
    int Losses,
    double AvgKills,
    double AvgDeaths,
    double AvgAssists,
    IReadOnlyList<HeroUsage> TopHeroes,
    /// <summary>Newest first: true won, false lost, null not scored.</summary>
    IReadOnlyList<bool?> LastResults)
{
    public double WinRate => Wins + Losses == 0 ? 0 : (double)Wins / (Wins + Losses);
    public double Kda => HistoryMath.Kda(AvgKills, AvgDeaths, AvgAssists);
}

public sealed record HeroUsage(int HeroId, int Games, int Wins)
{
    public double WinRate => Games == 0 ? 0 : (double)Wins / Games;
}

/// <summary>Your ranked badge after each game that showed one, oldest first, over the window the page covers.</summary>
public sealed record RankTrend(IReadOnlyList<RankPoint> Points)
{
    public int? Current => Points.Count > 0 ? Points[^1].Badge : null;
    public int? Peak => Points.Count > 0 ? Points.MaxBy(p => HistoryMath.BadgeIndex(p.Badge))!.Badge : null;
    /// <summary>Subranks gained (or lost) from the first point to the last: Archon 3 to Archon 5 is +2.</summary>
    public int? NetChange => Points.Count > 1 ? HistoryMath.BadgeIndex(Points[^1].Badge) - HistoryMath.BadgeIndex(Points[0].Badge) : null;
}

public sealed record RankPoint(DateTimeOffset At, int Badge);

/// <summary>One game: the history row, plus the processed match's detail once the API has it.</summary>
public sealed record HistoryGame(MatchHistoryEntry Entry, MatchDetail? Detail)
{
    public double Kda => HistoryMath.Kda(Entry.Kills, Entry.Deaths, Entry.Assists);
    public double SoulsPerMinute => Entry.Duration.TotalMinutes <= 0 ? 0 : Entry.NetWorth / Entry.Duration.TotalMinutes;
}

/// <summary>
/// A processed match from your side: your last hits, level and the items you held at the end (in buy order), where
/// your souls ranked on your team, and both teams. <see cref="Souls"/> and <see cref="Purchases"/> are the timelines:
/// your net worth through the match, and every item you bought with when (and whether) you sold it.
/// </summary>
public sealed record MatchDetail(
    long MatchId,
    int? LastHits,
    int? Denies,
    int? Level,
    IReadOnlyList<long> FinalItems,
    int TeamSoulsRank,
    int TeamSize,
    int AverageBadge,
    IReadOnlyList<HistoryPlayer> Team,
    IReadOnlyList<HistoryPlayer> Enemies,
    IReadOnlyList<NetWorthPoint>? Souls = null,
    IReadOnlyList<MatchItem>? Purchases = null,
    SoulSources? Sources = null,
    IReadOnlyList<DeathRecord>? DeathTimes = null);

/// <param name="Series">Their net worth through the match (0.6), for "net worth at any moment"; null in details cached before.</param>
public sealed record HistoryPlayer(uint AccountId, int HeroId, int Kills, int Deaths, int Assists, int NetWorth, bool IsMe,
                                   IReadOnlyList<NetWorthPoint>? Series = null, int? Lane = null);

/// <summary>
/// Your laning phase against the enemies assigned to your lane (0.7's lane graph): your souls and their average, from
/// 0:00 to the end of laning. <see cref="DiffAt"/> is yours less theirs; the lane is won when it is ahead at the end.
/// </summary>
public sealed record LaneDuel(IReadOnlyList<int> OpponentHeroes, IReadOnlyList<NetWorthPoint> Mine, IReadOnlyList<NetWorthPoint> Opponents)
{
    /// <summary>Your souls less the opponents' average at <paramref name="at"/>; null past the laning series.</summary>
    public int? DiffAt(TimeSpan at)
        => at > Mine[^1].GameTime ? null : HistoryMath.SoulsAt(Mine, at) - HistoryMath.SoulsAt(Opponents, at);

    /// <summary>Ahead at the end of laning: true won, false lost, null even or cut short.</summary>
    public bool? Won => Mine[^1].GameTime < HistoryMath.LaneEnd ? null
        : DiffAt(HistoryMath.LaneEnd) is { } d && d != 0 ? d > 0 : null;
}

/// <summary>A hero's lanes over the page: how many were won and lost, and the average difference at the end of laning.</summary>
public sealed record LaneRecord(int HeroId, int Won, int Lost, double AverageDiff)
{
    public int Games => Won + Lost;
}

/// <summary>One player's net worth at a moment of a match.</summary>
public sealed record MomentWorth(int HeroId, bool IsMe, int NetWorth);

/// <summary>
/// Every player's net worth at <paramref name="At"/>, your team and the enemy's, each richest first, with the teams'
/// totals. Players whose series is missing are left out (<see cref="Missing"/> counts them).
/// </summary>
/// <summary>Both teams' total souls at <paramref name="At"/> (0.7's team graph); <see cref="Gap"/> is yours less theirs.</summary>
public sealed record TeamWorthPoint(TimeSpan At, int Team, int Enemy)
{
    public int Gap => Team - Enemy;
}

/// <summary>The biggest change in the gap over a few minutes: <paramref name="Change"/> is yours less theirs, gained or lost.</summary>
public sealed record TeamSwing(TimeSpan From, TimeSpan To, int Change);

public sealed record MatchMoment(TimeSpan At, IReadOnlyList<MomentWorth> Team, IReadOnlyList<MomentWorth> Enemies, int Missing)
{
    public int TeamTotal => Team.Sum(p => p.NetWorth);
    public int EnemyTotal => Enemies.Sum(p => p.NetWorth);
}

using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.History;

/// <summary>
/// The match history page: a summary of your recent games, your rank over time, one card per game, and the history rows
/// the trends are drawn from (newest first, up to <see cref="MatchHistoryService.TrendGames"/>).
/// </summary>
public sealed record MatchHistoryPage(HistorySummary Summary, RankTrend Rank, IReadOnlyList<HistoryGame> Games, IReadOnlyList<MatchHistoryEntry>? History = null);

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
    IReadOnlyList<MatchItem>? Purchases = null);

public sealed record HistoryPlayer(uint AccountId, int HeroId, int Kills, int Deaths, int Assists, int NetWorth, bool IsMe);

using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.History;

/// <summary>One game on the trend charts, oldest first, with the rolling values ending at it.</summary>
public sealed record TrendPoint(MatchHistoryEntry Game, bool? Won, double SoulsPerMinute, double? RollingWinRate, double RollingSoulsPerMinute);

/// <summary>The older half of a range against the newer half. Win rates are null when a half has no scored game.</summary>
public sealed record TrendComparison(int Games, double? WinRateThen, double? WinRateNow, double SpmThen, double SpmNow, double KdaThen, double KdaNow);

/// <summary>One hero in the range: record, KDA over all its games, average souls per minute, when last played.</summary>
public sealed record HeroTrendRow(int HeroId, int Games, int Wins, int Losses, double Kda, double AvgSoulsPerMinute, DateTimeOffset LastPlayed)
{
    public double? WinRate => Wins + Losses == 0 ? null : (double)Wins / (Wins + Losses);
}

/// <summary>
/// Trends over your match history: the rows the history endpoint already returns (no match details needed), so any
/// range costs no extra calls. Inputs are newest first, as the history comes; series come out oldest first. Pure.
/// </summary>
public static class HistoryTrends
{
    /// <summary>Games in each rolling average.</summary>
    public const int RollingWindow = 10;
    /// <summary>Scored games a rolling win rate needs before it is drawn; fewer would swing between 0% and 100%.</summary>
    public const int MinForWinRate = 3;

    /// <summary>The newest <paramref name="count"/> games, on one hero when <paramref name="heroId"/> is set.</summary>
    public static IReadOnlyList<MatchHistoryEntry> Range(IEnumerable<MatchHistoryEntry> newestFirst, int count, int? heroId = null)
        => newestFirst.Where(g => heroId is null || g.HeroId == heroId).Take(count).ToList();

    public static double SoulsPerMinute(MatchHistoryEntry g) => g.Duration.TotalMinutes <= 0 ? 0 : g.NetWorth / g.Duration.TotalMinutes;

    /// <summary>Each game oldest first, with the win rate and souls per minute over it and the games before it (up to <see cref="RollingWindow"/>).</summary>
    public static IReadOnlyList<TrendPoint> Series(IReadOnlyList<MatchHistoryEntry> newestFirst)
    {
        var games = newestFirst.Reverse().ToList();
        var points = new List<TrendPoint>(games.Count);
        for (int i = 0; i < games.Count; i++)
        {
            var window = games.Skip(Math.Max(0, i + 1 - RollingWindow)).Take(Math.Min(i + 1, RollingWindow)).ToList();
            var scored = window.Where(g => g.IsWin || g.IsLoss).ToList();
            var g = games[i];
            points.Add(new TrendPoint(g, g.IsWin ? true : g.IsLoss ? false : null, SoulsPerMinute(g),
                scored.Count >= MinForWinRate ? (double)scored.Count(x => x.IsWin) / scored.Count : null,
                window.Average(SoulsPerMinute)));
        }
        return points;
    }

    /// <summary>The older half of the range against the newer half; null with fewer than four games. An odd game out goes to the newer half.</summary>
    public static TrendComparison? Compare(IReadOnlyList<MatchHistoryEntry> newestFirst)
    {
        if (newestFirst.Count < 4) return null;
        int half = newestFirst.Count / 2;
        var older = newestFirst.Skip(newestFirst.Count - half).ToList();
        var newer = newestFirst.Take(newestFirst.Count - half).ToList();
        return new TrendComparison(newestFirst.Count, WinRate(older), WinRate(newer), older.Average(SoulsPerMinute), newer.Average(SoulsPerMinute),
            Kda(older), Kda(newer));
    }

    /// <summary>Every hero in the range, most played first.</summary>
    public static IReadOnlyList<HeroTrendRow> Heroes(IReadOnlyList<MatchHistoryEntry> newestFirst)
        => newestFirst.GroupBy(g => g.HeroId)
            .Select(h => new HeroTrendRow(h.Key, h.Count(), h.Count(g => g.IsWin), h.Count(g => g.IsLoss), Kda(h.ToList()),
                h.Average(SoulsPerMinute), h.Max(g => g.StartTime)))
            .OrderByDescending(h => h.Games).ThenByDescending(h => h.LastPlayed).ToList();

    private static double? WinRate(IReadOnlyList<MatchHistoryEntry> games)
    {
        int scored = games.Count(g => g.IsWin || g.IsLoss);
        return scored == 0 ? null : (double)games.Count(g => g.IsWin) / scored;
    }

    /// <summary>(kills + assists) / deaths over all the games together, not the mean of each game's ratio.</summary>
    private static double Kda(IReadOnlyList<MatchHistoryEntry> games)
        => HistoryMath.Kda(games.Sum(g => g.Kills), games.Sum(g => g.Deaths), games.Sum(g => g.Assists));
}

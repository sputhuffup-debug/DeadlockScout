using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.History;

/// <summary>
/// The hero page's headline (0.8): your record on one hero over the history rows the page has (up to 100, no extra
/// call), your souls a minute and KDA on it, and the souls a minute of its newest five games against the rest, so a
/// hero you are improving on says so. Pure, so tested.
/// </summary>
public sealed record HeroOverview(int HeroId, int Games, int Wins, int Losses, double SoulsPerMinute, double Kda,
                                  double? RecentSpm, double? EarlierSpm, DateTimeOffset LastPlayed)
{
    /// <summary>Games in the "recent" side of the comparison.</summary>
    public const int RecentGames = 5;

    public double WinRate => Wins + Losses == 0 ? 0 : (double)Wins / (Wins + Losses);

    /// <summary>The newest games' souls a minute against the earlier ones' (0.06 = 6% more); null with too few of either.</summary>
    public double? Trend => RecentSpm is { } r && EarlierSpm is { } e && e > 0 ? r / e - 1 : null;

    /// <summary>The overview of <paramref name="heroId"/> from rows newest first; null when you have no game on it.</summary>
    public static HeroOverview? Of(int heroId, IEnumerable<MatchHistoryEntry> newestFirst)
    {
        var games = newestFirst.Where(g => g.HeroId == heroId).ToList();
        if (games.Count == 0) return null;
        double kills = games.Sum(g => (double)g.Kills), deaths = games.Sum(g => (double)g.Deaths), assists = games.Sum(g => (double)g.Assists);
        bool split = games.Count >= RecentGames * 2;
        return new HeroOverview(heroId, games.Count, games.Count(g => g.IsWin), games.Count(g => g.IsLoss),
            games.Average(HistoryTrends.SoulsPerMinute), HistoryMath.Kda(kills, deaths, assists),
            split ? games.Take(RecentGames).Average(HistoryTrends.SoulsPerMinute) : null,
            split ? games.Skip(RecentGames).Average(HistoryTrends.SoulsPerMinute) : null,
            games[0].StartTime + games[0].Duration);
    }
}

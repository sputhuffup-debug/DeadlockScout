using DeadlockScout.Core.History;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Session;

/// <summary>Which way your rank has been going over your recent ranked games.</summary>
public enum RankDirection { Rising, Steady, Falling }

/// <summary>
/// Where your last ranked games point: the badge now, the next subrank up (or down) and about how many games away at
/// the recent rate. <see cref="Games"/> is null when steady.
/// </summary>
public sealed record RankForecast(int Badge, RankDirection Trend, int? NextBadge, int? Games, int RankedGames);

/// <summary>
/// The session view's rank forecast: a straight line through your last <see cref="Window"/> ranked games' badges on one
/// ladder (<see cref="HistoryMath.BadgeIndex"/>, six subranks a tier), and how many games it takes that line to gain or
/// lose one subrank. Steady when it moves less than one subrank over the window, or would take more than
/// <see cref="MaxGames"/> games. Never a promise; a computed value, never stored. Pure, so tested.
/// </summary>
public static class RankForecasting
{
    public const int Window = 20;
    /// <summary>Ranked games needed before anything is said.</summary>
    public const int MinGames = 8;
    public const int MaxGames = 40;

    public static RankForecast? From(IReadOnlyList<MatchHistoryEntry> history)
    {
        var ranked = history.Where(h => h.RankedBadge is > 0)
            .OrderByDescending(h => h.StartTime).ThenByDescending(h => h.MatchId)
            .Take(Window).Reverse().ToList();                      // oldest first
        if (ranked.Count < MinGames) return null;
        var y = ranked.Select(h => (double)HistoryMath.BadgeIndex(h.RankedBadge!.Value)).ToList();
        int n = y.Count;
        double xMean = (n - 1) / 2.0, yMean = y.Average();
        double sxy = 0, sxx = 0;
        for (int i = 0; i < n; i++) { sxy += (i - xMean) * (y[i] - yMean); sxx += (i - xMean) * (i - xMean); }
        double slope = sxx > 0 ? sxy / sxx : 0;                    // subranks a game
        int badge = ranked[^1].RankedBadge!.Value, index = HistoryMath.BadgeIndex(badge);

        if (Math.Abs(slope) * Window < 1) return new RankForecast(badge, RankDirection.Steady, null, null, n);
        int games = (int)Math.Ceiling(1 / Math.Abs(slope));
        if (games > MaxGames) return new RankForecast(badge, RankDirection.Steady, null, null, n);
        bool up = slope > 0;
        int next = up ? index + 1 : index - 1;
        if (next < 1) return new RankForecast(badge, RankDirection.Steady, null, null, n);
        return new RankForecast(badge, up ? RankDirection.Rising : RankDirection.Falling, FromIndex(next), games, n);
    }

    /// <summary>The badge at a ladder position: the inverse of <see cref="HistoryMath.BadgeIndex"/>.</summary>
    public static int FromIndex(int index) => index <= 0 ? 0 : ((index - 1) / 6 + 1) * 10 + (index - 1) % 6 + 1;

    /// <summary>"At this rate, Archon 4 in about 9 games", with <paramref name="rank"/> naming a badge.</summary>
    public static string Text(RankForecast f, Func<int, string> rank) => f.Trend switch
    {
        RankDirection.Rising => $"Rank: at this rate, {rank(f.NextBadge!.Value)} in about {f.Games} game{(f.Games == 1 ? "" : "s")}",
        RankDirection.Falling => $"Rank: slipping, {rank(f.NextBadge!.Value)} in about {f.Games} game{(f.Games == 1 ? "" : "s")} at this rate",
        _ => $"Rank: holding steady at {rank(f.Badge)} over your last {f.RankedGames} ranked games",
    };
}

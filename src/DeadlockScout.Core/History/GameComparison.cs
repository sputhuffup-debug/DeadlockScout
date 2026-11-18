using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.History;

/// <summary>
/// Two of your games' souls side by side (0.8): the gap between them at the end of the shorter one, the biggest gap and
/// when, and where the gap opened, the <see cref="Window"/> in which it moved most in the direction it ended. Read off
/// both soul timelines minute by minute; nothing is stored.
/// </summary>
public sealed record GameComparison(TimeSpan Until, int Gap, TimeSpan WidestAt, int Widest, TimeSpan OpenedFrom, TimeSpan OpenedTo, int OpenedBy)
{
    /// <summary>The span the opening is measured over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(3);

    /// <summary>Compares <paramref name="mine"/> with <paramref name="other"/>; null when either has no timeline or they overlap under a window.</summary>
    public static GameComparison? Of(IReadOnlyList<NetWorthPoint> mine, IReadOnlyList<NetWorthPoint> other)
    {
        if (mine.Count < 2 || other.Count < 2) return null;
        mine = HistoryMath.FromZero(mine);
        other = HistoryMath.FromZero(other);
        var until = mine[^1].GameTime < other[^1].GameTime ? mine[^1].GameTime : other[^1].GameTime;
        if (until < Window) return null;

        var times = new List<TimeSpan>();
        for (var t = TimeSpan.Zero; t < until; t += TimeSpan.FromMinutes(1)) times.Add(t);
        times.Add(until);
        var gaps = times.Select(t => HistoryMath.SoulsAt(mine, t)!.Value - HistoryMath.SoulsAt(other, t)!.Value).ToList();

        int gap = gaps[^1];
        int widestIndex = 0;
        for (int i = 1; i < gaps.Count; i++)
            if (Math.Abs(gaps[i]) > Math.Abs(gaps[widestIndex])) widestIndex = i;

        // The window where the gap moved most toward its final side (ahead or behind).
        int sign = gap >= 0 ? 1 : -1, span = (int)Window.TotalMinutes, bestFrom = 0, bestBy = int.MinValue;
        for (int i = 0; i + span < gaps.Count; i++)
        {
            int by = (gaps[i + span] - gaps[i]) * sign;
            if (by > bestBy) { bestBy = by; bestFrom = i; }
        }
        if (bestBy == int.MinValue) { bestFrom = 0; bestBy = (gaps[^1] - gaps[0]) * sign; span = gaps.Count - 1; }
        return new GameComparison(until, gap, times[widestIndex], gaps[widestIndex],
            times[bestFrom], times[bestFrom + span], bestBy * sign);
    }

    /// <summary>Your best other game on <paramref name="of"/>'s hero by souls a minute, among games with a soul timeline; null when none.</summary>
    public static HistoryGame? Best(IEnumerable<HistoryGame> games, HistoryGame of)
        => games.Where(g => g.Entry.MatchId != of.Entry.MatchId && g.Entry.HeroId == of.Entry.HeroId && g.Detail?.Souls is { Count: > 1 })
                .OrderByDescending(g => g.SoulsPerMinute).FirstOrDefault();
}

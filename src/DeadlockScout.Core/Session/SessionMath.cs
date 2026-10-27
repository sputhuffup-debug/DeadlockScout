using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Session;

/// <summary>
/// Today so far: wins, losses, your current streak and how your rank moved. "Today" starts at a set local hour, so a
/// session past midnight still counts with its evening.
/// </summary>
public sealed record SessionSummary(int Wins, int Losses, int Streak, int? BadgeNow, int? BadgeAtStart, DateTimeOffset DayStart)
{
    public int Games => Wins + Losses;
    /// <summary>Badge levels gained (positive) or lost today; null without a ranked game before and after.</summary>
    public int? BadgeChange => BadgeNow is { } now && BadgeAtStart is { } start ? now - start : null;
}

/// <summary>One of your most played heroes against how that hero does in your bracket.</summary>
public sealed record HeroPoolRow(int HeroId, int Matches, int Wins, double? MetaWinRate)
{
    public double WinRate => Matches == 0 ? 0 : (double)Wins / Matches;
    /// <summary>Your win rate minus the hero's meta win rate; null without meta data.</summary>
    public double? VersusMeta => MetaWinRate is { } meta ? WinRate - meta : null;
}

/// <summary>Pure session reductions, for tests. Nothing here is stored; the view recomputes on each refresh.</summary>
public static class SessionMath
{
    /// <summary>The start of "today" for <paramref name="now"/>: the latest local <paramref name="dayStartsAtHour"/> at or before it.</summary>
    public static DateTimeOffset DayStart(DateTimeOffset now, TimeZoneInfo zone, int dayStartsAtHour)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var start = new DateTime(local.Year, local.Month, local.Day, Math.Clamp(dayStartsAtHour, 0, 23), 0, 0, DateTimeKind.Unspecified);
        if (local.DateTime < start) start = start.AddDays(-1);
        return new DateTimeOffset(start, zone.GetUtcOffset(start));
    }

    public static SessionSummary Summarize(IReadOnlyList<MatchHistoryEntry> history, DateTimeOffset now, TimeZoneInfo zone, int dayStartsAtHour)
    {
        var dayStart = DayStart(now, zone, dayStartsAtHour);
        var newestFirst = history.OrderByDescending(h => h.StartTime).ThenByDescending(h => h.MatchId).ToList();
        var today = newestFirst.Where(h => h.StartTime >= dayStart).ToList();

        // The streak runs back through history (not only today) over scored games; abandoned or unscored games are skipped.
        int streak = 0;
        foreach (var h in newestFirst.Where(h => h.IsWin || h.IsLoss))
        {
            if (streak == 0) streak = h.IsWin ? 1 : -1;
            else if (h.IsWin == streak > 0) streak += streak > 0 ? 1 : -1;
            else break;
        }

        int? badgeNow = newestFirst.FirstOrDefault(h => h.RankedBadge is > 0)?.RankedBadge;
        int? atStart = newestFirst.FirstOrDefault(h => h.StartTime < dayStart && h.RankedBadge is > 0)?.RankedBadge;
        return new SessionSummary(today.Count(h => h.IsWin), today.Count(h => h.IsLoss), streak, badgeNow, atStart, dayStart);
    }

    /// <summary>A gentle nudge after <paramref name="threshold"/> losses in a row; null otherwise or when turned off (0).</summary>
    public static string? Nudge(SessionSummary s, int threshold)
        => threshold > 0 && s.Streak <= -threshold ? $"{-s.Streak} losses in a row. A short break usually helps more than the next queue." : null;

    /// <summary>Your most played heroes (all-time totals), each against its meta win rate in your bracket.</summary>
    public static IReadOnlyList<HeroPoolRow> HeroPool(IEnumerable<PlayerHeroStats> mine, IEnumerable<AnalyticsHeroStats> meta, int count)
    {
        var metaByHero = meta.Where(m => m.Matches > 0).ToDictionary(m => m.HeroId, m => m.WinRate);
        return mine.Where(m => m.Matches > 0 && m.HeroId > 0)
            .OrderByDescending(m => m.Matches).ThenBy(m => m.HeroId)
            .Take(Math.Max(0, count))
            .Select(m => new HeroPoolRow(m.HeroId, m.Matches, m.Wins, metaByHero.TryGetValue(m.HeroId, out var wr) ? wr : null))
            .ToList();
    }
}

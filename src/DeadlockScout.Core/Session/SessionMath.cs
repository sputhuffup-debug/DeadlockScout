using DeadlockScout.Core.Benchmarks;
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
/// <param name="Pace">Your souls against the rank gate's on this hero over your recent games; null without them or the gate.</param>
public sealed record HeroPoolRow(int HeroId, int Matches, int Wins, double? MetaWinRate, HeroPace? Pace = null)
{
    public double WinRate => Matches == 0 ? 0 : (double)Wins / Matches;
    /// <summary>Your win rate minus the hero's meta win rate; null without meta data.</summary>
    public double? VersusMeta => MetaWinRate is { } meta ? WinRate - meta : null;
}

/// <summary>
/// Your recent games on a hero against the rank gate: each game's final net worth over the gate curve's at that game's
/// length, as a fraction (+0.05 = 5% more souls than the gate's players), averaged. <paramref name="Trend"/> is the newer
/// half's average less the older half's, null under <see cref="SessionMath.MinTrendGames"/> games.
/// </summary>
public sealed record HeroPace(int Games, double VersusGate, double? Trend, string GateRank);

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

    /// <summary>Games on a hero the pace looks back over, newest first.</summary>
    public const int PaceGames = 10;
    /// <summary>Games a hero needs before its trend is said (half of them against the other half).</summary>
    public const int MinTrendGames = 4;
    /// <summary>A trend smaller than this either way is "steady".</summary>
    public const double SteadyTrend = 0.03;
    /// <summary>Games shorter than this are left out: remakes and early abandons say nothing about pace.</summary>
    public static readonly TimeSpan MinPaceDuration = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The pool's rows with <see cref="HeroPoolRow.Pace"/> filled from your match history: your last
    /// <see cref="PaceGames"/> games of ten minutes or more on each hero, against <paramref name="gate"/>'s curve for that
    /// hero (null when there is none). Computed at render time, never stored.
    /// </summary>
    public static IReadOnlyList<HeroPoolRow> WithPace(IReadOnlyList<HeroPoolRow> pool, IReadOnlyList<MatchHistoryEntry> history,
                                                      Func<int, PaceCurve?> gate, string gateRank)
        => pool.Select(row => row with { Pace = Pace(row.HeroId, history, gate(row.HeroId), gateRank) }).ToList();

    public static HeroPace? Pace(int heroId, IReadOnlyList<MatchHistoryEntry> history, PaceCurve? gate, string gateRank)
    {
        if (gate is null || gate.Points.Count < 2) return null;
        var ratios = history
            .Where(h => h.HeroId == heroId && h.Duration >= MinPaceDuration && h.NetWorth > 0)
            .OrderByDescending(h => h.StartTime).ThenByDescending(h => h.MatchId)
            .Take(PaceGames)
            .Select(h => (Gate: PaceMath.NetWorthAt(gate, h.Duration), h.NetWorth))
            .Where(x => x.Gate > 0)
            .Select(x => x.NetWorth / x.Gate - 1)
            .ToList();
        if (ratios.Count == 0) return null;
        double? trend = null;
        if (ratios.Count >= MinTrendGames)
        {
            int half = ratios.Count / 2;
            trend = ratios.Take(half).Average() - ratios.Skip(ratios.Count - half).Average();
        }
        return new HeroPace(ratios.Count, ratios.Average(), trend, gateRank);
    }

    /// <summary>"pace −6% vs Oracle, rising": the hero pool's pace part, or null without one.</summary>
    public static string? PaceText(HeroPace? pace)
    {
        if (pace is null) return null;
        string pct = $"{(pace.VersusGate >= 0 ? "+" : "−")}{Math.Abs(pace.VersusGate * 100):0}%";
        string trend = pace.Trend switch
        {
            null => "",
            > SteadyTrend => ", rising",
            < -SteadyTrend => ", falling",
            _ => ", steady",
        };
        return $"pace {pct} vs {pace.GateRank}{trend}";
    }

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

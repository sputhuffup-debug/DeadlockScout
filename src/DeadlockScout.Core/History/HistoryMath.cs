using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.History;

/// <summary>The match history page's numbers, derived from the rows and metadata at render time. Pure.</summary>
public static class HistoryMath
{
    /// <summary>(kills + assists) / deaths, with no deaths counted as one.</summary>
    public static double Kda(double kills, double deaths, double assists) => (kills + assists) / Math.Max(1, deaths);

    /// <summary>A badge (tier*10 + subtier) as a position on one ladder, so subranks can be counted across tiers.</summary>
    public static int BadgeIndex(int badge) => badge <= 0 ? 0 : (badge / 10 - 1) * 6 + badge % 10;

    public static HistorySummary Summarize(IReadOnlyList<MatchHistoryEntry> games, int topHeroes = 3, int lastResults = 15)
    {
        int n = games.Count;
        return new HistorySummary(
            n,
            games.Count(g => g.IsWin),
            games.Count(g => g.IsLoss),
            n == 0 ? 0 : games.Average(g => (double)g.Kills),
            n == 0 ? 0 : games.Average(g => (double)g.Deaths),
            n == 0 ? 0 : games.Average(g => (double)g.Assists),
            games.GroupBy(g => g.HeroId)
                .Select(h => new HeroUsage(h.Key, h.Count(), h.Count(g => g.IsWin)))
                .OrderByDescending(h => h.Games).ThenByDescending(h => h.Wins).Take(topHeroes).ToList(),
            games.Take(lastResults).Select(g => g.IsWin ? true : g.IsLoss ? false : (bool?)null).ToList());
    }

    /// <summary>Your badge after each game that reported one, oldest first, for games since <paramref name="since"/>.</summary>
    public static RankTrend Rank(IEnumerable<MatchHistoryEntry> history, DateTimeOffset since)
        => new(history.Where(g => g.StartTime >= since && g.RankedBadge is > 0)
            .OrderBy(g => g.StartTime)
            .Select(g => new RankPoint(g.StartTime + g.Duration, g.RankedBadge!.Value)).ToList());

    /// <summary>
    /// Your history row for a match the API's history does not list yet, from its metadata: the result comes from the
    /// winning team (no outcome code, rank change or leave time in the metadata). Null when you are not in it.
    /// </summary>
    public static MatchHistoryEntry? Entry(MatchMetadata m, uint me)
        => m.Players.FirstOrDefault(p => p.AccountId == me) is { } p
            ? new MatchHistoryEntry(m.MatchId, p.HeroId, m.StartTime, m.Duration, p.Team, p.Kills, p.Deaths, p.Assists, p.NetWorth, 0, null, null,
                m.WinningTeam is 2 or 3 ? m.WinningTeam : null)
            : null;

    /// <summary>The match from your side, or null when you are not in it.</summary>
    public static MatchDetail? Detail(MatchMetadata m, uint me)
    {
        var mine = m.Players.FirstOrDefault(p => p.AccountId == me);
        if (mine is null) return null;
        HistoryPlayer Brief(MatchPlayer p) => new(p.AccountId, p.HeroId, p.Kills, p.Deaths, p.Assists, p.NetWorth, p.AccountId == me,
            p.NetWorthSeries is { Count: > 0 } s ? s : null);
        var team = m.Players.Where(p => p.Team == mine.Team).OrderByDescending(p => p.NetWorth).ToList();
        var enemies = m.Players.Where(p => p.Team != mine.Team).OrderByDescending(p => p.NetWorth).ToList();
        var held = (mine.Items ?? Array.Empty<MatchItem>()).Where(i => i.SoldAt is null).OrderBy(i => i.BoughtAt).Select(i => i.ItemId)
            .Distinct().ToList();
        return new MatchDetail(m.MatchId, mine.LastHits, mine.Denies, mine.Level, held,
            team.FindIndex(p => p.AccountId == me) + 1, team.Count,
            (m.AverageBadgeTeamAmber + m.AverageBadgeTeamSapphire) / 2,
            team.Select(Brief).ToList(), enemies.Select(Brief).ToList(),
            mine.NetWorthSeries,
            (mine.Items ?? Array.Empty<MatchItem>()).OrderBy(i => i.BoughtAt).ToList(),
            mine.Sources,
            mine.DeathTimes);
    }

    /// <summary>
    /// Every player's net worth at <paramref name="t"/> in a game, from their series in its details (yours from your own
    /// timeline when the player entry has none). A computed value, never stored.
    /// </summary>
    public static MatchMoment Moment(MatchDetail d, TimeSpan t)
    {
        int missing = 0;
        List<MomentWorth> Read(IEnumerable<HistoryPlayer> players)
        {
            var list = new List<MomentWorth>();
            foreach (var p in players)
            {
                var series = p.Series ?? (p.IsMe ? d.Souls : null);
                if (series is not { Count: > 0 }) { missing++; continue; }
                list.Add(new MomentWorth(p.HeroId, p.IsMe, SoulsAt(FromZero(series), t) ?? 0));
            }
            return list.OrderByDescending(m => m.NetWorth).ToList();
        }
        return new MatchMoment(t, Read(d.Team), Read(d.Enemies), missing);
    }

    /// <summary>How often the team graph reads the teams' totals.</summary>
    public static readonly TimeSpan TeamStep = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Both teams' total souls every <see cref="TeamStep"/> from 0:00 to <paramref name="duration"/> (0.7's team graph).
    /// Empty when any player has no series (a game cached before 0.6, or a player the record lacks): a total that leaves a
    /// player out would draw a gap that never was.
    /// </summary>
    public static IReadOnlyList<TeamWorthPoint> TeamWorth(MatchDetail d, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || d.Team.Count == 0 || d.Enemies.Count == 0) return Array.Empty<TeamWorthPoint>();
        var points = new List<TeamWorthPoint>();
        for (var t = TimeSpan.Zero; ; t += TeamStep)
        {
            if (t > duration) t = duration;
            var m = Moment(d, t);
            if (m.Missing > 0) return Array.Empty<TeamWorthPoint>();
            points.Add(new TeamWorthPoint(t, m.TeamTotal, m.EnemyTotal));
            if (t == duration) break;
        }
        return points;
    }

    /// <summary>
    /// The biggest change in the gap within <paramref name="window"/> (the steepest stretch of the game, either way), or
    /// null when the gap never moved by <paramref name="minChange"/> souls in that time.
    /// </summary>
    public static TeamSwing? BiggestSwing(IReadOnlyList<TeamWorthPoint> points, TimeSpan window, int minChange = 1000)
    {
        TeamSwing? best = null;
        for (int i = 0; i < points.Count; i++)
        {
            for (int j = i + 1; j < points.Count && points[j].At - points[i].At <= window; j++)
            {
                int change = points[j].Gap - points[i].Gap;
                if (best is null || Math.Abs(change) > Math.Abs(best.Change)) best = new TeamSwing(points[i].At, points[j].At, change);
            }
        }
        return best is { } b && Math.Abs(b.Change) >= minChange ? b : null;
    }

    /// <summary>
    /// <paramref name="series"/> starting at 0 souls at 0:00. The match metadata's first net-worth sample comes a minute or
    /// more into the match, so without this a soul timeline starts mid-air at its first sample.
    /// </summary>
    public static IReadOnlyList<NetWorthPoint> FromZero(IReadOnlyList<NetWorthPoint> series)
        => series.Count == 0 || series[0].GameTime <= TimeSpan.Zero
            ? series
            : series.Prepend(new NetWorthPoint(TimeSpan.Zero, 0)).ToList();

    /// <summary>Your souls at <paramref name="at"/>, read off the series between its samples; null without a series.</summary>
    public static int? SoulsAt(IReadOnlyList<NetWorthPoint> souls, TimeSpan at)
    {
        if (souls.Count == 0) return null;
        if (at <= souls[0].GameTime) return souls[0].NetWorth;
        for (int i = 1; i < souls.Count; i++)
        {
            if (souls[i].GameTime < at) continue;
            var a = souls[i - 1];
            var b = souls[i];
            double f = (at - a.GameTime) / (b.GameTime - a.GameTime);
            return (int)Math.Round(a.NetWorth + f * (b.NetWorth - a.NetWorth));
        }
        return souls[^1].NetWorth;
    }
}

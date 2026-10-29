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

    /// <summary>The match from your side, or null when you are not in it.</summary>
    public static MatchDetail? Detail(MatchMetadata m, uint me)
    {
        var mine = m.Players.FirstOrDefault(p => p.AccountId == me);
        if (mine is null) return null;
        HistoryPlayer Brief(MatchPlayer p) => new(p.AccountId, p.HeroId, p.Kills, p.Deaths, p.Assists, p.NetWorth, p.AccountId == me);
        var team = m.Players.Where(p => p.Team == mine.Team).OrderByDescending(p => p.NetWorth).ToList();
        var enemies = m.Players.Where(p => p.Team != mine.Team).OrderByDescending(p => p.NetWorth).ToList();
        var held = (mine.Items ?? Array.Empty<MatchItem>()).Where(i => i.SoldAt is null).OrderBy(i => i.BoughtAt).Select(i => i.ItemId)
            .Distinct().ToList();
        return new MatchDetail(m.MatchId, mine.LastHits, mine.Denies, mine.Level, held,
            team.FindIndex(p => p.AccountId == me) + 1, team.Count,
            (m.AverageBadgeTeamAmber + m.AverageBadgeTeamSapphire) / 2,
            team.Select(Brief).ToList(), enemies.Select(Brief).ToList(),
            mine.NetWorthSeries,
            (mine.Items ?? Array.Empty<MatchItem>()).OrderBy(i => i.BoughtAt).ToList());
    }

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

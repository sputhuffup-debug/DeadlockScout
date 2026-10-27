using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.PostMatch;

/// <summary>Your net worth at one minute against the benchmark curve.</summary>
public sealed record PaceCheckpoint(int Minute, int NetWorth, double? Expected, double? Delta, double? Percentile);

/// <summary>Your net worth against the average of your lane opponents at <see cref="Minute"/>.</summary>
public sealed record LaneResult(int Minute, int MyNetWorth, int OpponentNetWorth, IReadOnlyList<int> OpponentHeroIds)
{
    public int Difference => MyNetWorth - OpponentNetWorth;
}

/// <summary>
/// One finished match from your side. Computed from the match's metadata and a benchmark curve whenever it is shown,
/// never stored (only the metadata is cached).
/// </summary>
public sealed record PostMatchReport(
    long MatchId,
    int HeroId,
    bool Won,
    DateTimeOffset StartTime,
    TimeSpan Duration,
    int Kills,
    int Deaths,
    int Assists,
    int NetWorth,
    int TeamNetWorthRank,
    int TeamSize,
    IReadOnlyList<PaceCheckpoint> Pace,
    LaneResult? Lane,
    RankBracket Bracket,
    string? BenchmarkProvider,
    int BenchmarkSampleSize)
{
    public double SoulsPerMinute => Duration.TotalMinutes <= 0 ? 0 : NetWorth / Duration.TotalMinutes;
}

/// <summary>Pure report building, for tests.</summary>
public static class PostMatchReporter
{
    public static readonly IReadOnlyList<int> CheckpointMinutes = new[] { 10, 20, 30 };
    public const int LaneMinute = 10;

    /// <summary>The report for <paramref name="me"/>, or null when you are not in the match.</summary>
    public static PostMatchReport? Build(MatchMetadata meta, uint me, PaceCurve? curve, RankBracket bracket)
    {
        var mine = meta.Players.FirstOrDefault(p => p.AccountId == me);
        if (mine is null) return null;

        var team = meta.Players.Where(p => p.Team == mine.Team).OrderByDescending(p => p.NetWorth).ToList();
        int rank = team.FindIndex(p => p.AccountId == me) + 1;

        var pace = new List<PaceCheckpoint>();
        foreach (int minute in CheckpointMinutes)
        {
            var t = TimeSpan.FromMinutes(minute);
            if (t > meta.Duration || NetWorthAt(mine.NetWorthSeries, t) is not { } nw) continue;
            double? expected = curve is null ? null : PaceMath.NetWorthAt(curve, t);
            double? delta = expected is > 0 ? nw / expected.Value - 1 : null;
            double? pct = curve is null ? null : PaceMath.Percentile(curve, t, nw);
            pace.Add(new PaceCheckpoint(minute, nw, expected, delta, pct));
        }

        return new PostMatchReport(meta.MatchId, mine.HeroId, meta.WinningTeam == mine.Team, meta.StartTime, meta.Duration,
            mine.Kills, mine.Deaths, mine.Assists, mine.NetWorth, rank, team.Count, pace, Lane(meta, mine), bracket,
            curve?.Provider, curve?.SampleSize ?? 0);
    }

    /// <summary>The enemies assigned to your lane, at <see cref="LaneMinute"/>; null without lane data or a series that long.</summary>
    public static LaneResult? Lane(MatchMetadata meta, MatchPlayer mine)
    {
        if (mine.AssignedLane is not { } lane) return null;
        var t = TimeSpan.FromMinutes(LaneMinute);
        if (t > meta.Duration || NetWorthAt(mine.NetWorthSeries, t) is not { } myNw) return null;
        var opponents = meta.Players.Where(p => p.Team != mine.Team && p.AssignedLane == lane)
            .Select(p => (p.HeroId, Nw: NetWorthAt(p.NetWorthSeries, t))).Where(o => o.Nw is not null).ToList();
        if (opponents.Count == 0) return null;
        return new LaneResult(LaneMinute, myNw, (int)Math.Round(opponents.Average(o => o.Nw!.Value)), opponents.Select(o => o.HeroId).ToList());
    }

    /// <summary>Net worth at <paramref name="t"/>, linear between samples; null past the last sample or without samples.</summary>
    public static int? NetWorthAt(IReadOnlyList<NetWorthPoint> series, TimeSpan t)
    {
        if (series.Count == 0 || t > series[^1].GameTime) return null;
        if (t <= series[0].GameTime) return series[0].NetWorth;
        for (int i = 1; i < series.Count; i++)
        {
            var (a, b) = (series[i - 1], series[i]);
            if (t > b.GameTime) continue;
            double span = (b.GameTime - a.GameTime).TotalSeconds;
            double f = span <= 0 ? 1 : (t - a.GameTime).TotalSeconds / span;
            return (int)Math.Round(a.NetWorth + (b.NetWorth - a.NetWorth) * f);
        }
        return series[^1].NetWorth;
    }

    /// <summary>The match's average badge (both teams), for picking the benchmark bracket the way the live meter does.</summary>
    public static int? AverageBadge(MatchMetadata meta)
    {
        var badges = new[] { meta.AverageBadgeTeamAmber, meta.AverageBadgeTeamSapphire }.Where(b => b > 0).ToList();
        return badges.Count == 0 ? null : (int)Math.Round(badges.Average());
    }
}

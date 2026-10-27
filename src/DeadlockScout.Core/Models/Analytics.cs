namespace DeadlockScout.Core.Models;

/// <summary>Filters for analytics/hero-stats. Null means "no filter".</summary>
public sealed record AnalyticsQuery(
    IReadOnlyList<int>? HeroIds = null,
    int? MinDurationSeconds = null,
    int? MaxDurationSeconds = null,
    int? MinAverageBadge = null,
    int? MaxAverageBadge = null,
    long? MinUnixTimestamp = null,
    long? MaxUnixTimestamp = null);

/// <summary>One row of analytics/hero-stats. Totals are summed over <see cref="Matches"/>.</summary>
public sealed record AnalyticsHeroStats(
    int HeroId,
    int Matches,
    int Wins,
    long TotalNetWorth,
    long TotalKills,
    long TotalDeaths,
    long TotalAssists)
{
    public double MeanNetWorth => Matches == 0 ? 0 : (double)TotalNetWorth / Matches;
    public double WinRate => Matches == 0 ? 0 : (double)Wins / Matches;
}

/// <summary>Item pick stats for a hero and bracket.</summary>
public sealed record ItemStat(int ItemId, int Matches, int Wins);

/// <summary>Hero-pair win rate (the first hero against the second).</summary>
public sealed record HeroMatchup(int HeroId, int EnemyHeroId, int Matches, int Wins)
{
    public double WinRate => Matches == 0 ? 0 : (double)Wins / Matches;
}


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

/// <summary>
/// Purchases of one item by the hero's players whose net worth at the moment of purchase fell in
/// [<see cref="Bucket"/>, Bucket + 2,000). Wins count the matches those buyers won. Item ids are 32-bit unsigned, so long.
/// </summary>
public sealed record ItemBucketStat(long ItemId, int Bucket, int Matches, int Wins, double AvgBuyTimeSeconds)
{
    public double WinRate => Matches == 0 ? 0 : (double)Wins / Matches;
}

/// <summary>
/// When players of a hero in a badge range buy an item, on average (analytics/item-stats without a bucket): the gate an
/// item timing is compared with.
/// </summary>
public sealed record ItemTiming(long ItemId, int Matches, double AvgBuyTimeSeconds)
{
    public TimeSpan AvgBuyTime => TimeSpan.FromSeconds(AvgBuyTimeSeconds);
}

/// <summary>
/// One complete ability upgrade order (one entry per ability point spent, unlocks included) with the average game time of
/// each step, and how the players who used it did.
/// </summary>
public sealed record AbilityOrderStat(IReadOnlyList<long> Abilities, IReadOnlyList<double> AvgUpgradeTimesSeconds, int Matches, int Wins)
{
    public double WinRate => Matches == 0 ? 0 : (double)Wins / Matches;
}

/// <summary>Hero-pair record: <see cref="HeroId"/>'s wins against <see cref="EnemyHeroId"/> in a shared lane.</summary>
public sealed record HeroMatchup(int HeroId, int EnemyHeroId, int Matches, int Wins)
{
    public double WinRate => Matches == 0 ? 0 : (double)Wins / Matches;
}

/// <summary>How often a hero's players won the matches they bought an item in (analytics/item-stats, no bucket).</summary>
public sealed record ItemWinStat(long ItemId, int Matches, int Wins)
{
    public double WinRate => Matches == 0 ? 0 : (double)Wins / Matches;
}

/// <summary>
/// An item that wins more for your hero when <paramref name="EnemyHeroId"/> is on the other team than it does otherwise,
/// beyond the matchup's own shift. <paramref name="Lift"/> is in win-rate points (0.013 = 1.3 points), <paramref name="Z"/>
/// how many standard errors that is, <paramref name="Matches"/> the games behind it.
/// </summary>
public sealed record CounterItem(long ItemId, int EnemyHeroId, double Lift, double Z, int Matches);


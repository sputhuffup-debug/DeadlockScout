using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Abstractions;

/// <summary>The only type that talks HTTP. Every method is rate limited and retried by the implementation; callers cache.</summary>
public interface IDeadlockApi
{
    // Players
    /// <summary>Per-hero totals for up to 1000 accounts in one call. Protected accounts are simply absent.</summary>
    Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct);
    Task<IReadOnlyList<PlayerRank>> RanksAsync(IEnumerable<uint> accountIds, CancellationToken ct);
    Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct);
    Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct);
    Task<IReadOnlyList<MatchHistoryEntry>> MatchHistoryAsync(uint accountId, CancellationToken ct);

    // Matches
    Task<string> LiveUrlAsync(long matchId, CancellationToken ct);
    Task<ActiveMatch?> ActiveMatchAsync(long matchId, CancellationToken ct);
    Task<MatchMetadata?> MatchMetadataAsync(long matchId, CancellationToken ct);
    /// <summary>Recent matches of a hero in a badge range with per-player net-worth series, via the bulk metadata endpoint.</summary>
    Task<IReadOnlyList<MatchMetadata>> SampleMatchesAsync(int heroId, RankBracket bracket, int limit, CancellationToken ct);
    Task SubmitSaltsAsync(IEnumerable<MatchSalts> salts, CancellationToken ct);

    // Analytics
    Task<IReadOnlyList<AnalyticsHeroStats>> AnalyticsHeroStatsAsync(AnalyticsQuery q, CancellationToken ct);
    Task<IReadOnlyList<ItemStat>> ItemStatsAsync(int heroId, RankBracket bracket, int? minNetWorth, int? maxNetWorth, CancellationToken ct);
    /// <summary>
    /// Every hero's record against every other hero they shared a lane with, in matches whose average badge is in the
    /// bracket, over the last 30 days (analytics/hero-counter-stats; pairs below 20 matches are left out). One call
    /// covers every lane card in the bracket, so callers cache the whole table.
    /// </summary>
    Task<IReadOnlyList<HeroMatchup>> HeroCounterStatsAsync(RankBracket bracket, CancellationToken ct);

    // Assets
    Task<IReadOnlyList<HeroAsset>> HeroesAsync(CancellationToken ct);
    Task<IReadOnlyList<ItemAsset>> ItemsAsync(CancellationToken ct);
    Task<SteamInfo?> SteamInfoAsync(CancellationToken ct);
}

using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Abstractions;

/// <summary>The only type that talks HTTP. Every method is rate limited and cached by the implementation.</summary>
public interface IDeadlockApi
{
    Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct);
    Task<string> LiveUrlAsync(long matchId, CancellationToken ct);
    Task<MatchMetadata?> MatchMetadataAsync(long matchId, CancellationToken ct);
    Task<IReadOnlyList<AnalyticsHeroStats>> AnalyticsHeroStatsAsync(AnalyticsQuery q, CancellationToken ct);

    Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct);
    Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct);
    Task<IReadOnlyList<MmrPoint>> MmrHistoryAsync(uint accountId, CancellationToken ct);
    Task<IReadOnlyList<long>> MatchHistoryIdsAsync(uint accountId, int limit, CancellationToken ct);
    Task<IReadOnlyList<long>> RecentMatchIdsAsync(int heroId, RankBracket bracket, int limit, CancellationToken ct);

    Task<IReadOnlyList<ItemStat>> ItemStatsAsync(int heroId, RankBracket bracket, int? minNetWorth, int? maxNetWorth, CancellationToken ct);
    Task<IReadOnlyList<HeroMatchup>> HeroMatchupsAsync(int heroId, RankBracket bracket, CancellationToken ct);

    Task SubmitSaltsAsync(MatchSalts salts, CancellationToken ct);

    Task<IReadOnlyList<HeroAsset>> HeroesAsync(CancellationToken ct);
    Task<IReadOnlyList<ItemAsset>> ItemsAsync(CancellationToken ct);
}

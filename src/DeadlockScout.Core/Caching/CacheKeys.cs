using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Caching;

/// <summary>Every cache key in one place so TTL policy and invalidation are visible together.</summary>
public static class CacheKeys
{
    public const string HeroAssets = "assets:v2:heroes";
    public const string ItemAssets = "assets:v2:items";

    public static string PlayerHeroStats(uint accountId) => $"phs:{accountId}";
    public static string EnemyStats(uint me) => $"enemy:{me}";
    public static string MateStats(uint me) => $"mate:{me}";
    public static string LiveUrl(long matchId) => $"live:{matchId}";
    public static string MatchMetadata(long matchId) => $"meta:{matchId}";
    public static string AnalyticsCurve(int hero, RankBracket b, string patch) => $"pace:a:{hero}:{b}:{patch}";
    public static string MetadataCurve(int hero, RankBracket b, string patch) => $"pace:m:{hero}:{b}:{patch}";
    public static string PersonalCurve(int hero, uint me) => $"pace:p:{hero}:{me}";
    public static string ItemStats(int hero, RankBracket b, string patch) => $"items:{hero}:{b}:{patch}";
    public static string AbilityOrder(int hero, RankBracket b, string patch) => $"abil:{hero}:{b}:{patch}";
    public static string Matchup(int hero, int enemy, RankBracket b, string patch) => $"comb:{hero}:{enemy}:{b}:{patch}";
    public static string Mmr(uint me) => $"mmr:{me}";
    public static string MatchHistory(uint me) => $"mh:{me}";
    public static string SampleMatches(int hero, RankBracket b, string patch) => $"sample:{hero}:{b}:{patch}";
}

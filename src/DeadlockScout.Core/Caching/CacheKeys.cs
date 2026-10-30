using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Caching;

/// <summary>Every cache key in one place so TTL policy and invalidation are visible together.</summary>
public static class CacheKeys
{
    public const string HeroAssets = "assets:v2:heroes";
    public const string ItemAssets = "assets:v2:items";
    /// <summary>An item icon's bytes, by its URL (the URL changes when the art does).</summary>
    public static string Image(string url) => $"img:{url}";

    public static string PlayerHeroStats(uint accountId) => $"phs:{accountId}";
    public static string EnemyStats(uint me) => $"enemy:{me}";
    public static string MateStats(uint me) => $"mate:{me}";
    /// <summary>Who an account has partied with (mate-stats, same_party), for the lobby's party tags.</summary>
    public static string PartyMates(uint accountId) => $"party:{accountId}";
    public static string LiveUrl(long matchId) => $"live:{matchId}";
    public static string MatchMetadata(long matchId) => $"meta:{matchId}";
    public static string AnalyticsCurve(int hero, RankBracket b, string patch) => $"pace:a:{hero}:{b}:{patch}";
    public static string MetadataCurve(int hero, RankBracket b, string patch) => $"pace:m:{hero}:{b}:{patch}";
    public static string PersonalCurve(int hero, uint me) => $"pace:p:{hero}:{me}";
    public static string ItemStats(int hero, RankBracket b, string patch) => $"items:{hero}:{b}:{patch}";
    public static string ItemTimings(int hero, RankBracket b, string patch) => $"itime:{hero}:{b}:{patch}";
    public static string AbilityOrder(int hero, RankBracket b, string patch) => $"abil:{hero}:{b}:{patch}";
    /// <summary>The whole hero-versus-hero lane table for a bracket (analytics/hero-counter-stats).</summary>
    public static string Counters(RankBracket b, string patch) => $"counters:{b}:{patch}";
    public static string Ranks(IEnumerable<uint> accountIds) => $"ranks:{string.Join(',', accountIds.OrderBy(i => i))}";
    public static string MatchHistory(uint me) => $"mh:{me}";
    /// <summary>One finished match as the match history shows it, for you; a processed match never changes.</summary>
    public static string MatchDetail(uint me, long matchId) => $"md:{me}:{matchId}";
    /// <summary>Match ids whose salts were sent to the ingest endpoint (newest first, capped), and when the Steam cache was last scanned.</summary>
    public const string IngestSubmitted = "ingest:submitted";
    public const string IngestLastScan = "ingest:last-scan";
    /// <summary>Every hero's meta (analytics/hero-stats, no duration filter) for a bracket, for the hero pool view.</summary>
    public static string MetaHeroes(RankBracket b, string patch) => $"meta:heroes:{b}:{patch}";
    public static string SampleMatches(int hero, RankBracket b, string patch) => $"sample:{hero}:{b}:{patch}";
}

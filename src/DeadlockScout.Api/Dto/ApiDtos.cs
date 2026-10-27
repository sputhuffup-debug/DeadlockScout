using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeadlockScout.Api.Dto;

// Field names verified against the deadlock-api source (docs/verification/api.md). Every DTO keeps unknown
// fields in Extra so a new API field never breaks deserialisation, and every field is optional.

public abstract class TolerantDto
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>GET /v1/players/hero-stats item.</summary>
public sealed class HeroStatsDto : TolerantDto
{
    [JsonPropertyName("account_id")] public uint AccountId { get; set; }
    [JsonPropertyName("hero_id")] public int HeroId { get; set; }
    [JsonPropertyName("matches_played")] public long MatchesPlayed { get; set; }
    [JsonPropertyName("wins")] public long Wins { get; set; }
    [JsonPropertyName("kills")] public long Kills { get; set; }
    [JsonPropertyName("deaths")] public long Deaths { get; set; }
    [JsonPropertyName("assists")] public long Assists { get; set; }
    [JsonPropertyName("networth_per_min")] public double? NetWorthPerMin { get; set; }
    [JsonPropertyName("last_played")] public long? LastPlayed { get; set; }
}

/// <summary>GET /v1/players/{id}/enemy-stats and mate-stats items (enemy_id / mate_id).</summary>
public sealed class SharedStatsDto : TolerantDto
{
    [JsonPropertyName("enemy_id")] public uint? EnemyId { get; set; }
    [JsonPropertyName("mate_id")] public uint? MateId { get; set; }
    [JsonPropertyName("wins")] public long Wins { get; set; }
    [JsonPropertyName("matches_played")] public long MatchesPlayed { get; set; }
}

/// <summary>GET /v1/players/rank?account_ids= item.</summary>
public sealed class AccountRankDto : TolerantDto
{
    [JsonPropertyName("account_id")] public uint AccountId { get; set; }
    [JsonPropertyName("badge")] public int Badge { get; set; }
    [JsonPropertyName("rank")] public int Rank { get; set; }
    [JsonPropertyName("subrank")] public int Subrank { get; set; }
}

/// <summary>GET /v1/players/{id}/match-history item.</summary>
public sealed class MatchHistoryDto : TolerantDto
{
    [JsonPropertyName("match_id")] public long MatchId { get; set; }
    [JsonPropertyName("hero_id")] public int HeroId { get; set; }
    [JsonPropertyName("start_time")] public long StartTime { get; set; }
    [JsonPropertyName("match_duration_s")] public int MatchDurationS { get; set; }
    [JsonPropertyName("player_team")] public int PlayerTeam { get; set; }
    [JsonPropertyName("player_kills")] public int Kills { get; set; }
    [JsonPropertyName("player_deaths")] public int Deaths { get; set; }
    [JsonPropertyName("player_assists")] public int Assists { get; set; }
    [JsonPropertyName("net_worth")] public int NetWorth { get; set; }
    [JsonPropertyName("player_match_outcome")] public int Outcome { get; set; }
    [JsonPropertyName("ranked_display_badge")] public int? RankedBadge { get; set; }
    [JsonPropertyName("ranked_delta")] public int? RankedDelta { get; set; }
}

/// <summary>GET /v1/matches/{id}/live/url.</summary>
public sealed class LiveUrlDto : TolerantDto
{
    [JsonPropertyName("broadcast_url")] public string? BroadcastUrl { get; set; }
    [JsonPropertyName("lobby_id")] public long? LobbyId { get; set; }
}

/// <summary>GET /v1/matches/active item.</summary>
public sealed class ActiveMatchDto : TolerantDto
{
    [JsonPropertyName("match_id")] public long MatchId { get; set; }
    [JsonPropertyName("lobby_id")] public long? LobbyId { get; set; }
    [JsonPropertyName("start_time")] public long StartTime { get; set; }
    [JsonPropertyName("duration_s")] public int DurationS { get; set; }
    [JsonPropertyName("players")] public List<ActiveMatchPlayerDto>? Players { get; set; }
}

public sealed class ActiveMatchPlayerDto : TolerantDto
{
    [JsonPropertyName("account_id")] public uint AccountId { get; set; }
    [JsonPropertyName("team")] public int Team { get; set; }
    [JsonPropertyName("hero_id")] public int HeroId { get; set; }
}

/// <summary>GET /v1/analytics/hero-stats item (totals over matches).</summary>
public sealed class AnalyticsHeroStatsDto : TolerantDto
{
    [JsonPropertyName("hero_id")] public int HeroId { get; set; }
    [JsonPropertyName("matches")] public long Matches { get; set; }
    [JsonPropertyName("wins")] public long Wins { get; set; }
    [JsonPropertyName("total_net_worth")] public long TotalNetWorth { get; set; }
    [JsonPropertyName("total_kills")] public long TotalKills { get; set; }
    [JsonPropertyName("total_deaths")] public long TotalDeaths { get; set; }
    [JsonPropertyName("total_assists")] public long TotalAssists { get; set; }
}

/// <summary>GET /v1/analytics/item-stats item.</summary>
public sealed class ItemStatsDto : TolerantDto
{
    [JsonPropertyName("item_id")] public int ItemId { get; set; }
    [JsonPropertyName("matches")] public long Matches { get; set; }
    [JsonPropertyName("wins")] public long Wins { get; set; }
    [JsonPropertyName("avg_buy_time_s")] public double? AvgBuyTimeS { get; set; }
}

/// <summary>GET /v1/analytics/hero-counter-stats item (subset; kills, net worth and the rest stay in Extra).</summary>
public sealed class HeroCounterDto : TolerantDto
{
    [JsonPropertyName("hero_id")] public int HeroId { get; set; }
    [JsonPropertyName("enemy_hero_id")] public int EnemyHeroId { get; set; }
    [JsonPropertyName("wins")] public long Wins { get; set; }
    [JsonPropertyName("matches_played")] public long MatchesPlayed { get; set; }
}

/// <summary>GET /v1/assets/heroes item (subset).</summary>
public sealed class HeroAssetDto : TolerantDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("class_name")] public string? ClassName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("player_selectable")] public bool? PlayerSelectable { get; set; }
    [JsonPropertyName("disabled")] public bool? Disabled { get; set; }
    [JsonPropertyName("images")] public Dictionary<string, JsonElement>? Images { get; set; }
}

/// <summary>GET /v1/assets/items item (subset of the Upgrade shape; abilities and weapons share id/class_name/name/type).</summary>
public sealed class ItemAssetDto : TolerantDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("class_name")] public string? ClassName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("item_slot_type")] public string? SlotType { get; set; }
    [JsonPropertyName("item_tier")] public int? Tier { get; set; }
    [JsonPropertyName("cost")] public int? Cost { get; set; }
    [JsonPropertyName("shop_image_small")] public string? ShopImageSmall { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("shopable")] public bool? Shopable { get; set; }
}

/// <summary>GET /v1/assets/steam-info.</summary>
public sealed class SteamInfoDto : TolerantDto
{
    [JsonPropertyName("client_version")] public long ClientVersion { get; set; }
    [JsonPropertyName("server_version")] public long ServerVersion { get; set; }
    [JsonPropertyName("version_date")] public string? VersionDate { get; set; }
}

/// <summary>POST /v1/matches/salts body item.</summary>
public sealed class SaltsDto
{
    [JsonPropertyName("match_id")] public long MatchId { get; set; }
    [JsonPropertyName("metadata_salt")] public long? MetadataSalt { get; set; }
    [JsonPropertyName("replay_salt")] public long? ReplaySalt { get; set; }
    [JsonPropertyName("cluster_id")] public long? ClusterId { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
}

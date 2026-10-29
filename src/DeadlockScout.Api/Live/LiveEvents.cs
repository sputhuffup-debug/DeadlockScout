using System.Text.Json;
using System.Text.Json.Serialization;
using DeadlockScout.Api.Dto;

namespace DeadlockScout.Api.Live;

/// <summary>
/// One event from the live-events service (SSE). Envelope fields are flattened with the entity's own fields; everything
/// is optional. Shape verified against live-events/src/demo_parser (see docs/verification/api.md).
/// </summary>
public sealed class LiveEventDto : TolerantDto
{
    [JsonPropertyName("tick")] public long? Tick { get; set; }
    [JsonPropertyName("game_time")] public double? GameTime { get; set; }
    [JsonPropertyName("event_type")] public string? EventType { get; set; }
    [JsonPropertyName("delta")] public string? Delta { get; set; }
    [JsonPropertyName("entity_index")] public int? EntityIndex { get; set; }
    [JsonPropertyName("entity_type")] public string? EntityType { get; set; }

    // player_controller fields
    [JsonPropertyName("steam_id")] public uint? SteamId { get; set; }
    [JsonPropertyName("steam_name")] public string? SteamName { get; set; }
    [JsonPropertyName("team")] public int? Team { get; set; }
    [JsonPropertyName("hero_id")] public int? HeroId { get; set; }
    [JsonPropertyName("player_slot")] public int? PlayerSlot { get; set; }
    [JsonPropertyName("rank")] public int? Rank { get; set; }
    [JsonPropertyName("assigned_lane")] public int? AssignedLane { get; set; }
    [JsonPropertyName("net_worth")] public int? NetWorth { get; set; }
    [JsonPropertyName("kills")] public int? Kills { get; set; }
    [JsonPropertyName("deaths")] public int? Deaths { get; set; }
    [JsonPropertyName("assists")] public int? Assists { get; set; }
    /// <summary>Item ids the player holds (the broadcast's upgrade tokens, which are the assets' item ids). Direct feed only.</summary>
    [JsonPropertyName("items")] public long[]? Items { get; set; }
    [JsonPropertyName("ultimate_trained")] public bool? UltimateTrained { get; set; }

    // objectives fields (Direct feed only): whether the mid boss is up, each team's Rejuvenator count, whether the urn is on the map
    [JsonPropertyName("mid_boss_alive")] public bool? MidBossAlive { get; set; }
    [JsonPropertyName("rejuv_amber")] public int? RejuvAmber { get; set; }
    [JsonPropertyName("rejuv_sapphire")] public int? RejuvSapphire { get; set; }
    [JsonPropertyName("urn_on_map")] public bool? UrnOnMap { get; set; }

    public bool IsPlayerController => string.Equals(EntityType, "player_controller", StringComparison.OrdinalIgnoreCase);
    public bool IsObjectives => string.Equals(EventType, "objectives", StringComparison.OrdinalIgnoreCase);
    public bool IsTickEnd => string.Equals(EventType, "tick_end", StringComparison.OrdinalIgnoreCase);
    public bool IsEnd => string.Equals(EventType, "end", StringComparison.OrdinalIgnoreCase);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    public static LiveEventDto? TryParse(string data, string? sseEventName)
    {
        if (string.IsNullOrWhiteSpace(data) || data[0] != '{') return sseEventName == "end" ? new LiveEventDto { EventType = "end" } : null;
        try
        {
            var dto = JsonSerializer.Deserialize<LiveEventDto>(data, Json);
            if (dto is not null && dto.EventType is null) dto.EventType = sseEventName;
            return dto;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>Where the stream of live events comes from. The SSE service is the verified implementation; a hosted WebSocket one can be added behind the same seam.</summary>
public interface ILiveEventStream
{
    IAsyncEnumerable<LiveEventDto> ReadAsync(long matchId, CancellationToken ct);
}

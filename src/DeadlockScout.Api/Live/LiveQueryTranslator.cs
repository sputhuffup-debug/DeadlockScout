using System.Text.Json;

namespace DeadlockScout.Api.Live;

/// <summary>
/// Turns rows from the hosted live query into the same <see cref="LiveEventDto"/>s the self-hosted live-events service
/// sends, so <see cref="LiveMatchFeed"/> does not care where they came from. Pure and single-threaded; the stream feeds
/// it rows in arrival order.
///
/// Each controller row is the player's full state at that tick (the query engine materialises the whole entity on every
/// create/update; delete/leave rows are all nulls). Game time follows the live-events service:
/// <c>(tick - totalPausedTicks) / tickRate - gameStartTime</c>, with both values taken from the game-rules proxy rows.
/// A <c>tick_end</c> is synthesised when the tick advances, and only once the clock is known, so no sample is ever
/// stamped with a wrong game time.
/// </summary>
public sealed class LiveQueryTranslator
{
    public const ulong Steam64Base = 76561197960265728UL;

    // Column names from GET /v1/matches/demo/schema (CCitadelPlayerController, CCitadelGameRulesProxy). Columns are
    // quoted (mixed case); table names are not: the live engine resolves CCitadelPlayerController but answers "table
    // not found" for "CCitadelPlayerController" (live, Oct 31, 2026; verification note 29).
    public const string ColSteamId = "m_steamID";
    public const string ColName = "m_iszPlayerName";
    public const string ColTeam = "m_iTeamNum";
    public const string ColLane = "m_nAssignedLane";
    public const string ColHeroId = "m_PlayerDataGlobal__m_nHeroID";
    public const string ColNetWorth = "m_PlayerDataGlobal__m_iGoldNetWorth";
    public const string ColKills = "m_PlayerDataGlobal__m_iPlayerKills";
    public const string ColDeaths = "m_PlayerDataGlobal__m_iDeaths";
    public const string ColAssists = "m_PlayerDataGlobal__m_iPlayerAssists";
    public const string ColGameStart = "m_pGameRules__m_flGameStartTime";
    public const string ColPausedTicks = "m_pGameRules__m_nTotalPausedTicks";

    /// <summary>
    /// Columns are double-quoted (mixed case, and SQL folds unquoted names to lower case); the table name is bare, which is
    /// the only form the live engine resolves.
    /// </summary>
    public static readonly string ControllerQuery =
        $"SELECT \"tick\", \"entity_index\", \"delta_type\", \"{ColSteamId}\", \"{ColName}\", \"{ColTeam}\", \"{ColLane}\", " +
        $"\"{ColHeroId}\", \"{ColNetWorth}\", \"{ColKills}\", \"{ColDeaths}\", \"{ColAssists}\" FROM CCitadelPlayerController";

    public static readonly string RulesQuery =
        $"SELECT \"tick\", \"delta_type\", \"{ColGameStart}\", \"{ColPausedTicks}\" FROM CCitadelGameRulesProxy";

    private readonly double _tickRate;
    private double? _gameStartTime;
    private long _pausedTicks;
    private long _lastTick = -1;

    public LiveQueryTranslator(int tickRate = 64) => _tickRate = tickRate > 0 ? tickRate : 64;

    public bool ClockKnown => _gameStartTime is > 0;

    /// <summary>Game time at <paramref name="tick"/>, or null until the game-rules row has given the start time.</summary>
    public double? GameTimeAt(long tick) => ClockKnown ? (tick - _pausedTicks) / _tickRate - _gameStartTime!.Value : null;

    /// <summary>A row of <see cref="RulesQuery"/>: updates the clock. Never produces events.</summary>
    public void OnRulesRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) return;
        if (Number(row, ColGameStart) is { } start && start > 0) _gameStartTime = start;
        if (Number(row, ColPausedTicks) is { } paused && paused >= 0) _pausedTicks = (long)paused;
    }

    /// <summary>A row of <see cref="ControllerQuery"/>: a tick_end for the previous tick when the tick advances, then the player state.</summary>
    public IEnumerable<LiveEventDto> OnControllerRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) yield break;
        long tick = (long)(Number(row, "tick") ?? -1);
        if (tick < 0) yield break;

        if (_lastTick >= 0 && tick > _lastTick && ClockKnown)
            yield return TickEnd(_lastTick);
        if (tick > _lastTick) _lastTick = tick;

        string delta = row.TryGetProperty("delta_type", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : "update";
        uint? account = null;
        if (row.TryGetProperty(ColSteamId, out var sid) && sid.ValueKind == JsonValueKind.Number && sid.TryGetUInt64(out var steam))
            account = steam >= Steam64Base ? (uint)(steam - Steam64Base) : steam <= uint.MaxValue ? (uint)steam : null;

        yield return new LiveEventDto
        {
            Tick = tick,
            GameTime = GameTimeAt(tick),
            EventType = delta == "create" ? "entity_create" : delta == "update" ? "entity_update" : "entity_" + delta,
            Delta = delta,
            EntityIndex = (int?)Number(row, "entity_index"),
            EntityType = "player_controller",
            SteamId = account,
            SteamName = row.TryGetProperty(ColName, out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
            Team = (int?)Number(row, ColTeam),
            HeroId = (int?)Number(row, ColHeroId),
            AssignedLane = (int?)Number(row, ColLane),
            NetWorth = (int?)Number(row, ColNetWorth),
            Kills = (int?)Number(row, ColKills),
            Deaths = (int?)Number(row, ColDeaths),
            Assists = (int?)Number(row, ColAssists),
        };
    }

    /// <summary>Called when the controller stream ends: closes the last tick and ends the match.</summary>
    public IEnumerable<LiveEventDto> OnEnd()
    {
        if (_lastTick >= 0 && ClockKnown) yield return TickEnd(_lastTick);
        yield return new LiveEventDto { EventType = "end" };
    }

    private LiveEventDto TickEnd(long tick) => new() { Tick = tick, GameTime = GameTimeAt(tick), EventType = "tick_end" };

    private static double? Number(JsonElement row, string name)
        => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}

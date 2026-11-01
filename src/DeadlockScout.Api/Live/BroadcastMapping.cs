using DeadlockScout.Core.Laning;

namespace DeadlockScout.Api.Live;

/// <summary>One player controller as read from the broadcast, independent of the parser library.</summary>
public readonly record struct PlayerState(
    int EntityIndex, ulong SteamId64, string Name, int Team, int HeroId, int Lane,
    int NetWorth, int Kills, int Deaths, int Assists, long[]? Items = null, bool? UltimateTrained = null, float? X = null, float? Y = null);

/// <summary>
/// The map objectives as read from the broadcast: whether a mid boss entity is alive, each team's Rejuvenator count from
/// the game rules, and whether an urn pickup entity is on the map. Null where the entity or rules were not readable.
/// </summary>
public readonly record struct ObjectiveState(bool? MidBossAlive, int RejuvAmber, int RejuvSapphire, bool? UrnOnMap);

/// <summary>The game-rules values the clock needs, plus the parser's current server tick and tick interval.</summary>
public readonly record struct ClockState(
    long CurrentTick, double TickInterval, float GameStartTime,
    int MatchClockUpdateTick, float MatchClockAtLastUpdate, bool Paused);

/// <summary>The in-game match clock (what the HUD timer shows), from the game-rules entity.</summary>
public static class BroadcastClock
{
    /// <summary>
    /// Seconds since the match started, or null before the rules have said. Prefers the match clock the game keeps for its
    /// HUD (<c>m_flMatchClockAtLastUpdate</c> at <c>m_nMatchClockUpdateTick</c>, frozen while paused). Falls back to
    /// <c>tick * interval - m_flGameStartTime</c>, which ignores pauses, when the match clock is not set.
    /// </summary>
    public static double? GameTimeSeconds(ClockState c)
    {
        if (c.TickInterval <= 0 || c.CurrentTick <= 0) return null;
        if (c.MatchClockUpdateTick > 0)
        {
            if (c.Paused) return c.MatchClockAtLastUpdate;
            return c.MatchClockAtLastUpdate + (c.CurrentTick - c.MatchClockUpdateTick) * c.TickInterval;
        }
        if (c.GameStartTime > 0) return c.CurrentTick * c.TickInterval - c.GameStartTime;
        return null;
    }
}

/// <summary>Turns a snapshot of the broadcast into the events <see cref="LiveMatchFeed"/> consumes. Pure.</summary>
public static class BroadcastEventMapper
{
    public static IEnumerable<LiveEventDto> Snapshot(IReadOnlyList<PlayerState> players, long tick, double? gameTime, ObjectiveState? objectives = null,
                                                     IReadOnlyList<NeutralSighting>? neutrals = null)
    {
        double? gt = gameTime is >= 0 ? gameTime : null;
        foreach (var p in players)
        {
            if (p.SteamId64 == 0) continue; // bots and empty slots
            uint account = p.SteamId64 >= LiveQueryTranslator.Steam64Base ? (uint)(p.SteamId64 - LiveQueryTranslator.Steam64Base)
                         : p.SteamId64 <= uint.MaxValue ? (uint)p.SteamId64 : 0;
            if (account == 0) continue;
            yield return new LiveEventDto
            {
                Tick = tick,
                GameTime = gt,
                EventType = "entity_update",
                Delta = "update",
                EntityIndex = p.EntityIndex,
                EntityType = "player_controller",
                SteamId = account,
                SteamName = p.Name,
                Team = p.Team,
                HeroId = p.HeroId > 0 ? p.HeroId : null,
                AssignedLane = p.Lane >= 0 ? p.Lane : null,
                NetWorth = p.NetWorth,
                Kills = p.Kills,
                Deaths = p.Deaths,
                Assists = p.Assists,
                Items = p.Items,
                UltimateTrained = p.UltimateTrained,
                X = p.X,
                Y = p.Y,
            };
        }
        if (objectives is { } o)
            yield return new LiveEventDto
            {
                Tick = tick, GameTime = gt, EventType = "objectives",
                MidBossAlive = o.MidBossAlive, RejuvAmber = o.RejuvAmber, RejuvSapphire = o.RejuvSapphire, UrnOnMap = o.UrnOnMap,
            };
        if (neutrals is not null)
            yield return new LiveEventDto
            {
                Tick = tick, GameTime = gt, EventType = "camps",
                Neutrals = neutrals.Select(n => new NeutralDto { Id = n.Id, X = n.X, Y = n.Y, Sinners = n.Sinners }).ToList(),
            };
        if (gt is not null) yield return new LiveEventDto { Tick = tick, GameTime = gt, EventType = "tick_end" };
    }
}

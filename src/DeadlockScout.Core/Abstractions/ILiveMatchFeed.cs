using DeadlockScout.Core.Laning;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Objectives;

namespace DeadlockScout.Core.Abstractions;

/// <summary>The delayed public broadcast of the current match: roster once, then net-worth ticks.</summary>
public interface ILiveMatchFeed
{
    /// <summary>Resolves once all players are known; may take a minute after match start because the broadcast is delayed.</summary>
    Task<Roster> WaitForRosterAsync(MatchContext ctx, CancellationToken ct);

    /// <summary>Net-worth samples for every player, in game-time order, until the match ends or <paramref name="ct"/> fires.</summary>
    IAsyncEnumerable<NetWorthTick> TicksAsync(MatchContext ctx, CancellationToken ct);

    /// <summary>
    /// The map objectives (mid boss, Rejuvenator counts, urn): the latest state on subscribing, then each change, until
    /// the match ends. Only the Direct feed reads them; other sources never yield.
    /// </summary>
    IAsyncEnumerable<ObjectiveSample> ObjectivesAsync(MatchContext ctx, CancellationToken ct);

    /// <summary>
    /// The alive neutral camps and the players' positions, once a second until the match ends, for the camp respawn
    /// timers. Only the Direct feed reads them; other sources never yield.
    /// </summary>
    IAsyncEnumerable<CampSample> CampsAsync(MatchContext ctx, CancellationToken ct);

    /// <summary>Stops reading the match's broadcast once the match has ended; nothing reads it afterwards.</summary>
    void Stop(long matchId);
}

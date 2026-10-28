using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Abstractions;

/// <summary>The delayed public broadcast of the current match: roster once, then net-worth ticks.</summary>
public interface ILiveMatchFeed
{
    /// <summary>Resolves once all players are known; may take a minute after match start because the broadcast is delayed.</summary>
    Task<Roster> WaitForRosterAsync(MatchContext ctx, CancellationToken ct);

    /// <summary>Net-worth samples for every player, in game-time order, until the match ends or <paramref name="ct"/> fires.</summary>
    IAsyncEnumerable<NetWorthTick> TicksAsync(MatchContext ctx, CancellationToken ct);

    /// <summary>Stops reading the match's broadcast once the match has ended; nothing reads it afterwards.</summary>
    void Stop(long matchId);
}

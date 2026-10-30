using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Abstractions;

public interface IScoutingService
{
    /// <summary>One bulk call for every cache miss in the roster; rows come back in roster order.</summary>
    Task<IReadOnlyList<ScoutRow>> ScoutAsync(Roster roster, uint myAccountId, CancellationToken ct);

    /// <summary>
    /// Likely parties: account id → group number for players in a group of two or more (see <c>PartyGroups</c>). One
    /// call per player, each cached for a day, so it runs after the scout list is up rather than delaying it.
    /// </summary>
    Task<IReadOnlyDictionary<uint, int>> PartiesAsync(Roster roster, CancellationToken ct);
}

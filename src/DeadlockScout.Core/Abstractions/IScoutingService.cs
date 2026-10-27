using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Abstractions;

public interface IScoutingService
{
    /// <summary>One bulk call for every cache miss in the roster; rows come back in roster order.</summary>
    Task<IReadOnlyList<ScoutRow>> ScoutAsync(Roster roster, uint myAccountId, CancellationToken ct);
}

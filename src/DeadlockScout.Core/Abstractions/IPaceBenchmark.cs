using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Abstractions;

/// <summary>Produces a net-worth curve for a hero in a rank bracket. Stateless over <see cref="ICache"/>.</summary>
public interface IPaceBenchmark
{
    /// <summary>Lower runs first. The composite returns the first non-null curve and keeps upgrading in the background.</summary>
    int Priority { get; }
    string Name { get; }
    Task<PaceCurve?> GetAsync(int heroId, RankBracket bracket, CancellationToken ct);
}

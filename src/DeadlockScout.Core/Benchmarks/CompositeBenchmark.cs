using System.Runtime.CompilerServices;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockScout.Core.Benchmarks;

/// <summary>
/// Tries providers by <see cref="IPaceBenchmark.Priority"/> and returns the first curve; <see cref="WatchAsync"/> keeps
/// going and yields each better (lower-priority-number is faster, higher is more accurate) curve as it arrives.
/// </summary>
public sealed class CompositeBenchmark : IPaceBenchmark
{
    private readonly IReadOnlyList<IPaceBenchmark> _providers;
    private readonly ILogger _log;

    public CompositeBenchmark(IEnumerable<IPaceBenchmark> providers, ILogger<CompositeBenchmark>? log = null)
    {
        _providers = providers.OrderBy(p => p.Priority).ToList();
        _log = log ?? NullLogger<CompositeBenchmark>.Instance;
    }

    public int Priority => 0;
    public string Name => "composite";
    public IReadOnlyList<IPaceBenchmark> Providers => _providers;

    /// <summary>First non-null curve in priority order.</summary>
    public async Task<PaceCurve?> GetAsync(int heroId, RankBracket bracket, CancellationToken ct)
    {
        foreach (var p in _providers)
        {
            try
            {
                var curve = await p.GetAsync(heroId, bracket, ct).ConfigureAwait(false);
                if (curve is not null) return curve;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Benchmark provider {Provider} failed for hero {Hero} {Bracket}", p.Name, heroId, bracket);
            }
        }
        return null;
    }

    /// <summary>
    /// Yields the fastest curve first, then every later provider's curve as it completes, in priority order.
    /// The consumer keeps the latest one it received.
    /// </summary>
    public async IAsyncEnumerable<PaceCurve> WatchAsync(int heroId, RankBracket bracket, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var p in _providers)
        {
            PaceCurve? curve = null;
            try
            {
                curve = await p.GetAsync(heroId, bracket, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { yield break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Benchmark provider {Provider} failed for hero {Hero} {Bracket}", p.Name, heroId, bracket);
            }
            if (curve is not null) yield return curve;
        }
    }
}

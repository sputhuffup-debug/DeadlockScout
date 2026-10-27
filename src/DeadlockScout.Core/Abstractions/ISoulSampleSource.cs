using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Abstractions;

/// <summary>Emits your own soul samples with game time. Knows nothing about the overlay or the benchmark.</summary>
public interface ISoulSampleSource
{
    SampleSource Kind { get; }
    /// <summary>How far behind real time a sample is expected to be (broadcast delay, OCR latency).</summary>
    TimeSpan ExpectedLatency { get; }
    /// <summary>False when the source cannot currently produce trustworthy samples.</summary>
    bool IsHealthy { get; }
    IAsyncEnumerable<SoulSample> ReadAsync(MatchContext ctx, CancellationToken ct);
}

namespace DeadlockScout.Core.Abstractions;

/// <summary>Key that changes on a game patch so benchmark and item caches roll over. Default: date bucket rotated every 7 days.</summary>
public interface IPatchKeyProvider
{
    ValueTask<string> GetAsync(CancellationToken ct);
}

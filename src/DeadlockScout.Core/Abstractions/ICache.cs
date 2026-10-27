namespace DeadlockScout.Core.Abstractions;

/// <summary>Two-layer cache (memory in front of SQLite). Values are JSON-serialised.</summary>
public interface ICache
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct);

    /// <summary><paramref name="ttl"/> null means never expires.</summary>
    Task SetAsync<T>(string key, T value, TimeSpan? ttl, CancellationToken ct);

    /// <summary>
    /// Returns the cached value, or runs <paramref name="factory"/> once (concurrent callers for the same key share the in-flight task).
    /// With <paramref name="staleWhileRevalidate"/> an expired entry is returned immediately and refreshed in the background.
    /// </summary>
    Task<T> GetOrCreateAsync<T>(string key, TimeSpan? ttl, Func<CancellationToken, Task<T>> factory,
                                bool staleWhileRevalidate, CancellationToken ct);

    Task RemoveAsync(string key, CancellationToken ct);
}

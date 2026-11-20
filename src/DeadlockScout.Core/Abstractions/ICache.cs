namespace DeadlockScout.Core.Abstractions;

/// <summary>Two-layer cache (memory in front of SQLite). Values are JSON-serialised.</summary>
public interface ICache
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct);

    /// <summary>The cached value even when it has expired (null only when there is none), for a page shown at once before it refreshes.</summary>
    Task<T?> GetAnyAsync<T>(string key, CancellationToken ct) => GetAsync<T>(key, ct);

    /// <summary>What the cache holds in memory right now, for Diagnostics; null when it does not say.</summary>
    string? MemoryUse => null;

    /// <summary><paramref name="ttl"/> null means never expires.</summary>
    Task SetAsync<T>(string key, T value, TimeSpan? ttl, CancellationToken ct);

    /// <summary>
    /// Returns the cached value, or runs <paramref name="factory"/> once (concurrent callers for the same key share the in-flight task).
    /// With <paramref name="staleWhileRevalidate"/> an expired entry is returned immediately and refreshed in the background.
    /// A null result is returned but never stored: null means "not available yet", and the next call asks again.
    /// </summary>
    Task<T> GetOrCreateAsync<T>(string key, TimeSpan? ttl, Func<CancellationToken, Task<T>> factory,
                                bool staleWhileRevalidate, CancellationToken ct);

    Task RemoveAsync(string key, CancellationToken ct);
}

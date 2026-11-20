using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeadlockScout.Core.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockScout.Api.Caching;

/// <summary>
/// <see cref="ICache"/> over an in-process <see cref="IMemoryCache"/> in front of <see cref="SqliteCacheStore"/>.
/// Concurrent callers for one key share a single in-flight factory; stale-while-revalidate serves an expired entry at
/// once and refreshes it in the background.
/// <para>
/// The memory layer is bounded (1.0.3): each entry weighs its JSON's length, the layer holds at most
/// <see cref="MemoryBudget"/> of it, and an entry unused for <see cref="MemoryIdle"/> leaves memory (SQLite keeps it, so
/// nothing is fetched again). Before, every entry stayed for the whole session, most of them for ever: on Nov 18 the
/// .NET heap peaked at 67, 90 and 131 MB over three matches and the app started each match 20–60 MB larger.
/// </para>
/// </summary>
public sealed class LayeredCache : ICache, IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record Envelope(DateTimeOffset FetchedAt, DateTimeOffset? ExpiresAt, object? Value)
    {
        public bool IsFresh(DateTimeOffset now) => ExpiresAt is null || ExpiresAt > now;
    }

    /// <summary>The memory layer's budget, in characters of JSON (the objects take about two to three times that).</summary>
    public const long MemoryBudget = 16_000_000;
    /// <summary>How long an entry nobody reads stays in memory.</summary>
    public static readonly TimeSpan MemoryIdle = TimeSpan.FromMinutes(15);

    private readonly IMemoryCache _memory;
    private readonly MemoryCache? _own;
    private readonly SqliteCacheStore _store;
    private readonly IClock _clock;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inFlight = new();
    private readonly ConcurrentDictionary<string, byte> _revalidating = new();

    public LayeredCache(SqliteCacheStore store, IClock clock, IMemoryCache? memory = null, ILogger<LayeredCache>? log = null)
    {
        _store = store;
        _clock = clock;
        _own = memory is null ? new MemoryCache(new MemoryCacheOptions { SizeLimit = MemoryBudget, CompactionPercentage = 0.25, TrackStatistics = true }) : null;
        _memory = memory ?? _own!;
        _log = log ?? NullLogger<LayeredCache>.Instance;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct)
    {
        var env = await LoadAsync<T>(key, ct).ConfigureAwait(false);
        if (env is null || !env.IsFresh(_clock.UtcNow)) return default;
        return (T?)env.Value;
    }

    public async Task<T?> GetAnyAsync<T>(string key, CancellationToken ct)
        => (T?)(await LoadAsync<T>(key, ct).ConfigureAwait(false))?.Value;

    /// <summary>Returns the entry even when expired, with its freshness, for callers that can show an "offline" badge.</summary>
    public async Task<(T? Value, bool Fresh, DateTimeOffset? FetchedAt)> GetStaleAsync<T>(string key, CancellationToken ct)
    {
        var env = await LoadAsync<T>(key, ct).ConfigureAwait(false);
        if (env is null) return (default, false, null);
        return ((T?)env.Value, env.IsFresh(_clock.UtcNow), env.FetchedAt);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var env = new Envelope(now, ttl is null ? null : now + ttl.Value, value);
        var json = JsonSerializer.Serialize(value, Json);
        _memory.Set(key, env, MemoryOptions(ttl, json.Length));
        await _store.SetAsync(new CacheEntry(key, json, env.FetchedAt, env.ExpiresAt, null), ct).ConfigureAwait(false);
    }

    public async Task<T> GetOrCreateAsync<T>(string key, TimeSpan? ttl, Func<CancellationToken, Task<T>> factory, bool staleWhileRevalidate, CancellationToken ct)
    {
        var env = await LoadAsync<T>(key, ct).ConfigureAwait(false);
        var now = _clock.UtcNow;
        if (env is not null)
        {
            if (env.IsFresh(now)) return (T)env.Value!;
            if (staleWhileRevalidate)
            {
                Revalidate(key, ttl, factory);
                return (T)env.Value!;
            }
        }

        // Coalesce: one factory run per key, shared by every concurrent caller.
        var lazy = _inFlight.GetOrAdd(key, k => new Lazy<Task<object?>>(async () =>
        {
            try
            {
                var value = await factory(CancellationToken.None).ConfigureAwait(false);
                // Null means "not available yet" (e.g. metadata of a match the API has not processed): never cache it,
                // or a forever-entry would hide the match for good.
                if (value is not null) await SetAsync(key, value, ttl, CancellationToken.None).ConfigureAwait(false);
                return value;
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
            }
        }));
        var result = await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        return (T)result!;
    }

    public Task RemoveAsync(string key, CancellationToken ct)
    {
        _memory.Remove(key);
        return _store.RemoveAsync(key, ct);
    }

    private void Revalidate<T>(string key, TimeSpan? ttl, Func<CancellationToken, Task<T>> factory)
    {
        if (!_revalidating.TryAdd(key, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var value = await factory(CancellationToken.None).ConfigureAwait(false);
                if (value is not null) await SetAsync(key, value, ttl, CancellationToken.None).ConfigureAwait(false); // keep the stale value
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Background revalidation of {Key} failed; stale value stays", key);
            }
            finally
            {
                _revalidating.TryRemove(key, out _);
            }
        });
    }

    private async Task<Envelope?> LoadAsync<T>(string key, CancellationToken ct)
    {
        if (_memory.TryGetValue(key, out Envelope? env) && env is not null) return env;
        var row = await _store.GetAsync(key, ct).ConfigureAwait(false);
        if (row is null) return null;
        T? value;
        try { value = JsonSerializer.Deserialize<T>(row.Json, Json); }
        catch (JsonException ex)
        {
            _log.LogDebug(ex, "Cache row {Key} does not deserialize as {Type}; dropping", key, typeof(T).Name);
            await _store.RemoveAsync(key, ct).ConfigureAwait(false);
            return null;
        }
        env = new Envelope(row.FetchedAt, row.ExpiresAt, value);
        _memory.Set(key, env, MemoryOptions(row.ExpiresAt is null ? null : row.ExpiresAt - _clock.UtcNow, row.Json.Length));
        return env;
    }

    private static MemoryCacheEntryOptions MemoryOptions(TimeSpan? ttl, long size)
    {
        var o = new MemoryCacheEntryOptions { Size = Math.Max(1, size), SlidingExpiration = MemoryIdle };
        // Keep expired entries in memory a little longer so stale-while-revalidate does not have to hit SQLite.
        if (ttl is { } t) o.AbsoluteExpirationRelativeToNow = (t < TimeSpan.Zero ? TimeSpan.Zero : t) + TimeSpan.FromMinutes(5);
        return o;
    }

    /// <summary>"cache in memory: 412 entries, 9.3 MB of JSON" for Diagnostics' memory line; null for an injected memory cache.</summary>
    public string? MemoryUse => _own?.GetCurrentStatistics() is { } s
        ? $"cache in memory: {s.CurrentEntryCount} entries, {(s.CurrentEstimatedSize ?? 0) / 1_000_000.0:0.0} MB of JSON"
        : null;

    public void Dispose() => (_memory as IDisposable)?.Dispose();
}

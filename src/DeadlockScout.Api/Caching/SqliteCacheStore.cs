using Microsoft.Data.Sqlite;

namespace DeadlockScout.Api.Caching;

/// <summary>One row per key. Expired rows are kept until overwritten or purged so they can be served stale.</summary>
public sealed record CacheEntry(string Key, string Json, DateTimeOffset FetchedAt, DateTimeOffset? ExpiresAt, string? ETag);

/// <summary>Durable layer of the cache: SQLite in WAL mode at %LOCALAPPDATA%\DeadlockScout\cache.db (or any path).</summary>
public sealed class SqliteCacheStore : IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteCacheStore(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS cache (
                key        TEXT PRIMARY KEY,
                json       TEXT NOT NULL,
                fetched_at INTEGER NOT NULL,
                expires_at INTEGER,
                etag       TEXT
            );
            CREATE INDEX IF NOT EXISTS cache_expires ON cache(expires_at);
            """;
        cmd.ExecuteNonQuery();
    }

    public static string DefaultPath()
        => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeadlockScout", "cache.db");

    public string Path { get; }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    public async Task<CacheEntry?> GetAsync(string key, CancellationToken ct)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json, fetched_at, expires_at, etag FROM cache WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new CacheEntry(key, r.GetString(0),
            DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)),
            r.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)),
            r.IsDBNull(3) ? null : r.GetString(3));
    }

    public async Task SetAsync(CacheEntry e, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO cache(key, json, fetched_at, expires_at, etag) VALUES ($k, $j, $f, $e, $t)
                ON CONFLICT(key) DO UPDATE SET json = excluded.json, fetched_at = excluded.fetched_at, expires_at = excluded.expires_at, etag = excluded.etag
                """;
            cmd.Parameters.AddWithValue("$k", e.Key);
            cmd.Parameters.AddWithValue("$j", e.Json);
            cmd.Parameters.AddWithValue("$f", e.FetchedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$e", e.ExpiresAt.HasValue ? e.ExpiresAt.Value.ToUnixTimeMilliseconds() : DBNull.Value);
            cmd.Parameters.AddWithValue("$t", (object?)e.ETag ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task RemoveAsync(string key, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM cache WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Deletes rows that expired more than <paramref name="olderThan"/> ago. Returns the count.</summary>
    public async Task<int> PurgeExpiredAsync(DateTimeOffset now, TimeSpan olderThan, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM cache WHERE expires_at IS NOT NULL AND expires_at < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", (now - olderThan).ToUnixTimeMilliseconds());
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM cache";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public void Dispose() => _writeLock.Dispose();
}

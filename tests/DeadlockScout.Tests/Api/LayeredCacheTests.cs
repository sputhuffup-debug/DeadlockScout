using DeadlockScout.Api.Caching;

namespace DeadlockScout.Tests.Api;

public class LayeredCacheTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dls-cache");
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero));
    private SqliteCacheStore Store() => new(Path.Combine(_dir.FullName, "cache.db"));

    public void Dispose() { try { _dir.Delete(true); } catch { } }

    [Fact]
    public async Task Round_trips_through_sqlite_and_survives_a_new_process()
    {
        using (var store = Store())
        using (var cache = new LayeredCache(store, _clock))
            await cache.SetAsync("k", new List<int> { 1, 2, 3 }, TimeSpan.FromHours(1), default);

        using var store2 = Store();
        using var cache2 = new LayeredCache(store2, _clock);
        Assert.Equal(new List<int> { 1, 2, 3 }, await cache2.GetAsync<List<int>>("k", default));
        Assert.Equal(1, await store2.CountAsync(default));
    }

    [Fact]
    public async Task Expired_entries_are_invisible_to_Get_but_served_stale_when_asked()
    {
        using var store = Store();
        using var cache = new LayeredCache(store, _clock);
        await cache.SetAsync("k", "v", TimeSpan.FromMinutes(10), default);
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(await cache.GetAsync<string>("k", default));
        var (value, fresh, fetched) = await cache.GetStaleAsync<string>("k", default);
        Assert.Equal("v", value);
        Assert.False(fresh);
        Assert.NotNull(fetched);
    }

    [Fact]
    public async Task A_null_result_is_not_cached_so_the_next_call_asks_again()
    {
        using var store = Store();
        using var cache = new LayeredCache(store, _clock);
        int calls = 0;
        string? available = null; // e.g. match metadata the API has not processed yet
        Task<string?> Fetch(CancellationToken _) { calls++; return Task.FromResult(available); }

        Assert.Null(await cache.GetOrCreateAsync<string?>("meta:1", null, Fetch, false, default));
        Assert.Equal(0, await store.CountAsync(default));
        available = "ready";
        Assert.Equal("ready", await cache.GetOrCreateAsync<string?>("meta:1", null, Fetch, false, default));
        Assert.Equal("ready", await cache.GetOrCreateAsync<string?>("meta:1", null, Fetch, false, default)); // now cached for good
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Forever_entries_never_expire()
    {
        using var store = Store();
        using var cache = new LayeredCache(store, _clock);
        await cache.SetAsync("live:1", "url", null, default);
        _clock.Advance(TimeSpan.FromDays(400));
        Assert.Equal("url", await cache.GetAsync<string>("live:1", default));
    }

    [Fact]
    public async Task Concurrent_callers_share_one_factory_run()
    {
        using var store = Store();
        using var cache = new LayeredCache(store, _clock);
        int runs = 0;
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 20).Select(_ => cache.GetOrCreateAsync("k", TimeSpan.FromMinutes(1), async ct =>
        {
            Interlocked.Increment(ref runs);
            await gate.Task;
            return 42;
        }, false, default)).ToArray();
        await Task.Delay(50);
        gate.SetResult();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(42, r));
        Assert.Equal(1, runs);
        Assert.Equal(42, await cache.GetAsync<int>("k", default));
    }

    [Fact]
    public async Task Stale_while_revalidate_returns_the_old_value_and_refreshes_in_the_background()
    {
        using var store = Store();
        using var cache = new LayeredCache(store, _clock);
        await cache.SetAsync("k", "old", TimeSpan.FromMinutes(1), default);
        _clock.Advance(TimeSpan.FromMinutes(2));
        var refreshed = new TaskCompletionSource();
        var v = await cache.GetOrCreateAsync("k", TimeSpan.FromMinutes(1), ct => { refreshed.TrySetResult(); return Task.FromResult("new"); }, true, default);
        Assert.Equal("old", v);
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // the background write lands shortly after
        for (int i = 0; i < 50 && await cache.GetAsync<string>("k", default) != "new"; i++) await Task.Delay(20);
        Assert.Equal("new", await cache.GetAsync<string>("k", default));
    }

    [Fact]
    public async Task Without_stale_while_revalidate_an_expired_entry_is_refetched_inline()
    {
        using var store = Store();
        using var cache = new LayeredCache(store, _clock);
        await cache.SetAsync("k", "old", TimeSpan.FromMinutes(1), default);
        _clock.Advance(TimeSpan.FromMinutes(2));
        var v = await cache.GetOrCreateAsync("k", TimeSpan.FromMinutes(1), _ => Task.FromResult("new"), false, default);
        Assert.Equal("new", v);
    }

    [Fact]
    public async Task Factory_failure_propagates_and_caches_nothing()
    {
        using var store = Store();
        using var cache = new LayeredCache(store, _clock);
        await Assert.ThrowsAsync<HttpRequestException>(() => cache.GetOrCreateAsync<string>("k", null, _ => throw new HttpRequestException("x"), false, default));
        Assert.Null(await cache.GetAsync<string>("k", default));
        Assert.Equal("ok", await cache.GetOrCreateAsync("k", null, _ => Task.FromResult("ok"), false, default));
    }

    [Fact]
    public async Task Purge_removes_long_expired_rows_only()
    {
        using var store = Store();
        using var cache = new LayeredCache(store, _clock);
        await cache.SetAsync("a", 1, TimeSpan.FromMinutes(1), default);
        await cache.SetAsync("b", 2, TimeSpan.FromDays(30), default);
        await cache.SetAsync("c", 3, null, default);
        _clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, await store.PurgeExpiredAsync(_clock.UtcNow, TimeSpan.FromDays(1), default));
        Assert.Equal(2, await store.CountAsync(default));
    }

    [Fact]
    public async Task A_row_that_no_longer_deserializes_is_dropped_not_thrown()
    {
        using var store = Store();
        await store.SetAsync(new CacheEntry("k", "{not json", _clock.UtcNow, null, null), default);
        using var cache = new LayeredCache(store, _clock);
        Assert.Null(await cache.GetAsync<List<int>>("k", default));
        Assert.Equal(0, await store.CountAsync(default));
    }
}

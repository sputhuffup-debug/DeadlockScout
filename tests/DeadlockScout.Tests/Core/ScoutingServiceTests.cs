using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Scouting;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Tests.Core;

public class ScoutingServiceTests
{
    /// <summary>Minimal in-memory ICache for logic tests.</summary>
    public sealed class MemoryCacheStub : ICache
    {
        public readonly Dictionary<string, object?> Store = new();
        public Task<T?> GetAsync<T>(string key, CancellationToken ct) => Task.FromResult(Store.TryGetValue(key, out var v) ? (T?)v : default);
        public Task SetAsync<T>(string key, T value, TimeSpan? ttl, CancellationToken ct) { Store[key] = value; return Task.CompletedTask; }
        public async Task<T> GetOrCreateAsync<T>(string key, TimeSpan? ttl, Func<CancellationToken, Task<T>> factory, bool swr, CancellationToken ct)
        {
            if (Store.TryGetValue(key, out var v) && v is T t) return t;
            var created = await factory(ct);
            if (created is not null) Store[key] = created; // like LayeredCache: null is never stored
            return created;
        }
        public Task RemoveAsync(string key, CancellationToken ct) { Store.Remove(key); return Task.CompletedTask; }
    }

    private sealed class FakeApi : IDeadlockApi
    {
        public readonly List<uint[]> HeroStatsCalls = new();
        public Dictionary<uint, List<PlayerHeroStats>> Data = new();
        public List<SharedHistory> Enemies = new();
        public int EnemyCalls;

        public Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct)
        {
            var ids = accountIds.ToArray();
            HeroStatsCalls.Add(ids);
            return Task.FromResult<IReadOnlyList<PlayerHeroStats>>(ids.Where(Data.ContainsKey).SelectMany(i => Data[i]).ToList());
        }
        public Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) { EnemyCalls++; return Task.FromResult<IReadOnlyList<SharedHistory>>(Enemies); }
        public Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => Task.FromResult<IReadOnlyList<SharedHistory>>(new List<SharedHistory>());

        public Task<IReadOnlyList<PlayerRank>> RanksAsync(IEnumerable<uint> accountIds, CancellationToken ct) => Task.FromResult<IReadOnlyList<PlayerRank>>(new List<PlayerRank>());
        public Task<IReadOnlyList<MatchHistoryEntry>> MatchHistoryAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> LiveUrlAsync(long matchId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActiveMatch?> ActiveMatchAsync(long matchId, CancellationToken ct) => throw new NotSupportedException();
        public Task<MatchMetadata?> MatchMetadataAsync(long matchId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<MatchMetadata>> SampleMatchesAsync(int heroId, RankBracket bracket, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task SubmitSaltsAsync(IEnumerable<MatchSalts> salts, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AnalyticsHeroStats>> AnalyticsHeroStatsAsync(AnalyticsQuery q, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ItemBucketStat>> ItemStatsByNetWorthAsync(int heroId, RankBracket bracket, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AbilityOrderStat>> AbilityOrderStatsAsync(int heroId, RankBracket bracket, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<HeroMatchup>> HeroCounterStatsAsync(RankBracket bracket, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<HeroAsset>> HeroesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ItemAsset>> ItemsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<SteamInfo?> SteamInfoAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private static IOptionsMonitor<ScoutSettings> Options(ScoutSettings? s = null)
    {
        var settings = s ?? new ScoutSettings();
        return new StaticMonitor(settings);
    }

    private sealed class StaticMonitor(ScoutSettings v) : IOptionsMonitor<ScoutSettings>
    {
        public ScoutSettings CurrentValue => v;
        public ScoutSettings Get(string? name) => v;
        public IDisposable? OnChange(Action<ScoutSettings, string?> listener) => null;
    }

    private static Roster Lobby()
    {
        var players = new List<RosterPlayer>();
        for (uint i = 1; i <= 12; i++)
            players.Add(new RosterPlayer(100 + i, $"p{i}", i <= 6 ? Teams.Amber : Teams.Sapphire, (int)i, 50 + (int)i, (int)((i - 1) % 4) + 1));
        return new Roster(42, players, TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task A_lobby_of_strangers_costs_one_hero_stats_call_and_the_second_lobby_none()
    {
        var api = new FakeApi();
        var cache = new MemoryCacheStub();
        for (uint i = 1; i <= 12; i++)
            api.Data[100 + i] = new() { new PlayerHeroStats(100 + i, (int)i, 40, 20, 0, 0, 0), new PlayerHeroStats(100 + i, 99, 100, 50, 0, 0, 0) };
        var svc = new ScoutingService(api, cache, Options());
        var roster = Lobby();

        var rows = await svc.ScoutAsync(roster, 101, CancellationToken.None);
        Assert.Single(api.HeroStatsCalls);
        Assert.Equal(12, api.HeroStatsCalls[0].Length);
        Assert.Equal(12, rows.Count);
        Assert.All(rows, r => Assert.Equal(40, r.GamesOnHero));
        Assert.All(rows, r => Assert.Equal(140, r.TotalGames));
        Assert.Equal(1, api.EnemyCalls);

        await svc.ScoutAsync(roster, 101, CancellationToken.None);
        Assert.Single(api.HeroStatsCalls);
        Assert.Equal(1, api.EnemyCalls);
    }

    [Fact]
    public async Task Only_cache_misses_are_fetched()
    {
        var api = new FakeApi();
        var cache = new MemoryCacheStub();
        var roster = Lobby();
        foreach (var p in roster.Players.Take(10))
            cache.Store[DeadlockScout.Core.Caching.CacheKeys.PlayerHeroStats(p.AccountId)] = new List<PlayerHeroStats> { new(p.AccountId, p.HeroId, 10, 5, 0, 0, 0) };
        var svc = new ScoutingService(api, cache, Options());
        await svc.ScoutAsync(roster, 101, CancellationToken.None);
        Assert.Single(api.HeroStatsCalls);
        Assert.Equal(new uint[] { 111, 112 }, api.HeroStatsCalls[0]);
    }

    [Fact]
    public async Task Private_profiles_are_marked_and_cached_as_empty()
    {
        var api = new FakeApi();
        var cache = new MemoryCacheStub();
        var svc = new ScoutingService(api, cache, Options());
        var roster = Lobby();
        var rows = await svc.ScoutAsync(roster, 101, CancellationToken.None);
        Assert.All(rows.Where(r => r.Player.AccountId != 101), r => Assert.Equal("private", r.Flag));
        Assert.Null(rows.Single(r => r.Player.AccountId == 101).Flag);
        Assert.Equal(12, cache.Store.Keys.Count(k => k.StartsWith("phs:")));
    }

    [Fact]
    public async Task Met_before_comes_from_your_shared_history()
    {
        var api = new FakeApi();
        api.Enemies.Add(new SharedHistory(107, 3, 1, 2, AsEnemy: true));
        for (uint i = 1; i <= 12; i++) api.Data[100 + i] = new() { new PlayerHeroStats(100 + i, (int)i, 50, 25, 0, 0, 0) };
        var svc = new ScoutingService(api, new MemoryCacheStub(), Options());
        var rows = await svc.ScoutAsync(Lobby(), 101, CancellationToken.None);
        var met = rows.Single(r => r.Player.AccountId == 107);
        Assert.Equal(3, met.MetBefore);
        Assert.Equal((1, 2), met.MetRecord);
        Assert.Equal("met before", met.Flag);
        Assert.Equal(0, rows.Single(r => r.Player.AccountId == 108).MetBefore);
    }

    [Fact]
    public async Task Api_failure_degrades_to_unknown_rows_instead_of_throwing()
    {
        var api = new ThrowingApi();
        var svc = new ScoutingService(api, new MemoryCacheStub(), Options());
        var rows = await svc.ScoutAsync(Lobby(), 101, CancellationToken.None);
        Assert.Equal(12, rows.Count);
        Assert.All(rows.Where(r => r.Player.AccountId != 101), r => Assert.True(r.Flags.HasFlag(ScoutFlags.Private)));
    }

    private sealed class ThrowingApi : FakeApiBase
    {
        public override Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct) => throw new HttpRequestException("503");
        public override Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct) => throw new HttpRequestException("503");
        public override Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct) => throw new HttpRequestException("503");
    }

    public abstract class FakeApiBase : IDeadlockApi
    {
        public abstract Task<IReadOnlyList<PlayerHeroStats>> HeroStatsAsync(IEnumerable<uint> accountIds, CancellationToken ct);
        public abstract Task<IReadOnlyList<SharedHistory>> EnemyStatsAsync(uint accountId, CancellationToken ct);
        public abstract Task<IReadOnlyList<SharedHistory>> MateStatsAsync(uint accountId, CancellationToken ct);
        public virtual Task<IReadOnlyList<PlayerRank>> RanksAsync(IEnumerable<uint> accountIds, CancellationToken ct) => Task.FromResult<IReadOnlyList<PlayerRank>>(new List<PlayerRank>());
        public virtual Task<IReadOnlyList<MatchHistoryEntry>> MatchHistoryAsync(uint accountId, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<string> LiveUrlAsync(long matchId, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<ActiveMatch?> ActiveMatchAsync(long matchId, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<MatchMetadata?> MatchMetadataAsync(long matchId, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<IReadOnlyList<MatchMetadata>> SampleMatchesAsync(int heroId, RankBracket bracket, int limit, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task SubmitSaltsAsync(IEnumerable<MatchSalts> salts, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<IReadOnlyList<AnalyticsHeroStats>> AnalyticsHeroStatsAsync(AnalyticsQuery q, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<IReadOnlyList<ItemBucketStat>> ItemStatsByNetWorthAsync(int heroId, RankBracket bracket, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<IReadOnlyList<AbilityOrderStat>> AbilityOrderStatsAsync(int heroId, RankBracket bracket, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<IReadOnlyList<HeroMatchup>> HeroCounterStatsAsync(RankBracket bracket, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<IReadOnlyList<HeroAsset>> HeroesAsync(CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<IReadOnlyList<ItemAsset>> ItemsAsync(CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<SteamInfo?> SteamInfoAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}

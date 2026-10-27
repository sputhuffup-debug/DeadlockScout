using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;

namespace DeadlockScout.App.Services;

/// <summary>Hero and item names by id or internal class name, from the assets endpoints, cached a day and refreshed on an unknown id.</summary>
public sealed class AssetCatalog
{
    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly TimeSpan _ttl;
    private IReadOnlyList<HeroAsset> _heroes = Array.Empty<HeroAsset>();
    private IReadOnlyList<ItemAsset> _items = Array.Empty<ItemAsset>();
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    public AssetCatalog(IDeadlockApi api, ICache cache, TimeSpan ttl)
    {
        _api = api;
        _cache = cache;
        _ttl = ttl;
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            _heroes = await _cache.GetOrCreateAsync(CacheKeys.HeroAssets, _ttl, async c => (await _api.HeroesAsync(c)).ToList(), true, ct);
            _items = await _cache.GetOrCreateAsync(CacheKeys.ItemAssets, _ttl, async c => (await _api.ItemsAsync(c)).ToList(), true, ct);
            _lastRefresh = DateTimeOffset.UtcNow;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* names fall back to ids */ }
    }

    public string HeroName(int heroId)
    {
        var h = _heroes.FirstOrDefault(x => x.Id == heroId);
        if (h is null && heroId > 0) RefreshIfStale();
        return h?.Name ?? (heroId > 0 ? $"Hero {heroId}" : "?");
    }

    public int? HeroIdByClassName(string className)
    {
        var h = _heroes.FirstOrDefault(x => string.Equals(x.ClassName, className, StringComparison.OrdinalIgnoreCase));
        if (h is null) RefreshIfStale();
        return h?.Id;
    }

    public string ItemName(int itemId) => _items.FirstOrDefault(x => x.Id == itemId)?.Name ?? $"Item {itemId}";

    private void RefreshIfStale()
    {
        if (DateTimeOffset.UtcNow - _lastRefresh < TimeSpan.FromMinutes(10)) return;
        _lastRefresh = DateTimeOffset.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                await _cache.RemoveAsync(CacheKeys.HeroAssets, CancellationToken.None);
                await LoadAsync(CancellationToken.None);
            }
            catch { }
        });
    }
}

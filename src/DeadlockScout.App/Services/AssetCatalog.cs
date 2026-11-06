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

    public string ItemName(long itemId) => _items.FirstOrDefault(x => x.Id == itemId)?.Name ?? $"Item {itemId}";

    /// <summary>Shop items and abilities, empty until loaded.</summary>
    public IReadOnlyList<ItemAsset> Items => _items;

    /// <summary>Every hero the assets API lists (empty until loaded).</summary>
    public IReadOnlyList<HeroAsset> Heroes => _heroes;

    public int ItemCost(long itemId) => Item(itemId)?.Cost ?? 0;

    /// <summary>
    /// What the items with these ids cost (abilities and unknown ids count nothing): the part of net worth that is not
    /// unspent souls. Null until the item list has loaded, or without ids.
    /// </summary>
    public int? HeldCost(IReadOnlyList<long>? itemIds)
    {
        var items = _items;
        if (items.Count == 0 || itemIds is null) return null;
        int sum = 0;
        foreach (var id in itemIds)
            if (items.FirstOrDefault(x => x.Id == id) is { } item && item.Slot != "ability") sum += item.Cost;
        return sum;
    }

    /// <summary>Every shop item's price, empty until loaded.</summary>
    public IEnumerable<int> ShopPrices() => _items.Where(x => x.Slot != "ability" && x.Cost > 0).Select(x => x.Cost);

    public ItemAsset? Item(long itemId) => _items.FirstOrDefault(x => x.Id == itemId);

    public HeroAsset? Hero(int heroId) => _heroes.FirstOrDefault(x => x.Id == heroId);

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

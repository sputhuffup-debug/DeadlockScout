using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;

namespace DeadlockScout.App.Services;

/// <summary>
/// Item and hero icons (the overlay's spike chips, the match history), from the assets API's image bucket through <see cref="IDeadlockApi"/>, so
/// they share its rate limiter, and cached on disk for 30 days. <see cref="Get"/> never waits: it returns null until the
/// icon has loaded and the overlay's next refresh picks it up. Off in screenshot mode, because shots never show game art.
/// </summary>
public sealed class ItemIcons
{
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(30);
    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly AssetCatalog _assets;
    // Keyed by item id, or by -hero id for heroes (item ids are positive). null: loading, failed, or no image.
    private readonly ConcurrentDictionary<long, ImageSource?> _icons = new();
    private readonly ConcurrentDictionary<long, byte> _started = new();

    public ItemIcons(IDeadlockApi api, ICache cache, AssetCatalog assets)
    {
        _api = api;
        _cache = cache;
        _assets = assets;
    }

    /// <summary>False in screenshot mode: every chip then shows its placeholder.</summary>
    public bool Enabled { get; set; } = true;

    public ImageSource? Get(long itemId) => itemId <= 0 ? null : Lookup(itemId, () => _assets.Item(itemId)?.ImageUrl);

    public ImageSource? GetHero(int heroId) => heroId <= 0 ? null : Lookup(-heroId, () => _assets.Hero(heroId)?.ImageUrl);

    private ImageSource? Lookup(long key, Func<string?> url)
    {
        if (!Enabled) return null;
        if (_icons.TryGetValue(key, out var icon)) return icon;
        if (_started.TryAdd(key, 0)) _ = LoadAsync(key, url);
        return null;
    }

    private async Task LoadAsync(long key, Func<string?> imageUrl)
    {
        ImageSource? icon = null;
        try
        {
            if (imageUrl() is { Length: > 0 } url)
            {
                var bytes = await _cache.GetOrCreateAsync(CacheKeys.Image(url), Ttl, c => _api.ImageAsync(url, c)!, false, CancellationToken.None);
                if (bytes is { Length: > 0 }) icon = Decode(bytes);
            }
        }
        catch (Exception) { /* the chip keeps its placeholder */ }
        _icons[key] = icon;
    }

    private static ImageSource Decode(byte[] bytes)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 32;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze(); // decoded on a pool thread, drawn on the UI thread
        return image;
    }
}

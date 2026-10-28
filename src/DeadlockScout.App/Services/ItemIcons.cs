using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;

namespace DeadlockScout.App.Services;

/// <summary>
/// Item icons for the overlay's spike chips, from the assets API's image bucket through <see cref="IDeadlockApi"/>, so
/// they share its rate limiter, and cached on disk for 30 days. <see cref="Get"/> never waits: it returns null until the
/// icon has loaded and the overlay's next refresh picks it up. Off in screenshot mode, because shots never show game art.
/// </summary>
public sealed class ItemIcons
{
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(30);
    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly AssetCatalog _assets;
    private readonly ConcurrentDictionary<long, ImageSource?> _icons = new(); // null: loading, failed, or no image
    private readonly ConcurrentDictionary<long, byte> _started = new();

    public ItemIcons(IDeadlockApi api, ICache cache, AssetCatalog assets)
    {
        _api = api;
        _cache = cache;
        _assets = assets;
    }

    /// <summary>False in screenshot mode: every chip then shows its placeholder.</summary>
    public bool Enabled { get; set; } = true;

    public ImageSource? Get(long itemId)
    {
        if (!Enabled || itemId <= 0) return null;
        if (_icons.TryGetValue(itemId, out var icon)) return icon;
        if (_started.TryAdd(itemId, 0)) _ = LoadAsync(itemId);
        return null;
    }

    private async Task LoadAsync(long itemId)
    {
        ImageSource? icon = null;
        try
        {
            if (_assets.Item(itemId)?.ImageUrl is { Length: > 0 } url)
            {
                var bytes = await _cache.GetOrCreateAsync(CacheKeys.Image(url), Ttl, c => _api.ImageAsync(url, c)!, false, CancellationToken.None);
                if (bytes is { Length: > 0 }) icon = Decode(bytes);
            }
        }
        catch (Exception) { /* the chip keeps its placeholder */ }
        _icons[itemId] = icon;
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

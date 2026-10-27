using DeadlockScout.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockScout.Api.Caching;

/// <summary>
/// Patch key from the game's client version (assets/steam-info), so benchmark and item caches roll over on patch day.
/// Cached for an hour; falls back to the weekly date key when the API is unreachable.
/// </summary>
public sealed class ClientVersionPatchKeyProvider : IPatchKeyProvider
{
    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly IPatchKeyProvider _fallback;
    private readonly ILogger _log;

    public ClientVersionPatchKeyProvider(IDeadlockApi api, ICache cache, IPatchKeyProvider fallback, ILogger<ClientVersionPatchKeyProvider>? log = null)
    {
        _api = api;
        _cache = cache;
        _fallback = fallback;
        _log = log ?? NullLogger<ClientVersionPatchKeyProvider>.Instance;
    }

    public async ValueTask<string> GetAsync(CancellationToken ct)
    {
        try
        {
            var version = await _cache.GetOrCreateAsync("patch:client-version", TimeSpan.FromHours(1), async c =>
            {
                var info = await _api.SteamInfoAsync(c).ConfigureAwait(false);
                return info?.ClientVersion ?? 0;
            }, staleWhileRevalidate: true, ct).ConfigureAwait(false);
            if (version > 0) return $"v{version}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "steam-info unavailable; using the date patch key");
        }
        return await _fallback.GetAsync(ct).ConfigureAwait(false);
    }
}

using System.Net;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockScout.Api.Live;

/// <summary>
/// Finds a match's broadcast URL: <c>live/url</c> on the API once per match (cached), with retries while the API is
/// unreachable. Valve hands broadcast URLs only to a Steam client that spectates the match, which the API does for us.
/// The URL cannot be guessed: current relay addresses carry a per-broadcast token
/// (<c>http://dist1-ord1.steamcontent.com/tv/{match_id}_{token}</c>, seen Oct 27, 2026).
/// </summary>
public sealed class BroadcastUrlResolver
{
    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly ILogger _log;

    public BroadcastUrlResolver(IDeadlockApi api, ICache cache, ILogger<BroadcastUrlResolver>? log = null)
    {
        _api = api;
        _cache = cache;
        _log = log ?? NullLogger<BroadcastUrlResolver>.Instance;
    }

    /// <summary>Delays between attempts; replaceable in tests.</summary>
    public Func<int, TimeSpan> Backoff { get; init; } = attempt => TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(attempt, 4))));
    public int MaxAttempts { get; init; } = 4;

    public async Task<string?> ResolveAsync(long matchId, CancellationToken ct)
    {
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                return await _cache.GetOrCreateAsync(CacheKeys.LiveUrl(matchId), TimeSpan.FromHours(6),
                    c => _api.LiveUrlAsync(matchId, c), staleWhileRevalidate: false, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            {
                _log.LogWarning("Match {MatchId} has no live broadcast: {Message}", matchId, ex.Message);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "live/url for match {MatchId} failed (attempt {Attempt})", matchId, attempt + 1);
                if (attempt + 1 < MaxAttempts) await Task.Delay(Backoff(attempt + 1), ct).ConfigureAwait(false);
            }
        }
        return null;
    }
}

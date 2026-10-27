using System.Net;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockScout.Api.Live;

/// <summary>
/// Finds a match's broadcast URL: <c>live/url</c> on the API once per match (cached), with retries while the API is
/// unreachable. Valve hands broadcast URLs only to a Steam client that spectates the match, which the API does for us.
/// When the API cannot be reached at all, callers that read Valve's relay directly may try the conventional relay
/// address <c>http://dist1-ord1.steamcontent.com/tv/{match_id}</c>; that guess is unverified for arbitrary matches.
/// </summary>
public sealed class BroadcastUrlResolver
{
    public const string GuessedRelay = "http://dist1-ord1.steamcontent.com/tv/";

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

    public static string Guess(long matchId) => GuessedRelay + matchId;

    /// <param name="allowGuess">Return <see cref="Guess"/> when the API never answered (not when it said the match cannot be live).</param>
    public async Task<string?> ResolveAsync(long matchId, bool allowGuess, CancellationToken ct)
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
        if (!allowGuess) return null;
        _log.LogInformation("API unreachable; trying the conventional relay address for match {MatchId}", matchId);
        return Guess(matchId);
    }
}

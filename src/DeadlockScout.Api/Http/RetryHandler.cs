using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockScout.Api.Http;

/// <summary>
/// 429: honour a short Retry-After (up to <see cref="MaxRetryAfter"/>; a longer one returns the 429), else exponential
/// backoff capped at 5 minutes, up to <see cref="MaxRateLimitRetries"/> times.
/// 5xx and transport errors: 3 retries with jittered backoff. Never retries POSTs that are not idempotent (salts are).
/// <para>
/// A long Retry-After (a 429, or a 503 "Steam is busy") also holds back the next requests to the same endpoint until it
/// runs out (<see cref="Cooldowns"/>, 1.0.4): they get the 429 at once instead of spending the quota again. On Nov 24
/// the single-match metadata quota (3 an hour) answered "requests: 3, 4, 5" because the app kept asking.
/// </para>
/// </summary>
public sealed class RetryHandler : DelegatingHandler
{
    public int MaxRateLimitRetries { get; init; } = 4;
    public int MaxServerErrorRetries { get; init; } = 3;
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>
    /// A Retry-After longer than this is not waited out inside the request: the 429 goes back to the caller at once.
    /// (The single-match metadata endpoint answers 429 with Retry-After of about an hour when its Steam fallback is spent;
    /// waiting that long only ran into the client timeout.)
    /// </summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(20);
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
    public Random Random { get; init; } = Random.Shared;

    /// <summary>Endpoints told to wait, shared by every handler (HttpClientFactory replaces handlers every few minutes).</summary>
    public EndpointCooldowns Cooldowns { get; init; } = EndpointCooldowns.Shared;

    private readonly ILogger _log;

    public RetryHandler(ILogger<RetryHandler>? log = null) => _log = log ?? NullLogger<RetryHandler>.Instance;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Cooldowns.Remaining(request.RequestUri) is { } held)
        {
            _log.LogDebug("{Path} is held back for {Wait} after a long Retry-After; answering 429 without asking", request.RequestUri?.AbsolutePath, held);
            var local = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request, ReasonPhrase = "Held back after a long Retry-After" };
            local.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(held);
            return local;
        }
        int rateLimited = 0, serverErrors = 0;
        while (true)
        {
            HttpResponseMessage? response = null;
            Exception? transport = null;
            try
            {
                response = await base.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) { transport = ex; }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { transport = ex; } // HttpClient timeout

            if (response is { StatusCode: HttpStatusCode.TooManyRequests })
            {
                if (rateLimited++ >= MaxRateLimitRetries) return response;
                var retryAfter = RetryAfter(response);
                if (retryAfter > MaxRetryAfter)
                {
                    _log.LogInformation("429 from {Path} with Retry-After {Wait}; not waiting, and holding the endpoint back until then", request.RequestUri?.AbsolutePath, retryAfter);
                    Cooldowns.Hold(request.RequestUri, retryAfter!.Value);
                    return response;
                }
                var wait = retryAfter ?? Backoff(rateLimited);
                _log.LogInformation("429 from {Path}; waiting {Wait}", request.RequestUri?.AbsolutePath, wait);
                response.Dispose();
                await Delay(wait, ct).ConfigureAwait(false);
                continue;
            }

            // "Steam is busy" (503) comes with a Retry-After of half an hour or more: a quick retry cannot help, so it goes
            // back to the caller, like a long 429.
            if (response is { StatusCode: HttpStatusCode.ServiceUnavailable } && RetryAfter(response) > MaxRetryAfter)
            {
                _log.LogInformation("503 from {Path} with Retry-After {Wait}; not waiting", request.RequestUri?.AbsolutePath, RetryAfter(response));
                Cooldowns.Hold(request.RequestUri, RetryAfter(response)!.Value);
                return response;
            }

            bool serverError = response is { StatusCode: >= HttpStatusCode.InternalServerError } || transport is not null;
            if (serverError && serverErrors < MaxServerErrorRetries)
            {
                serverErrors++;
                var wait = Backoff(serverErrors);
                _log.LogInformation("{Status} from {Path}; retry {N} in {Wait}", response?.StatusCode.ToString() ?? transport!.GetType().Name,
                    request.RequestUri?.AbsolutePath, serverErrors, wait);
                response?.Dispose();
                await Delay(wait, ct).ConfigureAwait(false);
                continue;
            }

            if (transport is not null) throw transport;
            return response!;
        }
    }

    public static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } d) return d;
        if (ra.Date is { } when) return when - DateTimeOffset.UtcNow is var diff && diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
        return null;
    }

    private TimeSpan Backoff(int attempt)
    {
        double seconds = Math.Pow(2, attempt) + Random.NextDouble() * 0.5 * Math.Pow(2, attempt);
        var t = TimeSpan.FromSeconds(seconds);
        return t > MaxBackoff ? MaxBackoff : t;
    }
}

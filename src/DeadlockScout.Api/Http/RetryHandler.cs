using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockScout.Api.Http;

/// <summary>
/// 429: honour a short Retry-After (up to <see cref="MaxRetryAfter"/>; a longer one returns the 429), else exponential
/// backoff capped at 5 minutes, up to <see cref="MaxRateLimitRetries"/> times.
/// 5xx and transport errors: 3 retries with jittered backoff. Never retries POSTs that are not idempotent (salts are).
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

    private readonly ILogger _log;

    public RetryHandler(ILogger<RetryHandler>? log = null) => _log = log ?? NullLogger<RetryHandler>.Instance;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
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
                    _log.LogInformation("429 from {Path} with Retry-After {Wait}; not waiting", request.RequestUri?.AbsolutePath, retryAfter);
                    return response;
                }
                var wait = retryAfter ?? Backoff(rateLimited);
                _log.LogInformation("429 from {Path}; waiting {Wait}", request.RequestUri?.AbsolutePath, wait);
                response.Dispose();
                await Delay(wait, ct).ConfigureAwait(false);
                continue;
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

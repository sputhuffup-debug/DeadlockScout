using System.Threading.RateLimiting;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Api.Http;

/// <summary>
/// One token bucket per <see cref="EndpointFamily"/>, sized from settings and kept below the documented per-IP caps.
/// Requests queue (in order) rather than fail, so a burst at lobby time is smoothed instead of producing 429s.
/// Foreground requests may carry <see cref="PriorityOption"/> so background work never starves them.
/// </summary>
public sealed class RateLimitedHandler : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<RequestPriority> PriorityOption = new("DeadlockScout.Priority");

    private readonly RateLimiterRegistry _registry;
    private readonly Func<RequestPriority, CancellationToken, Task> _gate;
    private readonly ILogger _log;

    /// <param name="registry">Shared bucket state. Register it as a singleton: HttpClientFactory rebuilds handler chains every few minutes, and a bucket that restarted with them would forget what it had spent.</param>
    public RateLimitedHandler(RateLimiterRegistry registry, Func<RequestPriority, CancellationToken, Task>? gate = null, ILogger<RateLimitedHandler>? log = null)
    {
        _registry = registry;
        _gate = gate ?? ((_, _) => Task.CompletedTask);
        _log = log ?? NullLogger<RateLimitedHandler>.Instance;
    }

    /// <summary>Convenience for tests: private buckets from the given limits.</summary>
    public RateLimitedHandler(RateLimitSettings limits, Func<RequestPriority, CancellationToken, Task>? gate = null)
        : this(new RateLimiterRegistry(limits), gate) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var priority = request.Options.TryGetValue(PriorityOption, out var p) ? p : RequestPriority.Foreground;
        await _gate(priority, ct).ConfigureAwait(false);

        var family = EndpointFamilies.Classify(request.RequestUri!);
        var limiter = _registry.Get(family);
        var lease = limiter.AttemptAcquire(1);
        if (!lease.IsAcquired)
        {
            // Say so: a request held here sends nothing, so without this line a long wait (live/url's bucket refills
            // hourly) looks like the app silently doing nothing. The per-minute buckets queue for seconds at lobby time,
            // which is normal and only worth a debug line.
            lease.Dispose();
            _log.Log(family == EndpointFamily.LiveUrl ? LogLevel.Warning : LogLevel.Debug,
                "{Family} rate limit reached; {Path} waits for the next token", family, request.RequestUri!.AbsolutePath);
            lease = await limiter.AcquireAsync(1, ct).ConfigureAwait(false);
        }
        using var _ = lease;
        if (!lease.IsAcquired)
            throw new HttpRequestException($"Rate limiter queue for {family} is full; request dropped.", null, System.Net.HttpStatusCode.TooManyRequests);
        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }
}

/// <summary>Process-wide token buckets, one per endpoint family, sized from settings and kept below the documented per-IP caps.</summary>
public sealed class RateLimiterRegistry : IDisposable
{
    private readonly Dictionary<EndpointFamily, RateLimiter> _limiters;

    public RateLimiterRegistry(IOptionsMonitor<ScoutSettings> settings) : this(settings.CurrentValue.Api.Limits) { }

    public RateLimiterRegistry(RateLimitSettings limits)
    {
        _limiters = new()
        {
            [EndpointFamily.MatchHistory] = PerMinute(limits.MatchHistoryPerMin),
            [EndpointFamily.HeroStats] = PerMinute(limits.HeroStatsPerMin),
            [EndpointFamily.Metadata] = PerMinute(limits.MetadataPerMin),
            [EndpointFamily.Analytics] = PerMinute(limits.AnalyticsPerMin),
            [EndpointFamily.LiveUrl] = new TokenBucketRateLimiter(new()
            {
                TokenLimit = Math.Max(1, limits.LiveUrlPerHour),
                TokensPerPeriod = Math.Max(1, limits.LiveUrlPerHour),
                ReplenishmentPeriod = TimeSpan.FromHours(1),
                QueueLimit = 4,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            }),
            [EndpointFamily.LiveQuery] = PerMinute(limits.LiveQueryPerMin),
            [EndpointFamily.Assets] = PerMinute(10),
            [EndpointFamily.Default] = PerMinute(limits.DefaultPerMin),
        };
    }

    public RateLimiter Get(EndpointFamily family) => _limiters[family];

    /// <summary>Smooth bucket: the per-minute quota is spread over the minute (one token every 60/n seconds, burst of n/4).</summary>
    private static RateLimiter PerMinute(int perMinute)
    {
        perMinute = Math.Max(1, perMinute);
        int burst = Math.Max(1, perMinute / 4);
        return new TokenBucketRateLimiter(new()
        {
            TokenLimit = burst,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromSeconds(60.0 / perMinute),
            QueueLimit = 1000,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    }

    public void Dispose() { foreach (var l in _limiters.Values) l.Dispose(); }
}

public enum RequestPriority { Foreground, Background }

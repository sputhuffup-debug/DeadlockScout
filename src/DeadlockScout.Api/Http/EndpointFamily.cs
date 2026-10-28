namespace DeadlockScout.Api.Http;

/// <summary>Groups of endpoints that share one token bucket, matching the quotas in <see cref="Core.Settings.RateLimitSettings"/>.</summary>
public enum EndpointFamily
{
    Default,
    MatchHistory,
    HeroStats,
    Metadata,
    Analytics,
    LiveUrl,
    LiveQuery,
    Assets,
}

public static class EndpointFamilies
{
    /// <summary>Classifies a request path. Pure, so it is unit tested.</summary>
    public static EndpointFamily Classify(Uri uri)
    {
        var host = uri.Host;
        var path = uri.AbsolutePath;
        if (host.StartsWith("assets.", StringComparison.OrdinalIgnoreCase) || host.StartsWith("assets-bucket.", StringComparison.OrdinalIgnoreCase))
            return EndpointFamily.Assets;
        if (path.Contains("/match-history", StringComparison.OrdinalIgnoreCase)) return EndpointFamily.MatchHistory;
        if (path.Contains("/players/hero-stats", StringComparison.OrdinalIgnoreCase)) return EndpointFamily.HeroStats;
        if (path.Contains("/live/url", StringComparison.OrdinalIgnoreCase)) return EndpointFamily.LiveUrl;
        if (path.Contains("/demo/live/query", StringComparison.OrdinalIgnoreCase)) return EndpointFamily.LiveQuery;
        if (path.Contains("/metadata", StringComparison.OrdinalIgnoreCase)) return EndpointFamily.Metadata;
        if (path.Contains("/analytics/", StringComparison.OrdinalIgnoreCase)) return EndpointFamily.Analytics;
        return EndpointFamily.Default;
    }
}

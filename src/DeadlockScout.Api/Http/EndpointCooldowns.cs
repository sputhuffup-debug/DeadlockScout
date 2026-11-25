using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DeadlockScout.Api.Http;

/// <summary>
/// Endpoints the API told to wait (a long Retry-After), by path with its ids folded ("/v1/matches/{id}/metadata"), until
/// when. The single-match metadata quota is per IP across every match id, so one match's 429 holds all of them.
/// </summary>
public sealed partial class EndpointCooldowns
{
    public static readonly EndpointCooldowns Shared = new();

    private readonly ConcurrentDictionary<string, DateTimeOffset> _until = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _now;

    public EndpointCooldowns(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>The endpoint a request goes to, ids folded: "/v1/matches/{id}/metadata".</summary>
    public static string Key(Uri? uri) => uri is null ? "" : Ids().Replace(uri.AbsolutePath, "/{id}");

    public void Hold(Uri? uri, TimeSpan wait)
    {
        if (uri is null || wait <= TimeSpan.Zero) return;
        var until = _now() + wait;
        _until.AddOrUpdate(Key(uri), until, (_, old) => old > until ? old : until);
    }

    /// <summary>How long the endpoint is still held back; null when it may be asked.</summary>
    public TimeSpan? Remaining(Uri? uri)
    {
        if (uri is null || !_until.TryGetValue(Key(uri), out var until)) return null;
        var left = until - _now();
        if (left > TimeSpan.Zero) return left;
        _until.TryRemove(Key(uri), out _);
        return null;
    }

    [GeneratedRegex(@"/\d+(?=/|$)")]
    private static partial Regex Ids();
}

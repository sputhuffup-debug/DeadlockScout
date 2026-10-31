using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Api.Http;

/// <summary>
/// Adds the optional API key as X-API-KEY, never in the URL (so it is not logged), and only to deadlock-api.com's own
/// hosts or the API address set in Settings: never to another host, whatever the request is for. Not to the image
/// bucket either: it serves static files and has no use for the key. Redirects are followed by
/// <see cref="RedirectHandler"/> above this handler, so each hop is checked here again rather than carrying the header
/// along (.NET's own redirects keep custom headers on a cross-host hop).
/// </summary>
public sealed class ApiKeyHandler : DelegatingHandler
{
    public const string Header = "X-API-KEY";
    private const string ApiDomain = "deadlock-api.com";

    private readonly Func<string?> _key;
    private readonly Func<string?> _baseUrl;

    public ApiKeyHandler(IOptionsMonitor<ScoutSettings> settings)
        : this(() => settings.CurrentValue.Api.ApiKey, () => settings.CurrentValue.Api.BaseUrl) { }

    public ApiKeyHandler(Func<string?> key, Func<string?>? baseUrl = null)
    {
        _key = key;
        _baseUrl = baseUrl ?? (() => null);
    }

    /// <summary>True when <paramref name="uri"/> may receive the key: deadlock-api.com or a subdomain over HTTPS
    /// (the image bucket aside), or exactly the scheme, host and port of the configured API address.</summary>
    public static bool MayCarryKey(Uri? uri, string? baseUrl)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var configured)
            && string.Equals(uri.Scheme, configured.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, configured.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == configured.Port
            && !IsImageBucket(uri))
            return true;
        if (uri.Scheme != Uri.UriSchemeHttps || IsImageBucket(uri)) return false;
        var host = uri.Host;
        return host.Equals(ApiDomain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + ApiDomain, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImageBucket(Uri uri) => uri.Host.StartsWith("assets-bucket.", StringComparison.OrdinalIgnoreCase);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = _key();
        // A header someone set by hand is dropped too when the host may not have it.
        if (!MayCarryKey(request.RequestUri, _baseUrl())) request.Headers.Remove(Header);
        else if (!string.IsNullOrWhiteSpace(key) && !request.Headers.Contains(Header))
            request.Headers.TryAddWithoutValidation(Header, key);
        if (!request.Headers.UserAgent.Any())
            request.Headers.UserAgent.ParseAdd("DeadlockScout/0.1 (+https://github.com/sputhuffup-debug/DeadlockScout)");
        return base.SendAsync(request, ct);
    }
}

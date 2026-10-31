using System.Net;

namespace DeadlockScout.Api.Http;

/// <summary>
/// Follows redirects itself, outermost in the deadlock-api pipeline whose primary handler has automatic redirects off.
/// Each hop is a new request through the whole pipeline, so <see cref="ApiKeyHandler"/> decides again for the new
/// host (the key never rides a redirect to someone else), and the rate limiter and diagnostics see every hop.
/// Never follows HTTPS to plain HTTP, never replays a request body, and gives up after <see cref="MaxRedirects"/>.
/// </summary>
public sealed class RedirectHandler : DelegatingHandler
{
    public const int MaxRedirects = 5;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        for (int hop = 0; hop < MaxRedirects; hop++)
        {
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location || request.RequestUri is null)
                return response;
            var target = location.IsAbsoluteUri ? location : new Uri(request.RequestUri, location);
            if (request.RequestUri.Scheme == Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttps) return response;
            if (target.Scheme != Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttp) return response;

            // 303, and 301/302 after a POST, become a GET without a body (what browsers and .NET do).
            bool toGet = response.StatusCode == HttpStatusCode.SeeOther
                || (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found && request.Method == HttpMethod.Post);
            if (!toGet && request.Content is not null) return response;

            var next = new HttpRequestMessage(toGet ? HttpMethod.Get : request.Method, target) { Version = request.Version };
            foreach (var header in request.Headers)
                if (!header.Key.Equals(ApiKeyHandler.Header, StringComparison.OrdinalIgnoreCase))
                    next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            var options = (IDictionary<string, object?>)next.Options;
            foreach (var option in request.Options) options[option.Key] = option.Value;

            response.Dispose();
            request = next;
            response = await base.SendAsync(next, ct).ConfigureAwait(false);
        }
        return response;
    }

    private static bool IsRedirect(HttpStatusCode code) => code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}

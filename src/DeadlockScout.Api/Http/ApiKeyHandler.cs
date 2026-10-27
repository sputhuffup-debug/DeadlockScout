using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Api.Http;

/// <summary>Adds the optional API key as X-API-KEY, never in the URL (so it is not logged).</summary>
public sealed class ApiKeyHandler : DelegatingHandler
{
    private readonly Func<string?> _key;

    public ApiKeyHandler(IOptionsMonitor<ScoutSettings> settings) : this(() => settings.CurrentValue.Api.ApiKey) { }
    public ApiKeyHandler(Func<string?> key) => _key = key;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = _key();
        if (!string.IsNullOrWhiteSpace(key) && !request.Headers.Contains("X-API-KEY"))
            request.Headers.TryAddWithoutValidation("X-API-KEY", key);
        if (!request.Headers.UserAgent.Any())
            request.Headers.UserAgent.ParseAdd("DeadlockScout/0.1 (+https://github.com/sputhuffup-debug/DeadlockScout)");
        return base.SendAsync(request, ct);
    }
}

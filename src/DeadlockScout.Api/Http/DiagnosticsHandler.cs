using System.Diagnostics;
using DeadlockScout.Core.Diagnostics;

namespace DeadlockScout.Api.Http;

/// <summary>
/// Innermost in the chain, so it sees every attempt that goes over the wire (each retry too): records its endpoint
/// family, status or transport error and time in the <see cref="DiagnosticsLog"/>. Paths are stripped of player ids
/// and queries there.
/// </summary>
public sealed class DiagnosticsHandler : DelegatingHandler
{
    private readonly DiagnosticsLog _log;

    public DiagnosticsHandler(DiagnosticsLog log) => _log = log;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var family = EndpointFamilies.Classify(request.RequestUri!).ToString();
        var path = request.RequestUri!.AbsolutePath;
        var watch = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            _log.ApiAttempt(family, path, (int)response.StatusCode, null, watch.Elapsed, DateTimeOffset.UtcNow);
            return response;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.ApiAttempt(family, path, null, ex is TaskCanceledException ? "timed out" : ex.GetType().Name, watch.Elapsed, DateTimeOffset.UtcNow);
            throw;
        }
    }
}

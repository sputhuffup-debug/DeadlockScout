using System.Runtime.CompilerServices;
using DeadlockScout.Api.Http;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Api.Live;

/// <summary>
/// Reads <c>GET {liveUrl}/v1/matches/{match_id}/live/demo/events?subscribed_entities=player_controller</c> from the
/// deadlock-api live-events service (self-hosted by default at http://localhost:3000). Reconnects with backoff while
/// the match is live; stops on the service's terminal <c>end</c> event.
/// </summary>
public sealed class SseLiveEventStream : ILiveEventStream
{
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly ILogger _log;

    public SseLiveEventStream(HttpClient http, IOptionsMonitor<ScoutSettings> settings, ILogger<SseLiveEventStream>? log = null)
    {
        _http = http;
        _settings = settings;
        _log = log ?? NullLogger<SseLiveEventStream>.Instance;
    }

    public static string EventsUrl(string liveBase, long matchId)
        => $"{liveBase.TrimEnd('/')}/v1/matches/{matchId}/live/demo/events?subscribed_entities=player_controller";

    public async IAsyncEnumerable<LiveEventDto> ReadAsync(long matchId, [EnumeratorCancellation] CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var url = EventsUrl(_settings.CurrentValue.Api.LiveUrl, matchId);
            HttpResponseMessage? response = null;
            Stream? stream = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.ParseAdd("text/event-stream");
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                response?.Dispose();
                attempt++;
                var wait = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempt, 6))));
                _log.LogWarning(ex, "Live events connect failed ({Attempt}); retry in {Wait}", attempt, wait);
                await Task.Delay(wait, ct).ConfigureAwait(false);
                continue;
            }

            bool ended = false;
            using (response)
            await using (stream)
            {
                attempt = 0;
                await foreach (var sse in SseReader.ReadAsync(stream, ct).ConfigureAwait(false))
                {
                    var dto = LiveEventDto.TryParse(sse.Data, sse.Event);
                    if (dto is null) continue;
                    if (dto.IsEnd) { ended = true; yield return dto; break; }
                    yield return dto;
                }
            }
            if (ended) yield break;
            // Stream closed without an end event: reconnect after a short pause.
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }
}

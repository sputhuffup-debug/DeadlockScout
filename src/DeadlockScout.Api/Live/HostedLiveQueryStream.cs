using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using DeadlockScout.Api.Http;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Api.Live;

/// <summary>
/// The live feed with nothing to install: two long-lived <c>GET /v1/matches/demo/live/query</c> streams on
/// api.deadlock-api.com over the match's broadcast, one for the player controllers and one for the game-rules clock,
/// translated by <see cref="LiveQueryTranslator"/> into the events <see cref="LiveMatchFeed"/> already understands.
/// The broadcast URL comes from <c>live/url</c> once per match (cached). A dropped stream reconnects with backoff; a
/// 4xx error (bad query, unknown match) stops it, since retrying cannot help.
/// </summary>
public sealed class HostedLiveQueryStream : ILiveEventStream
{
    private readonly HttpClient _http;
    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly ILogger _log;

    public HostedLiveQueryStream(HttpClient http, IDeadlockApi api, ICache cache, IOptionsMonitor<ScoutSettings> settings, ILogger<HostedLiveQueryStream>? log = null)
    {
        _http = http;
        _api = api;
        _cache = cache;
        _settings = settings;
        _log = log ?? NullLogger<HostedLiveQueryStream>.Instance;
    }

    /// <summary>Delays between reconnect attempts; replaceable in tests.</summary>
    public Func<int, TimeSpan> Backoff { get; init; } = attempt => TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(attempt, 4))));
    public int MaxAttempts { get; init; } = 8;

    public static string QueryUrl(string apiBase, string broadcastUrl, string sql)
        => $"{apiBase.TrimEnd('/')}/v1/matches/demo/live/query?broadcast_url={Uri.EscapeDataString(broadcastUrl)}&query={Uri.EscapeDataString(sql)}";

    private enum Source { Controllers, Rules }
    private sealed record Item(Source Source, JsonElement? Row, bool Ended, bool Fatal);

    public async IAsyncEnumerable<LiveEventDto> ReadAsync(long matchId, [EnumeratorCancellation] CancellationToken ct)
    {
        var broadcastUrl = await ResolveBroadcastUrlAsync(matchId, ct).ConfigureAwait(false);
        if (broadcastUrl is null) { yield return new LiveEventDto { EventType = "end" }; yield break; }

        var cfg = _settings.CurrentValue;
        var translator = new LiveQueryTranslator(cfg.Api.LiveTickRate);
        var channel = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var rules = PumpAsync(Source.Rules, QueryUrl(cfg.Api.BaseUrl, broadcastUrl, LiveQueryTranslator.RulesQuery), channel.Writer, cts.Token);
        var controllers = PumpAsync(Source.Controllers, QueryUrl(cfg.Api.BaseUrl, broadcastUrl, LiveQueryTranslator.ControllerQuery), channel.Writer, cts.Token);
        _ = Task.WhenAll(rules, controllers).ContinueWith(_ => channel.Writer.TryComplete(), TaskScheduler.Default);

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (item.Source == Source.Rules)
                {
                    if (item.Row is { } r) translator.OnRulesRow(r);
                    continue;
                }
                if (item.Ended)
                {
                    foreach (var e in translator.OnEnd()) yield return e;
                    yield break;
                }
                if (item.Row is { } row)
                    foreach (var e in translator.OnControllerRow(row)) yield return e;
            }
            // Both pumps gave up without a clean end.
            foreach (var e in translator.OnEnd()) yield return e;
        }
        finally
        {
            cts.Cancel();
        }
    }

    private async Task<string?> ResolveBroadcastUrlAsync(long matchId, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return await _cache.GetOrCreateAsync(CacheKeys.LiveUrl(matchId), TimeSpan.FromHours(6),
                    c => _api.LiveUrlAsync(matchId, c), staleWhileRevalidate: false, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            {
                _log.LogWarning("Match {MatchId} has no live broadcast: {Message}", matchId, ex.Message);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "live/url for match {MatchId} failed (attempt {Attempt})", matchId, attempt + 1);
                await Task.Delay(Backoff(attempt + 1), ct).ConfigureAwait(false);
            }
        }
        return null;
    }

    private async Task PumpAsync(Source source, string url, ChannelWriter<Item> writer, CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested && attempt < MaxAttempts)
        {
            bool fatal = false, cleanEnd = false;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.ParseAdd("text/event-stream");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    _log.LogError("Live query ({Source}) rejected with {Status}: {Body}", source, (int)response.StatusCode, body.Length > 300 ? body[..300] : body);
                    fatal = true;
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    bool sawError = false;
                    await foreach (var sse in SseReader.ReadAsync(stream, ct).ConfigureAwait(false))
                    {
                        switch (sse.Event)
                        {
                            case null or "message":
                                attempt = 0;
                                if (TryParse(sse.Data) is { } row) await writer.WriteAsync(new Item(source, row, false, false), ct).ConfigureAwait(false);
                                break;
                            case "status":
                                _log.LogDebug("Live query ({Source}) status: {Data}", source, sse.Data);
                                break;
                            case "error":
                                sawError = true;
                                int status = ErrorStatus(sse.Data);
                                _log.LogWarning("Live query ({Source}) error: {Data}", source, sse.Data);
                                // 4xx: the query or match is wrong; 408: the two-hour cap. Retrying will not help either.
                                if (status is >= 400 and < 500 && status != 429) fatal = true;
                                break;
                            case "end":
                                cleanEnd = !sawError;
                                break;
                        }
                        if (cleanEnd || fatal) break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Live query ({Source}) dropped", source);
            }

            if (cleanEnd || fatal)
            {
                await writer.WriteAsync(new Item(source, null, true, fatal), ct).ConfigureAwait(false);
                return;
            }
            attempt++;
            await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
        }
        if (!ct.IsCancellationRequested)
            await writer.WriteAsync(new Item(source, null, true, true), ct).ConfigureAwait(false);
    }

    private static JsonElement? TryParse(string data)
    {
        if (string.IsNullOrWhiteSpace(data) || data[0] != '{') return null;
        try { using var doc = JsonDocument.Parse(data); return doc.RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    private static int ErrorStatus(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            return doc.RootElement.TryGetProperty("status", out var s) && s.TryGetInt32(out var n) ? n : 500;
        }
        catch (JsonException) { return 500; }
    }
}

/// <summary>Picks the hosted or self-hosted feed from settings each time a match starts.</summary>
public sealed class LiveEventStreamSelector : ILiveEventStream
{
    private readonly HostedLiveQueryStream _hosted;
    private readonly SseLiveEventStream _selfHosted;
    private readonly IOptionsMonitor<ScoutSettings> _settings;

    public LiveEventStreamSelector(HostedLiveQueryStream hosted, SseLiveEventStream selfHosted, IOptionsMonitor<ScoutSettings> settings)
    {
        _hosted = hosted;
        _selfHosted = selfHosted;
        _settings = settings;
    }

    public IAsyncEnumerable<LiveEventDto> ReadAsync(long matchId, CancellationToken ct)
        => _settings.CurrentValue.Api.LiveSource == LiveSource.SelfHosted ? _selfHosted.ReadAsync(matchId, ct) : _hosted.ReadAsync(matchId, ct);
}

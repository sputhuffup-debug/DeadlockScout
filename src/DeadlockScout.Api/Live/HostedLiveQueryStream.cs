using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using DeadlockScout.Api.Http;
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
    private readonly BroadcastUrlResolver _resolver;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly ILogger _log;

    public HostedLiveQueryStream(HttpClient http, BroadcastUrlResolver resolver, IOptionsMonitor<ScoutSettings> settings, ILogger<HostedLiveQueryStream>? log = null)
    {
        _http = http;
        _resolver = resolver;
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
        var broadcastUrl = await _resolver.ResolveAsync(matchId, allowGuess: false, ct).ConfigureAwait(false);
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

/// <summary>
/// Picks the live feed from settings each time a match starts. With <see cref="LiveSource.Direct"/>, a broadcast the
/// parser cannot decode switches to the hosted feed for the rest of the match (unless that fallback is turned off).
/// </summary>
public sealed class LiveEventStreamSelector : ILiveEventStream
{
    private readonly ILiveEventStream _hosted;
    private readonly ILiveEventStream _selfHosted;
    private readonly ILiveEventStream _direct;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly ILogger _log;

    public LiveEventStreamSelector(ILiveEventStream hosted, ILiveEventStream selfHosted, ILiveEventStream direct,
                                   IOptionsMonitor<ScoutSettings> settings, ILogger<LiveEventStreamSelector>? log = null)
    {
        _hosted = hosted;
        _selfHosted = selfHosted;
        _direct = direct;
        _settings = settings;
        _log = log ?? NullLogger<LiveEventStreamSelector>.Instance;
    }

    public IAsyncEnumerable<LiveEventDto> ReadAsync(long matchId, CancellationToken ct) => _settings.CurrentValue.Api.LiveSource switch
    {
        LiveSource.SelfHosted => _selfHosted.ReadAsync(matchId, ct),
        LiveSource.Direct => DirectWithFallbackAsync(matchId, ct),
        _ => _hosted.ReadAsync(matchId, ct),
    };

    private async IAsyncEnumerable<LiveEventDto> DirectWithFallbackAsync(long matchId, [EnumeratorCancellation] CancellationToken ct)
    {
        BroadcastParseException? failed = null;
        await using (var direct = _direct.ReadAsync(matchId, ct).GetAsyncEnumerator(ct))
        {
            while (true)
            {
                try
                {
                    if (!await direct.MoveNextAsync().ConfigureAwait(false)) yield break;
                }
                catch (BroadcastParseException ex) { failed = ex; break; }
                yield return direct.Current;
            }
        }
        if (!_settings.CurrentValue.Api.DirectFallbackToHosted) throw failed;
        _log.LogWarning(failed, "Direct broadcast failed for match {MatchId}; switching to the hosted feed", matchId);
        await foreach (var e in _hosted.ReadAsync(matchId, ct).ConfigureAwait(false)) yield return e;
    }
}

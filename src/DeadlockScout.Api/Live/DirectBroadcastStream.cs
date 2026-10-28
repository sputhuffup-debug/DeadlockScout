using System.Net;
using System.Runtime.CompilerServices;
using DeadlockScout.Core.Settings;
using DemoFile;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Api.Live;

/// <summary>The broadcast could not be decoded (usually a game patch the parser library does not know yet).</summary>
public sealed class BroadcastParseException(string message, Exception inner) : Exception(message, inner);

/// <summary>The relay never served the broadcast (refused or missing for longer than the start-up patience).</summary>
public sealed class BroadcastUnavailableException(string message, Exception? inner) : Exception(message, inner);

/// <summary>
/// Reads one parser (broadcast or demo file) and snapshots the players about once a second of game time. Shared by the
/// live stream and the replay check, so a replay exercises exactly the code a live match does.
/// </summary>
public sealed class BroadcastSnapshotter
{
    private readonly DeadlockDemoParser _demo;
    private double _tickInterval = 1.0 / 64;
    private long _lastEmitTick = long.MinValue;

    public BroadcastSnapshotter(DeadlockDemoParser demo, int fallbackTickRate = 64)
    {
        _demo = demo;
        if (fallbackTickRate > 0) _tickInterval = 1.0 / fallbackTickRate;
        demo.PacketEvents.SvcServerInfo += e => { if (e.TickInterval > 0) _tickInterval = e.TickInterval; };
    }

    public double TickInterval => _tickInterval;

    /// <summary>Events for the current state when at least a second of server time has passed since the last snapshot; otherwise none.</summary>
    public IReadOnlyList<LiveEventDto> MaybeSnapshot(bool force = false)
    {
        long tick = _demo.CurrentGameTick.Value;
        if (tick <= 0) return Array.Empty<LiveEventDto>();
        if (!force && _lastEmitTick != long.MinValue && (tick - _lastEmitTick) * _tickInterval < 1.0) return Array.Empty<LiveEventDto>();
        _lastEmitTick = tick;
        var clock = ReadClock(tick);
        return BroadcastEventMapper.Snapshot(ReadPlayers(), tick, BroadcastClock.GameTimeSeconds(clock)).ToList();
    }

    public ClockState ReadClock(long tick)
    {
        try
        {
            var rules = _demo.GameRules;
            if (rules is null) return new ClockState(tick, _tickInterval, 0, 0, 0, false);
            return new ClockState(tick, _tickInterval, rules.GameStartTime.Value, rules.MatchClockUpdateTick, rules.MatchClockAtLastUpdate, rules.ServerPaused);
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
        {
            return new ClockState(tick, _tickInterval, 0, 0, 0, false); // game rules entity not received yet
        }
    }

    public List<PlayerState> ReadPlayers()
    {
        var list = new List<PlayerState>(12);
        foreach (var c in _demo.PlayersIncludingDisconnected)
        {
            var d = c.PlayerDataGlobal;
            list.Add(new PlayerState((int)c.EntityIndex.Value, c.SteamID, c.PlayerName, c.TeamNum, d.HeroID.Value, c.AssignedLane,
                d.GoldNetWorth, d.PlayerKills, d.Deaths, d.PlayerAssists,
                // m_vecUpgrades: the items the player holds, as string tokens (MurmurHash2 of the class name), which is
                // exactly the assets API's item id. Checked against a recorded broadcast, Oct 2026.
                d.Upgrades.Select(u => (long)u.Value).ToArray(), d.UltimateTrained));
        }
        return list;
    }
}

/// <summary>
/// The live feed with no third-party service in the data path: the app reads Valve's HTTP broadcast relay itself with
/// DemoFile and snapshots the players once a second. Only the broadcast URL comes from the API (<see cref="BroadcastUrlResolver"/>).
/// Network drops reconnect from the relay's current fragment; a decoding failure throws <see cref="BroadcastParseException"/>
/// and a relay that never serves the match throws <see cref="BroadcastUnavailableException"/>, so
/// <see cref="LiveEventStreamSelector"/> can fall back to the hosted feed.
/// </summary>
public sealed class DirectBroadcastStream : ILiveEventStream
{
    private readonly BroadcastUrlResolver _resolver;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly ILogger _log;

    public DirectBroadcastStream(BroadcastUrlResolver resolver, IOptionsMonitor<ScoutSettings> settings, ILogger<DirectBroadcastStream>? log = null)
    {
        _resolver = resolver;
        _settings = settings;
        _log = log ?? NullLogger<DirectBroadcastStream>.Instance;
    }

    public int MaxReconnects { get; init; } = 5;

    /// <summary>
    /// How long to keep asking a relay that has served nothing yet. The lobby line comes over a minute before the game
    /// starts and the broadcast runs behind it, so the first minutes can be refusals; after this the hosted feed takes over.
    /// </summary>
    public TimeSpan NotStartedPatience { get; init; } = TimeSpan.FromMinutes(6);
    public TimeSpan NotStartedRetry { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Replaceable in tests: one connection to the relay, and the waits between them.</summary>
    public Func<string, CancellationToken, IAsyncEnumerable<LiveEventDto>>? Session { get; init; }
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// The relay over HTTPS, the scheme every verified read used; live/url hands out <c>http://</c>. (A 405 from the
    /// relay means the broadcast has not started yet, on either scheme; see <see cref="NotStartedPatience"/>.)
    /// </summary>
    public static string RelayUrl(string url)
        => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url["http://".Length..] : url;

    public async IAsyncEnumerable<LiveEventDto> ReadAsync(long matchId, [EnumeratorCancellation] CancellationToken ct)
    {
        var resolved = await _resolver.ResolveAsync(matchId, ct).ConfigureAwait(false);
        if (resolved is null) { yield return new LiveEventDto { EventType = "end" }; yield break; }
        var url = RelayUrl(resolved);
        var session = Session ?? SessionAsync;

        var waitingSince = Now();
        bool everGotData = false;
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            bool finished = false, gotData = false;
            Exception? failure = null;
            await using (var e = session(url, ct).GetAsyncEnumerator(ct))
            {
                while (true)
                {
                    try
                    {
                        if (!await e.MoveNextAsync().ConfigureAwait(false)) { finished = true; break; }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { yield break; }
                    catch (Exception ex) { failure = ex; break; }
                    gotData = everGotData = true;
                    yield return e.Current;
                }
            }

            if (finished)
            {
                yield return new LiveEventDto { EventType = "end" };
                yield break;
            }
            if (failure is not null && !IsNetwork(failure))
                throw new BroadcastParseException($"Could not decode the broadcast for match {matchId}: {failure.Message}", failure);

            if (!everGotData)
            {
                if (Now() - waitingSince >= NotStartedPatience)
                    throw new BroadcastUnavailableException($"The broadcast relay served nothing for match {matchId} in {NotStartedPatience.TotalMinutes:0} minutes: {failure?.Message}", failure);
                _log.LogInformation("Broadcast for match {MatchId} not available yet ({Reason}); asking again in {Seconds} s",
                    matchId, failure?.Message, NotStartedRetry.TotalSeconds);
                await Delay(NotStartedRetry, ct).ConfigureAwait(false);
                continue;
            }

            if (gotData) attempt = 0;
            if (++attempt > MaxReconnects) break;
            _log.LogWarning(failure, "Broadcast connection for match {MatchId} dropped; reconnecting (attempt {Attempt})", matchId, attempt);
            await Delay(TimeSpan.FromSeconds(Math.Min(30, 3 * attempt)), ct).ConfigureAwait(false);
        }
        yield return new LiveEventDto { EventType = "end" };
    }

    private async IAsyncEnumerable<LiveEventDto> SessionAsync(string url, [EnumeratorCancellation] CancellationToken ct)
    {
        var demo = new DeadlockDemoParser();
        var snap = new BroadcastSnapshotter(demo, _settings.CurrentValue.Api.LiveTickRate);
        // Deadlock publishes a fragment every keyframe_interval (3 s); DemoFile's default retries a missing one every second,
        // so two of every three requests were 404s. Retrying at the publish cadence halves the requests and loses nothing:
        // the data is a minute behind anyway. Catching up at the start stays at one request a second (DeltaRequestInterval).
        var handler = new RetryingBroadcastHttpHandler(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip })
        {
            RetryInterval = FragmentRetryInterval,
            MaxRetries = 10, // 30 s without a new fragment before the reconnect logic takes over
        };
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(url.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DeadlockScout/0.1");
        var reader = HttpBroadcastReader.Create(demo, http);
        await reader.StartReadingAsync(ct).ConfigureAwait(false);
        while (await reader.MoveNextAsync(ct).ConfigureAwait(false))
            foreach (var e in snap.MaybeSnapshot()) yield return e;
        foreach (var e in snap.MaybeSnapshot(force: true)) yield return e;
    }

    /// <summary>How long to wait before asking again for a fragment the relay has not published yet.</summary>
    public static readonly TimeSpan FragmentRetryInterval = TimeSpan.FromSeconds(3);

    /// <summary>Transport problems are worth a reconnect; anything else is the parser failing on the data.</summary>
    public static bool IsNetwork(Exception ex) => ex is HttpRequestException or IOException or TimeoutException
        || (ex is TaskCanceledException tce && tce.InnerException is TimeoutException)
        || (ex.InnerException is { } inner && inner != ex && IsNetwork(inner));
}

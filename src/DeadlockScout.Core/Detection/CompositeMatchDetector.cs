using System.Threading.Channels;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockScout.Core.Detection;

/// <summary>
/// Merges signal sources (console.log primary, Steam content log fallback) into one start/end per match.
/// The console log carries the match id; the content log only says "a match started", so it is used only when the
/// console log has been silent, and its start is upgraded in place when the console line arrives late.
/// </summary>
public sealed class CompositeMatchDetector : IMatchDetector
{
    private readonly IReadOnlyList<IMatchSignalSource> _sources;
    private readonly IClock _clock;
    private readonly ILogger _log;
    private readonly TimeSpan _fallbackGrace;
    private readonly Func<uint> _accountId;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly object _gate = new();

    private MatchContext? _current;
    private DateTimeOffset _lastConsoleSignal = DateTimeOffset.MinValue;

    public event Action<MatchContext>? MatchStarted;
    public event Action<long>? MatchEnded;
    public event Action<string>? HeroLocked;

    /// <param name="accountId">Resolves the local account id at start time (Steam config); 0 when unknown.</param>
    /// <param name="fallbackGrace">How long after a content-log start to wait for the console line before firing without a match id.</param>
    public CompositeMatchDetector(IEnumerable<IMatchSignalSource> sources, IClock clock, Func<uint> accountId,
                                  TimeSpan? fallbackGrace = null, ILogger<CompositeMatchDetector>? log = null)
    {
        _sources = sources.ToList();
        _clock = clock;
        _accountId = accountId;
        _fallbackGrace = fallbackGrace ?? TimeSpan.FromSeconds(20);
        _log = log ?? NullLogger<CompositeMatchDetector>.Instance;
    }

    public MatchContext? Current { get { lock (_gate) return _current; } }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<MatchSignal>();
        var pumps = _sources.Select(s => PumpAsync(s, channel.Writer, ct)).ToList();
        _ = Task.WhenAll(pumps).ContinueWith(_ => channel.Writer.TryComplete(), TaskScheduler.Default);

        MatchSignal.Started? pendingFallback = null;
        while (true)
        {
            MatchSignal signal;
            if (pendingFallback is not null)
            {
                // Wait for a console line, but no longer than the grace period.
                using var timeout = new CancellationTokenSource(_fallbackGrace);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                try
                {
                    signal = await channel.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    StartMatch(0, 0, pendingFallback.Source);
                    pendingFallback = null;
                    continue;
                }
                catch (ChannelClosedException) { break; }
            }
            else
            {
                try { signal = await channel.Reader.ReadAsync(ct).ConfigureAwait(false); }
                catch (ChannelClosedException) { break; }
            }

            Handle(signal, ref pendingFallback);
        }
    }

    /// <summary>Feeds one signal synchronously. Exposed for replay tests; the live loop calls it too.</summary>
    public void Handle(MatchSignal signal, ref MatchSignal.Started? pendingFallback)
    {
        bool fromConsole = signal.Source.Contains("console", StringComparison.OrdinalIgnoreCase);
        if (fromConsole) _lastConsoleSignal = signal.At;

        switch (signal)
        {
            case MatchSignal.LobbyCreated lc:
                pendingFallback = null;
                StartMatch(lc.MatchId, lc.LobbyId, lc.Source);
                break;

            case MatchSignal.Started st when st.MatchId > 0:
                pendingFallback = null;
                StartMatch(st.MatchId, 0, st.Source);
                break;

            case MatchSignal.Started st:
                // Fallback with no id: ignore if the console already told us about a match recently.
                lock (_gate)
                {
                    if (_current is not null) break;
                }
                if (_clock.UtcNow - _lastConsoleSignal < TimeSpan.FromMinutes(1)) break;
                pendingFallback = st;
                break;

            case MatchSignal.Ended en:
                pendingFallback = null;
                EndMatch(en.MatchId);
                break;

            case MatchSignal.HeroLocked hl:
                HeroLocked?.Invoke(hl.HeroName);
                break;
        }
    }

    private void StartMatch(long matchId, long lobbyId, string source)
    {
        MatchContext ctx;
        lock (_gate)
        {
            if (_current is not null)
            {
                if (_current.MatchId == matchId)
                {
                    // Duplicate. The server ticket line starts the match a moment before the lobby line names the lobby.
                    if (_current.LobbyId == 0 && lobbyId > 0) _current = _current with { LobbyId = lobbyId };
                    return;
                }
                if (_current.MatchId == 0 && matchId > 0)
                {
                    // Upgrade a fallback start with the real id; consumers see a new start for the same match.
                    _current = _current with { MatchId = matchId, LobbyId = lobbyId };
                    ctx = _current;
                    _log.LogInformation("Match id {MatchId} arrived from {Source} after fallback start", matchId, source);
                    MatchStarted?.Invoke(ctx);
                    return;
                }
                // A new match started before the previous one ended (missed end line).
                var old = _current.MatchId;
                _current = null;
                MatchEnded?.Invoke(old);
            }
            ctx = new MatchContext(matchId, lobbyId, _accountId(), _clock.UtcNow);
            _current = ctx;
        }
        _log.LogInformation("Match started: {MatchId} (lobby {LobbyId}) via {Source}", matchId, lobbyId, source);
        MatchStarted?.Invoke(ctx);
    }

    private void EndMatch(long matchId)
    {
        long ended;
        lock (_gate)
        {
            if (_current is null) return;
            if (matchId > 0 && _current.MatchId > 0 && matchId != _current.MatchId) return;
            ended = _current.MatchId;
            _current = null;
        }
        _log.LogInformation("Match ended: {MatchId}", ended);
        MatchEnded?.Invoke(ended);
    }

    private async Task PumpAsync(IMatchSignalSource source, ChannelWriter<MatchSignal> writer, CancellationToken ct)
    {
        try
        {
            await foreach (var s in source.ReadAsync(ct).ConfigureAwait(false))
                await writer.WriteAsync(s, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Signal source {Source} stopped", source.Name);
        }
    }
}

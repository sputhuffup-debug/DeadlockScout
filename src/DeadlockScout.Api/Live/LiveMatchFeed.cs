using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockScout.Api.Live;

/// <summary>
/// Turns player_controller entity events into a <see cref="Roster"/> (once every slot is filled, or after a timeout
/// with what is known) and <see cref="NetWorthTick"/>s (one per player per tick_end when a value changed).
/// Ranks come from players/rank, not the feed (the feed's rank field is always 0). matches/active is a free fallback
/// roster source while the broadcast is still starting.
/// </summary>
public sealed class LiveMatchFeed : ILiveMatchFeed
{
    private readonly ILiveEventStream _stream;
    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly ILogger _log;
    private readonly TimeSpan _rosterTimeout;

    private readonly object _gate = new();
    private long _matchId;
    private Task? _pump;
    private CancellationTokenSource? _pumpCts;
    private readonly Dictionary<int, LiveEventDto> _controllers = new();           // entity_index → latest state
    private readonly Dictionary<uint, (int NetWorth, TimeSpan At)> _lastEmitted = new();
    private readonly List<ChannelWriter<NetWorthTick>> _tickSubscribers = new();
    private TaskCompletionSource<Roster> _roster = NewRosterTcs();
    private TimeSpan _gameTime;
    private bool _ended; // the current match's stream sent its end event

    public LiveMatchFeed(ILiveEventStream stream, IDeadlockApi api, ICache cache, TimeSpan? rosterTimeout = null, ILogger<LiveMatchFeed>? log = null)
    {
        _stream = stream;
        _api = api;
        _cache = cache;
        _rosterTimeout = rosterTimeout ?? TimeSpan.FromSeconds(150);
        _log = log ?? NullLogger<LiveMatchFeed>.Instance;
    }

    public const int ExpectedPlayers = 12;

    public async Task<Roster> WaitForRosterAsync(MatchContext ctx, CancellationToken ct)
    {
        EnsurePump(ctx.MatchId);
        Task<Roster> fromFeed;
        lock (_gate) fromFeed = _roster.Task;

        using var timeout = new CancellationTokenSource(_rosterTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            var roster = await fromFeed.WaitAsync(linked.Token).ConfigureAwait(false);
            return await WithRanksAsync(roster, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Feed too slow: use whatever we have, topped up from matches/active.
            var partial = BuildRoster(force: true);
            if (partial.Players.Count < ExpectedPlayers)
            {
                try
                {
                    var active = await _api.ActiveMatchAsync(ctx.MatchId, ct).ConfigureAwait(false);
                    if (active is not null) partial = MergeActive(partial, active);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "matches/active unavailable"); }
            }
            return await WithRanksAsync(partial, ct).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<NetWorthTick> TicksAsync(MatchContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        // Subscribe before starting the pump, so a stream that reaches its end before we get here cannot strand us;
        // a subscriber that joins after the end completes straight away.
        var channel = Channel.CreateUnbounded<NetWorthTick>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate)
        {
            if (_ended && _pump is not null && _matchId == ctx.MatchId) channel.Writer.TryComplete();
            else _tickSubscribers.Add(channel.Writer);
        }
        EnsurePump(ctx.MatchId);
        try
        {
            await foreach (var tick in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return tick;
        }
        finally
        {
            lock (_gate) _tickSubscribers.Remove(channel.Writer);
        }
    }

    /// <summary>Feeds one event; public so fixtures can be replayed without a network.</summary>
    public void Handle(LiveEventDto e)
    {
        List<NetWorthTick>? emit = null;
        Roster? roster = null;
        lock (_gate)
        {
            if (e.GameTime is { } gt && gt >= 0) _gameTime = TimeSpan.FromSeconds(gt);

            if (e.IsPlayerController && e.EntityIndex is { } idx)
            {
                if (string.Equals(e.Delta, "delete", StringComparison.OrdinalIgnoreCase) || string.Equals(e.Delta, "leave", StringComparison.OrdinalIgnoreCase))
                {
                    // keep the last known state so the roster survives a disconnect
                }
                else if (_controllers.TryGetValue(idx, out var prev))
                {
                    _controllers[idx] = Merge(prev, e);
                }
                else
                {
                    _controllers[idx] = e;
                }
                if (!_roster.Task.IsCompleted && CountKnownPlayers() >= ExpectedPlayers) roster = BuildRoster(force: false);
            }
            else if (e.IsTickEnd)
            {
                foreach (var c in _controllers.Values)
                {
                    if (c.SteamId is not { } id || id == 0 || c.NetWorth is not { } nw) continue;
                    if (_lastEmitted.TryGetValue(id, out var last) && last.NetWorth == nw && _gameTime - last.At < TimeSpan.FromSeconds(10)) continue;
                    _lastEmitted[id] = (nw, _gameTime);
                    (emit ??= new()).Add(new NetWorthTick(_gameTime, id, nw, c.Kills ?? 0, c.Deaths ?? 0, c.Assists ?? 0));
                }
            }
            else if (e.IsEnd)
            {
                _ended = true;
                foreach (var w in _tickSubscribers) w.TryComplete();
                _tickSubscribers.Clear();
            }

            if (roster is not null) _roster.TrySetResult(roster);
            if (emit is not null)
                foreach (var w in _tickSubscribers)
                    foreach (var t in emit) w.TryWrite(t);
        }
    }

    public TimeSpan GameTime { get { lock (_gate) return _gameTime; } }

    private void EnsurePump(long matchId)
    {
        lock (_gate)
        {
            if (_pump is not null && _matchId == matchId) return;
            _pumpCts?.Cancel();
            _matchId = matchId;
            _controllers.Clear();
            _lastEmitted.Clear();
            _roster = NewRosterTcs();
            _gameTime = TimeSpan.Zero;
            _ended = false;
            _pumpCts = new CancellationTokenSource();
            var ct = _pumpCts.Token;
            _pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var e in _stream.ReadAsync(matchId, ct).ConfigureAwait(false)) Handle(e);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log.LogError(ex, "Live feed for match {MatchId} stopped", matchId); }
            }, ct);
        }
    }

    private int CountKnownPlayers() => _controllers.Values.Count(c => c.SteamId is > 0 && c.HeroId is > 0 && c.Team is > 0);

    private Roster BuildRoster(bool force)
    {
        var players = _controllers.Values
            .Where(c => c.SteamId is > 0 && (force || (c.HeroId is > 0 && c.Team is > 0)))
            .GroupBy(c => c.SteamId!.Value).Select(g => g.Last())
            .OrderBy(c => c.Team).ThenBy(c => c.PlayerSlot)
            .Select(c => new RosterPlayer(c.SteamId!.Value, c.SteamName ?? c.SteamId.Value.ToString(), c.Team ?? 0, c.HeroId ?? 0,
                c.Rank is > 0 ? c.Rank : null, c.AssignedLane is >= 0 ? c.AssignedLane : null))
            .ToList();
        return new Roster(_matchId, players, _gameTime);
    }

    private static Roster MergeActive(Roster partial, ActiveMatch active)
    {
        var known = partial.Players.ToDictionary(p => p.AccountId);
        foreach (var p in active.Players)
            if (!known.ContainsKey(p.AccountId))
                known[p.AccountId] = new RosterPlayer(p.AccountId, p.AccountId.ToString(), p.Team, p.HeroId, null, null);
        return partial with { Players = known.Values.OrderBy(p => p.Team).ToList() };
    }

    private async Task<Roster> WithRanksAsync(Roster roster, CancellationToken ct)
    {
        try
        {
            var ids = roster.Players.Select(p => p.AccountId).ToList();
            var ranks = await _cache.GetOrCreateAsync(CacheKeys.Ranks(ids), TimeSpan.FromHours(12),
                async c => (await _api.RanksAsync(ids, c).ConfigureAwait(false)).ToList(), staleWhileRevalidate: true, ct).ConfigureAwait(false);
            var byId = ranks.ToDictionary(r => r.AccountId, r => r.Badge);
            return roster with
            {
                Players = roster.Players.Select(p => byId.TryGetValue(p.AccountId, out var badge) && badge > 0 ? p with { Rank = badge } : p).ToList(),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "players/rank unavailable; roster without ranks");
            return roster;
        }
    }

    private static LiveEventDto Merge(LiveEventDto prev, LiveEventDto next) => new()
    {
        Tick = next.Tick ?? prev.Tick, GameTime = next.GameTime ?? prev.GameTime, EventType = next.EventType, Delta = next.Delta,
        EntityIndex = next.EntityIndex ?? prev.EntityIndex, EntityType = next.EntityType ?? prev.EntityType,
        SteamId = next.SteamId is > 0 ? next.SteamId : prev.SteamId, SteamName = next.SteamName ?? prev.SteamName,
        Team = next.Team ?? prev.Team, HeroId = next.HeroId is > 0 ? next.HeroId : prev.HeroId, PlayerSlot = next.PlayerSlot ?? prev.PlayerSlot,
        Rank = next.Rank ?? prev.Rank, AssignedLane = next.AssignedLane ?? prev.AssignedLane, NetWorth = next.NetWorth ?? prev.NetWorth,
        Kills = next.Kills ?? prev.Kills, Deaths = next.Deaths ?? prev.Deaths, Assists = next.Assists ?? prev.Assists,
    };

    private static TaskCompletionSource<Roster> NewRosterTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

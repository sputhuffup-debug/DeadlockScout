using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Laning;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Objectives;
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
    private readonly Dictionary<uint, (int NetWorth, TimeSpan At, long[]? Items, bool? Ult)> _lastEmitted = new();
    private readonly List<ChannelWriter<NetWorthTick>> _tickSubscribers = new();
    private readonly List<ChannelWriter<ObjectiveSample>> _objectiveSubscribers = new();
    private readonly List<ChannelWriter<CampSample>> _campSubscribers = new();
    private LiveEventDto? _camps;              // this snapshot's camps event, published at its tick_end
    private LiveEventDto? _objectives;         // latest objectives event, published at the next tick_end
    private ObjectiveSample? _lastObjectives;  // last published, for change detection and late subscribers
    private TaskCompletionSource<Roster> _roster = NewRosterTcs();
    private TimeSpan _gameTime;
    private bool _ended; // the current match's stream sent its end event
    // Which pump the current match's events come from. A stream only checks for cancellation between reads, so a cancelled
    // one can still hand over a whole snapshot (twelve players) after the next match has started and cleared the state:
    // those events carry the old generation and are dropped, or the next roster would be the last match's players.
    private long _generation;

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

    public IAsyncEnumerable<NetWorthTick> TicksAsync(MatchContext ctx, CancellationToken ct)
        => SubscribeAsync(_tickSubscribers, null, ctx, ct);

    public IAsyncEnumerable<ObjectiveSample> ObjectivesAsync(MatchContext ctx, CancellationToken ct)
        => SubscribeAsync(_objectiveSubscribers, () => _lastObjectives, ctx, ct);

    public IAsyncEnumerable<CampSample> CampsAsync(MatchContext ctx, CancellationToken ct)
        => SubscribeAsync(_campSubscribers, null, ctx, ct);

    private async IAsyncEnumerable<T> SubscribeAsync<T>(List<ChannelWriter<T>> subscribers, Func<T?>? current, MatchContext ctx,
                                                       [EnumeratorCancellation] CancellationToken ct) where T : class
    {
        // Subscribe before starting the pump, so a stream that reaches its end before we get here cannot strand us;
        // a subscriber that joins after the end completes straight away.
        var channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate)
        {
            if (_matchId == ctx.MatchId && current?.Invoke() is { } now) channel.Writer.TryWrite(now);
            if (_ended && _pump is not null && _matchId == ctx.MatchId) channel.Writer.TryComplete();
            else subscribers.Add(channel.Writer);
        }
        EnsurePump(ctx.MatchId);
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return item;
        }
        finally
        {
            lock (_gate) subscribers.Remove(channel.Writer);
        }
    }

    /// <summary>Feeds one event to the current match; public so fixtures can be replayed without a network.</summary>
    public void Handle(LiveEventDto e)
    {
        long generation;
        lock (_gate) generation = _generation;
        Handle(e, generation);
    }

    /// <summary>One event from the pump of <paramref name="generation"/>; ignored once a newer match has started.</summary>
    private void Handle(LiveEventDto e, long generation)
    {
        List<NetWorthTick>? emit = null;
        ObjectiveSample? objectives = null;
        CampSample? camps = null;
        Roster? roster = null;
        lock (_gate)
        {
            if (generation != _generation) return;
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
            else if (e.IsObjectives)
            {
                _objectives = e;
            }
            else if (e.IsCamps)
            {
                _camps = e;
            }
            else if (e.IsTickEnd)
            {
                if (_objectives is { } o)
                {
                    var sample = new ObjectiveSample(_gameTime, o.MidBossAlive, o.RejuvAmber ?? 0, o.RejuvSapphire ?? 0, o.UrnOnMap);
                    if (_lastObjectives is null || _lastObjectives with { GameTime = sample.GameTime } != sample)
                        objectives = _lastObjectives = sample;
                }
                if (_camps is { Neutrals: { } neutrals } && _campSubscribers.Count > 0)
                {
                    var positions = new Dictionary<uint, (float X, float Y)>();
                    foreach (var c in _controllers.Values)
                        if (c.SteamId is { } sid && sid > 0 && c.X is { } x && c.Y is { } y) positions[sid] = (x, y);
                    camps = new CampSample(_gameTime, neutrals.Select(n => new NeutralSighting(n.Id, n.X, n.Y, n.Sinners)).ToList(), positions);
                }
                _camps = null;
                foreach (var c in _controllers.Values)
                {
                    if (c.SteamId is not { } id || id == 0 || c.NetWorth is not { } nw) continue;
                    // A purchase leaves net worth unchanged (souls become an item), so a changed item list emits too.
                    if (_lastEmitted.TryGetValue(id, out var last) && last.NetWorth == nw && _gameTime - last.At < TimeSpan.FromSeconds(10)
                        && SameItems(last.Items, c.Items) && last.Ult == c.UltimateTrained) continue;
                    _lastEmitted[id] = (nw, _gameTime, c.Items, c.UltimateTrained);
                    (emit ??= new()).Add(new NetWorthTick(_gameTime, id, nw, c.Kills ?? 0, c.Deaths ?? 0, c.Assists ?? 0, c.Items, c.UltimateTrained));
                }
            }
            else if (e.IsEnd)
            {
                _ended = true;
                CompleteSubscribers();
            }

            if (roster is not null) _roster.TrySetResult(roster);
            if (emit is not null)
                foreach (var w in _tickSubscribers)
                    foreach (var t in emit) w.TryWrite(t);
            if (objectives is not null)
                foreach (var w in _objectiveSubscribers) w.TryWrite(objectives);
            if (camps is not null)
                foreach (var w in _campSubscribers) w.TryWrite(camps);
        }
    }

    public TimeSpan GameTime { get { lock (_gate) return _gameTime; } }

    public void Stop(long matchId)
    {
        lock (_gate)
        {
            if (_pump is null || (matchId > 0 && matchId != _matchId)) return;
            _pumpCts?.Cancel();
            _generation++; // anything the stopped stream still hands over is dropped
            _pump = null;
            _ended = true;
            CompleteSubscribers();
        }
    }

    private void CompleteSubscribers()
    {
        foreach (var w in _tickSubscribers) w.TryComplete();
        _tickSubscribers.Clear();
        foreach (var w in _objectiveSubscribers) w.TryComplete();
        _objectiveSubscribers.Clear();
        foreach (var w in _campSubscribers) w.TryComplete();
        _campSubscribers.Clear();
    }

    private void EnsurePump(long matchId)
    {
        lock (_gate)
        {
            if (_pump is not null && _matchId == matchId) return;
            _pumpCts?.Cancel();
            long generation = ++_generation;
            _matchId = matchId;
            _controllers.Clear();
            _lastEmitted.Clear();
            _objectives = null; _lastObjectives = null; _camps = null;
            _roster = NewRosterTcs();
            _gameTime = TimeSpan.Zero;
            _ended = false;
            _pumpCts = new CancellationTokenSource();
            var ct = _pumpCts.Token;
            _pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var e in _stream.ReadAsync(matchId, ct).ConfigureAwait(false))
                    {
                        if (ct.IsCancellationRequested) break;
                        Handle(e, generation);
                    }
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
        Items = next.Items ?? prev.Items, UltimateTrained = next.UltimateTrained ?? prev.UltimateTrained,
        X = next.X ?? prev.X, Y = next.Y ?? prev.Y,
    };

    private static bool SameItems(long[]? a, long[]? b) => ReferenceEquals(a, b) || (a is not null && b is not null && a.AsSpan().SequenceEqual(b));

    private static TaskCompletionSource<Roster> NewRosterTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

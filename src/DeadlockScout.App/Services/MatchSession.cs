using DeadlockScout.Api.Benchmarks;
using DeadlockScout.Api.Work;
using DeadlockScout.App.ViewModels;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Builds;
using DeadlockScout.Core.History;
using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Diagnostics;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Scouting;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeadlockScout.App.Services;

/// <summary>
/// One match from the lobby line to the end screen: starts the feed, scouts the roster, meters your souls against the
/// benchmark curve and keeps the view models current. Everything here is background work; the overlay reads the view
/// models on its own 1 Hz timer.
/// </summary>
public sealed class MatchSession : IDisposable
{
    private readonly IMatchDetector _detector;
    private readonly MatchHistoryService _history;
    private DateTimeOffset? _clockAnchor;
    private bool _delayLogged, _anchorChecked;
    private bool? _urnOnMap;
    private readonly ILiveMatchFeed _feed;
    private readonly IScoutingService _scouting;
    private readonly ISoulSampleSource _souls;
    private readonly CompositeBenchmark _benchmark;
    private readonly PersonalBenchmark _personal;
    private readonly MatchupService _matchups;
    private readonly BuildHintService _builds;
    private readonly MatchActivityGate _gate;
    private readonly AssetCatalog _assets;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly OverlayState _state;
    private readonly ILogger<MatchSession> _log;
    private readonly DiagnosticsLog _diagnostics;

    private CancellationTokenSource? _matchCts;
    private MatchContext? _ctx;
    private string? _lockedHero;

    public MatchSession(IMatchDetector detector, ILiveMatchFeed feed, IScoutingService scouting, ISoulSampleSource souls,
                        CompositeBenchmark benchmark, PersonalBenchmark personal, MatchupService matchups, BuildHintService builds, MatchActivityGate gate, AssetCatalog assets,
                        IOptionsMonitor<ScoutSettings> settings, OverlayState state, ILogger<MatchSession> log, MatchHistoryService history,
                        DiagnosticsLog diagnostics)
    {
        _diagnostics = diagnostics;
        _history = history;
        _detector = detector;
        _feed = feed;
        _scouting = scouting;
        _souls = souls;
        _benchmark = benchmark;
        _personal = personal;
        _matchups = matchups;
        _builds = builds;
        _gate = gate;
        _assets = assets;
        _settings = settings;
        _state = state;
        _log = log;

        _detector.MatchStarted += OnMatchStarted;
        _detector.MatchEnded += OnMatchEnded;
        _detector.HeroLocked += hero => { _lockedHero = hero; _state.Status.HeroName = _assets.HeroName(_assets.HeroIdByClassName(hero) ?? 0); };
        _detector.ClockStarted += OnClockStarted;
    }

    /// <summary>
    /// The console log's game-start line: the live clock for the timers runs from it. Kept here too, so a resync after
    /// the demo lobby (which resets the overlay state) still has it. Spectating, the broadcast is what you watch, so it
    /// is the clock.
    /// </summary>
    private void OnClockStarted(DateTimeOffset at)
    {
        if (_ctx is not { Spectating: false } || _clockAnchor is not null) return;
        _clockAnchor = at;
        _state.Clock.SetAnchor(at);
    }

    private void OnMatchStarted(MatchContext ctx)
    {
        _matchCts?.Cancel();
        _matchCts = new CancellationTokenSource();
        if (_ctx?.MatchId != ctx.MatchId || ctx.MatchId == 0) _clockAnchor = null; // the id upgrade of a fallback start is the same match
        _ctx = ctx;
        _delayLogged = _anchorChecked = false;
        _diagnostics.MatchStarted(ctx.MatchId, ctx.Spectating, DateTimeOffset.UtcNow);
        _urnOnMap = null;
        _gate.MatchStarted();
        _state.MidBossRespawn = TimeSpan.FromSeconds(Math.Max(60, _settings.CurrentValue.Objectives.MidBossRespawnSeconds));
        _state.Reset();
        _state.InMatch = true;
        _state.Status.MatchId = ctx.MatchId;
        _state.Spectating = ctx.Spectating;
        _state.Status.Phase = ctx.Spectating ? "Spectating, waiting for the broadcast"
            : ctx.HasMatchId ? "Match found, waiting for the broadcast" : "Match started (no id yet)";
        if (!ctx.HasMatchId) return; // nothing to scout without an id; the detector re-fires when it arrives
        _ = RunAsync(ctx, _matchCts.Token);
    }

    /// <summary>
    /// Stops writing to the overlay state while the demo lobby borrows it; the match keeps going and <see cref="Resync"/>
    /// picks it up again.
    /// </summary>
    public void Pause() => _matchCts?.Cancel();

    /// <summary>
    /// Fills the overlay state again from the live match, after the demo lobby replaced it. Cheap: the feed keeps the
    /// roster it already has and the benchmark calls come from the cache. The soul meter and the item timelines start
    /// over, so the sparkline restarts and items bought before now do not flash.
    /// </summary>
    public void Resync()
    {
        if (_ctx is not { HasMatchId: true } ctx) return;
        _matchCts?.Cancel();
        var cts = _matchCts = new CancellationTokenSource();
        _state.Reset();
        _state.Status.Phase = ctx.Spectating ? "Spectating, catching up" : "Catching up with the match";
        _log.LogInformation("Resyncing match {MatchId} after the demo lobby", ctx.MatchId);
        _ = RunAsync(ctx, cts.Token);
    }

    private void OnMatchEnded(long matchId)
    {
        _diagnostics.MatchEnded(DateTimeOffset.UtcNow);
        _matchCts?.Cancel();
        _feed.Stop(matchId);
        _gate.MatchEnded();
        _state.InMatch = false;
        _state.Status.Phase = "Match ended";
        _state.ScoutVisibleUntil = DateTimeOffset.MinValue;
        _ctx = null;
    }

    private async Task RunAsync(MatchContext ctx, CancellationToken ct)
    {
        try
        {
            var timers = _settings.CurrentValue.Timers;
            _state.TimerSettings = timers;
            var objectives = _settings.CurrentValue.Objectives;
            _state.UrnFirstSpawn = TimeSpan.FromSeconds(Math.Max(0, objectives.UrnFirstSpawnSeconds));
            _state.UrnInterval = TimeSpan.FromSeconds(Math.Max(0, objectives.UrnIntervalSeconds));
            _state.AssumedDelay = ctx.Spectating ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, timers.BroadcastDelaySeconds));
            if (_clockAnchor is { } anchor) _state.Clock.SetAnchor(anchor);
            var roster = await _feed.WaitForRosterAsync(ctx, ct);
            _diagnostics.RosterReceived(DateTimeOffset.UtcNow);
            _state.Status.Phase = "Roster received, scouting";
            // Spectating someone else's match: nobody in it is you, so the panels about your own game stay empty and the
            // pace board covers all twelve players.
            var me = ctx.Spectating ? null : roster.Find(ctx.MyAccountId);
            int heroId = ctx.Spectating ? 0 : me?.HeroId ?? (_lockedHero is { } h ? _assets.HeroIdByClassName(h) ?? 0 : 0);
            _state.Status.HeroName = _assets.HeroName(heroId);

            var rows = await _scouting.ScoutAsync(roster, ctx.MyAccountId, ct);
            _state.SetRoster(roster, rows, ctx.MyAccountId, _assets);
            _state.ScoutVisibleUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(_settings.CurrentValue.Overlay.ScoutAutoSeconds);
            _state.Status.Phase = ctx.Spectating ? "Spectating" : "Live";

            // Curves for the lobby's own rank when watching, not yours.
            int? badge = ctx.Spectating ? AverageBadge(roster) : me?.Rank;
            var bracket = BracketPolicy.Resolve(_settings.CurrentValue.Meter, badge);
            var benchmarkTask = heroId > 0 ? WatchBenchmarkAsync(heroId, bracket, ct) : Task.CompletedTask;
            var personalTask = heroId > 0 && _settings.CurrentValue.Overlay.Panels.Personal ? LoadPersonalCurveAsync(heroId, bracket, ct) : Task.CompletedTask;
            _state.ShowPhases = _settings.CurrentValue.Goal.Phases;
            var goalTask = heroId > 0 && !ctx.Spectating && _settings.CurrentValue.Goal.Overlay ? LoadGoalCurveAsync(heroId, me?.Rank, ct) : Task.CompletedTask;
            var nextItemTask = heroId > 0 && !ctx.Spectating && _settings.CurrentValue.Overlay.Panels.NextItem
                ? LoadNextItemAsync(ctx.MyAccountId, heroId, me?.Rank, ct) : Task.CompletedTask;
            var matchupTask = me is not null && heroId > 0 ? LoadLaneMatchupsAsync(roster, me, heroId, bracket, ct) : Task.CompletedTask;
            var partyTask = LoadPartiesAsync(roster, ct);
            var buildTask = heroId > 0 && _settings.CurrentValue.Overlay.Panels.Build ? LoadBuildAsync(heroId, bracket, ct) : Task.CompletedTask;
            var enemyTask = WatchEnemyCurvesAsync(roster, me?.Team ?? 0, bracket, ct);
            var spikesTask = _settings.CurrentValue.Overlay.Panels.Spikes ? LoadEnemySpikesAsync(roster, me?.Team ?? 0, bracket, ct) : Task.CompletedTask;
            var ticksTask = PumpTicksAsync(ctx, roster, me?.Team ?? 0, ct);
            var soulsTask = ctx.Spectating ? Task.CompletedTask : PumpSoulsAsync(ctx, ct);
            var objectivesTask = PumpObjectivesAsync(ctx, ct);
            await Task.WhenAll(benchmarkTask, personalTask, goalTask, nextItemTask, matchupTask, partyTask, buildTask, enemyTask, spikesTask, ticksTask, soulsTask, objectivesTask);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Match session failed");
            _state.Status.Phase = $"Error: {ex.Message}";
        }
    }

    private static int? AverageBadge(Roster roster)
    {
        var ranks = roster.Players.Where(p => p.Rank is > 0).Select(p => p.Rank!.Value).ToList();
        return ranks.Count == 0 ? null : (int)Math.Round(ranks.Average());
    }

    /// <summary>
    /// Your own recent games on the hero, for the second pace line. Its requests are background ones and wait while the
    /// match is live, so in practice the curve comes from the between-match prefetch's cache.
    /// </summary>
    private async Task LoadPersonalCurveAsync(int heroId, RankBracket bracket, CancellationToken ct)
    {
        try
        {
            if (await _personal.GetAsync(heroId, bracket, ct) is { } curve) _state.PersonalCurve = curve;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.LogDebug(ex, "No personal curve for hero {Hero}", heroId); }
    }

    /// <summary>
    /// The rank goal's curve on your hero (the tier above yours unless settings name one), for the overlay's gate line.
    /// The analytics provider answers it from the same cached per-bracket call the benchmark uses.
    /// </summary>
    private async Task LoadGoalCurveAsync(int heroId, int? badge, CancellationToken ct)
    {
        int tier = RankGoal.Resolve(_settings.CurrentValue.Goal, badge);
        try
        {
            // The analytics buckets only: the composite's fallback is background work, which waits until the match is over.
            var provider = _benchmark.Providers.FirstOrDefault(p => p.Name == AnalyticsBucketBenchmark.ProviderName) ?? _benchmark;
            if (await provider.GetAsync(heroId, RankGoal.Bracket(tier), ct) is { } curve)
            {
                _state.GoalRank = RankGoal.Name(tier);
                _state.GoalCurve = curve;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.LogDebug(ex, "No {Rank} curve for hero {Hero}", RankGoal.Name(tier), heroId); }
    }

    /// <summary>
    /// The next-item nudge's two tables: your usual big items on the hero from your last games (the match history's cached
    /// details; at most one bulk call for games not seen before) and the goal rank's buy times on it (cached for the patch).
    /// </summary>
    private async Task LoadNextItemAsync(uint me, int heroId, int? badge, CancellationToken ct)
    {
        try
        {
            var page = await _history.GetAsync(me, NextItemGames, ct);
            var games = page.Games.Where(g => g.Entry.HeroId == heroId && g.Detail?.Purchases is { Count: > 0 })
                .Select(g => g.Detail!.Purchases!).ToList();
            var usual = NextItem.UsualBuild(games, _assets.ItemCost);
            if (usual.Count == 0) return;
            _state.UsualBuild = usual;
            int tier = RankGoal.Resolve(_settings.CurrentValue.Goal, badge);
            _state.NextItemRank = RankGoal.Name(tier);
            _state.RankItemTimings = await _builds.GetItemTimingsAsync(heroId, RankGoal.Bracket(tier), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.LogDebug(ex, "No next-item nudge for hero {Hero}", heroId); }
    }

    /// <summary>Your recent games the usual build is read from.</summary>
    public const int NextItemGames = 30;

    /// <summary>The build hint's tables; usually already cached by the between-match prefetch.</summary>
    private async Task LoadBuildAsync(int heroId, RankBracket bracket, CancellationToken ct)
        => _state.SetBuild(await _builds.GetAsync(heroId, bracket, ct)); // the service turns failures into empty tables

    /// <summary>Your hero's lane record against each lane opponent's hero, for the lane card.</summary>
    private async Task LoadLaneMatchupsAsync(Roster roster, RosterPlayer me, int heroId, RankBracket bracket, CancellationToken ct)
    {
        if (me.Lane is null) return;
        foreach (var opponent in roster.Players.Where(p => p.Team != me.Team && p.Lane == me.Lane && p.HeroId > 0))
        {
            try
            {
                if (await _matchups.GetAsync(heroId, opponent.HeroId, bracket, ct) is { } m) _state.SetMatchup(opponent.AccountId, m);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogDebug(ex, "No matchup for {Hero} against {Enemy}", heroId, opponent.HeroId); }
        }
    }

    /// <summary>Likely parties for the scout list's "Team 1" tags, after the list is up (one cached call per player).</summary>
    private async Task LoadPartiesAsync(Roster roster, CancellationToken ct)
    {
        try
        {
            _state.SetParties(await _scouting.PartiesAsync(roster, ct));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.LogWarning(ex, "Party tags unavailable"); }
    }

    private async Task WatchBenchmarkAsync(int heroId, RankBracket bracket, CancellationToken ct)
    {
        await foreach (var curve in _benchmark.WatchAsync(heroId, bracket, ct))
        {
            _state.Curve = curve;
            _state.Pace.Provider = curve.Provider;
            _state.Pace.SampleSize = curve.SampleSize;
            _state.Pace.Bracket = bracket.ToString();
        }
    }

    private async Task WatchEnemyCurvesAsync(Roster roster, int myTeam, RankBracket bracket, CancellationToken ct)
    {
        foreach (var enemy in roster.Players.Where(p => p.Team != myTeam && p.HeroId > 0))
        {
            try
            {
                var curve = await _benchmark.GetAsync(enemy.HeroId, bracket, ct);
                if (curve is not null) _state.EnemyCurves[enemy.AccountId] = curve;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogDebug(ex, "No curve for enemy hero {Hero}", enemy.HeroId); }
        }
    }

    /// <summary>
    /// Each enemy's power-spike items. The 6,400-soul items and the ultimate count straight away; the items that lift
    /// their hero's win rate most at this rank join once that hero's item table loads (one cached analytics call each).
    /// </summary>
    private async Task LoadEnemySpikesAsync(Roster roster, int myTeam, RankBracket bracket, CancellationToken ct)
    {
        var enemies = roster.Players.Where(p => p.Team != myTeam && p.HeroId > 0).ToList();
        var always = ItemSpikes.Pick(Array.Empty<ItemBucketStat>(), _assets.Items);
        foreach (var enemy in enemies) _state.EnemySpikes[enemy.AccountId] = always;
        foreach (var hero in enemies.GroupBy(p => p.HeroId))
        {
            var stats = await _builds.GetItemStatsAsync(hero.Key, bracket, ct); // the service turns failures into an empty table
            var set = ItemSpikes.Pick(stats, _assets.Items);
            foreach (var enemy in hero) _state.EnemySpikes[enemy.AccountId] = set;
        }
    }

    private async Task PumpTicksAsync(MatchContext ctx, Roster roster, int myTeam, CancellationToken ct)
    {
        await foreach (var tick in _feed.TicksAsync(ctx, ct))
        {
            _state.LatestGameTime = tick.GameTime;
            _state.Clock.ObserveFeed(tick.GameTime, DateTimeOffset.UtcNow);
            LogDelayOnce(tick.GameTime);
            if (tick.AccountId == ctx.MyAccountId)
            {
                if (tick.Items is { } mine) _state.MyItems.Observe(mine, tick.GameTime); // for the next-item nudge
                continue;
            }
            var player = roster.Find(tick.AccountId);
            if (player is null || player.Team == myTeam) continue;
            _state.UpdateEnemy(tick, player, _assets);
        }
    }

    private async Task PumpObjectivesAsync(MatchContext ctx, CancellationToken ct)
    {
        await foreach (var sample in _feed.ObjectivesAsync(ctx, ct))
        {
            // The respawn delay is not in the broadcast; logging each one seen checks the default against real matches.
            if (_state.ObjectiveTracker.Observe(sample) is { } delay)
                _log.LogInformation("Mid boss respawned {Delay} after it died (game time {At})", PaceMath.FormatClock(delay), PaceMath.FormatClock(sample.GameTime));
            // The urn schedule (objectives.urnFirstSpawnSeconds) was checked on a real match; each urn seen arriving keeps checking it.
            if (sample.UrnOnMap == true && _urnOnMap != true)
                _log.LogInformation("Urn on the map at game time {At}", PaceMath.FormatClock(sample.GameTime));
            if (sample.UrnOnMap is { } onMap) _urnOnMap = onMap;
        }
    }

    private async Task PumpSoulsAsync(MatchContext ctx, CancellationToken ct)
    {
        await foreach (var sample in _souls.ReadAsync(ctx, ct))
        {
            _state.Meter.Add(sample);
            _state.Pace.SourceLabel = sample.Source == SampleSource.Ocr ? "OCR" : "broadcast";
            if (sample.Source == SampleSource.Ocr)
            {
                var now = DateTimeOffset.UtcNow;
                _state.Clock.ObserveScreen(sample.GameTime, now);
                // The HUD clock is exact: how far off the game-log anchor is, once (a pause makes it run ahead).
                if (!_anchorChecked && _state.Clock.Anchor is { } a && sample.GameTime > TimeSpan.FromMinutes(1))
                {
                    _anchorChecked = true;
                    _log.LogInformation("Game-log clock start vs the HUD clock: {Offset:+0.0;-0.0} s (game time {At})",
                        ((now - a) - sample.GameTime).TotalSeconds, PaceMath.FormatClock(sample.GameTime));
                    _diagnostics.ClockCheck((now - a) - sample.GameTime);
                }
            }
        }
    }

    /// <summary>Logs the broadcast delay the game-log anchor implies, once a match is three minutes in, to check the anchor and the default delay.</summary>
    private void LogDelayOnce(TimeSpan gameTime)
    {
        if (_delayLogged || gameTime < TimeSpan.FromMinutes(3) || _state.Clock.MeasuredDelay(DateTimeOffset.UtcNow) is not { } delay) return;
        _delayLogged = true;
        _log.LogInformation("Broadcast delay by the game log's start line: {Delay:0.0} s (game time {At})", delay.TotalSeconds, PaceMath.FormatClock(gameTime));
        _diagnostics.BroadcastDelay(delay, "against the game log's start line");
    }

    public void Dispose()
    {
        _matchCts?.Cancel();
        _detector.MatchStarted -= OnMatchStarted;
        _detector.MatchEnded -= OnMatchEnded;
        _detector.ClockStarted -= OnClockStarted;
    }
}

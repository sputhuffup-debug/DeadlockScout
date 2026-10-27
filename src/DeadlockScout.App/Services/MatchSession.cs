using DeadlockScout.Api.Work;
using DeadlockScout.App.ViewModels;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeadlockScout.App.Services;

/// <summary>
/// One match from the lobby line to the end screen: starts the feed, scouts the roster, meters your souls against the
/// benchmark curve and keeps the view models current. Everything here is background work; the overlay reads the view
/// models on its own 2 Hz timer.
/// </summary>
public sealed class MatchSession : IDisposable
{
    private readonly IMatchDetector _detector;
    private readonly ILiveMatchFeed _feed;
    private readonly IScoutingService _scouting;
    private readonly ISoulSampleSource _souls;
    private readonly CompositeBenchmark _benchmark;
    private readonly MatchActivityGate _gate;
    private readonly AssetCatalog _assets;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly OverlayState _state;
    private readonly ILogger<MatchSession> _log;

    private CancellationTokenSource? _matchCts;
    private MatchContext? _ctx;
    private string? _lockedHero;

    public MatchSession(IMatchDetector detector, ILiveMatchFeed feed, IScoutingService scouting, ISoulSampleSource souls,
                        CompositeBenchmark benchmark, MatchActivityGate gate, AssetCatalog assets,
                        IOptionsMonitor<ScoutSettings> settings, OverlayState state, ILogger<MatchSession> log)
    {
        _detector = detector;
        _feed = feed;
        _scouting = scouting;
        _souls = souls;
        _benchmark = benchmark;
        _gate = gate;
        _assets = assets;
        _settings = settings;
        _state = state;
        _log = log;

        _detector.MatchStarted += OnMatchStarted;
        _detector.MatchEnded += OnMatchEnded;
        _detector.HeroLocked += hero => { _lockedHero = hero; _state.Status.HeroName = _assets.HeroName(_assets.HeroIdByClassName(hero) ?? 0); };
    }

    private void OnMatchStarted(MatchContext ctx)
    {
        _matchCts?.Cancel();
        _matchCts = new CancellationTokenSource();
        _ctx = ctx;
        _gate.MatchStarted();
        _state.Reset();
        _state.Status.MatchId = ctx.MatchId;
        _state.Status.Phase = ctx.HasMatchId ? "Match found, waiting for the broadcast" : "Match started (no id yet)";
        if (!ctx.HasMatchId) return; // nothing to scout without an id; the detector re-fires when it arrives
        _ = RunAsync(ctx, _matchCts.Token);
    }

    private void OnMatchEnded(long matchId)
    {
        _matchCts?.Cancel();
        _gate.MatchEnded();
        _state.Status.Phase = "Match ended";
        _state.ScoutVisibleUntil = DateTimeOffset.MinValue;
        _ctx = null;
    }

    private async Task RunAsync(MatchContext ctx, CancellationToken ct)
    {
        try
        {
            var roster = await _feed.WaitForRosterAsync(ctx, ct);
            _state.Status.Phase = "Roster received, scouting";
            var me = roster.Find(ctx.MyAccountId);
            int heroId = me?.HeroId ?? (_lockedHero is { } h ? _assets.HeroIdByClassName(h) ?? 0 : 0);
            _state.Status.HeroName = _assets.HeroName(heroId);

            var rows = await _scouting.ScoutAsync(roster, ctx.MyAccountId, ct);
            _state.SetRoster(roster, rows, ctx.MyAccountId, _assets);
            _state.ScoutVisibleUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(_settings.CurrentValue.Overlay.ScoutAutoSeconds);
            _state.Status.Phase = "Live";

            var bracket = ResolveBracket(me?.Rank);
            var benchmarkTask = heroId > 0 ? WatchBenchmarkAsync(heroId, bracket, ct) : Task.CompletedTask;
            var enemyTask = WatchEnemyCurvesAsync(roster, me?.Team ?? 0, bracket, ct);
            var ticksTask = PumpTicksAsync(ctx, roster, me?.Team ?? 0, ct);
            var soulsTask = PumpSoulsAsync(ctx, ct);
            await Task.WhenAll(benchmarkTask, enemyTask, ticksTask, soulsTask);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Match session failed");
            _state.Status.Phase = $"Error: {ex.Message}";
        }
    }

    private RankBracket ResolveBracket(int? myBadge)
    {
        var cfg = _settings.CurrentValue.Meter;
        if (cfg.BenchmarkBracket.Equals("all", StringComparison.OrdinalIgnoreCase)) return RankBracket.All;
        if (!cfg.BenchmarkBracket.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var parts = cfg.BenchmarkBracket.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[0], out var lo) && int.TryParse(parts[1], out var hi)) return new RankBracket(lo, hi);
        }
        return myBadge is { } b and > 0 ? RankBracket.Around(b, cfg.BracketWidth) : RankBracket.All;
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

    private async Task PumpTicksAsync(MatchContext ctx, Roster roster, int myTeam, CancellationToken ct)
    {
        await foreach (var tick in _feed.TicksAsync(ctx, ct))
        {
            _state.LatestGameTime = tick.GameTime;
            if (tick.AccountId == ctx.MyAccountId) continue;
            var player = roster.Find(tick.AccountId);
            if (player is null || player.Team == myTeam) continue;
            _state.UpdateEnemy(tick, player, _assets);
        }
    }

    private async Task PumpSoulsAsync(MatchContext ctx, CancellationToken ct)
    {
        await foreach (var sample in _souls.ReadAsync(ctx, ct))
        {
            _state.Meter.Add(sample);
            _state.Pace.SourceLabel = sample.Source == SampleSource.Ocr ? "OCR" : "broadcast";
        }
    }

    public void Dispose()
    {
        _matchCts?.Cancel();
        _detector.MatchStarted -= OnMatchStarted;
        _detector.MatchEnded -= OnMatchEnded;
    }
}

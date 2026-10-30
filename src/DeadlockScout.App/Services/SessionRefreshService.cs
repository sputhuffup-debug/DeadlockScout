using DeadlockScout.Api.Http;
using DeadlockScout.Api.Work;
using DeadlockScout.App.ViewModels;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.History;
using DeadlockScout.Core.Session;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeadlockScout.App.Services;

/// <summary>
/// Keeps the session view and the weekly summary current: at startup, every <see cref="SessionSettings.RefreshHours"/>,
/// and a few minutes after each match (once the match history has it). Never while a match is live.
/// </summary>
public sealed class SessionRefreshService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(50);
    /// <summary>The match history usually lists a match a couple of minutes after it ends.</summary>
    private static readonly TimeSpan AfterMatch = TimeSpan.FromMinutes(3);

    private readonly SessionService _session;
    private readonly MatchActivityGate _gate;
    private readonly GamePaths _paths;
    private readonly OverlayState _state;
    private readonly AssetCatalog _assets;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly ILogger<SessionRefreshService> _log;
    private readonly SemaphoreSlim _matchEnded = new(0);
    private readonly MatchHistoryService _history;
    private readonly IClock _clock;

    public SessionRefreshService(SessionService session, IMatchDetector detector, MatchActivityGate gate, GamePaths paths, OverlayState state,
                                 AssetCatalog assets, IOptionsMonitor<ScoutSettings> settings, ILogger<SessionRefreshService> log,
                                 MatchHistoryService history, IClock clock)
    {
        _history = history;
        _clock = clock;
        _session = session;
        _gate = gate;
        _paths = paths;
        _state = state;
        _assets = assets;
        _settings = settings;
        _log = log;
        detector.MatchEnded += _ => _matchEnded.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                await _gate.WaitAsync(RequestPriority.Background, ct).ConfigureAwait(false);
                try
                {
                    if (await _session.RefreshAsync(_paths.AccountId, ct).ConfigureAwait(false) is { } view) _state.SetSession(view, _assets);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log.LogWarning(ex, "Session refresh failed"); }
                try { await RefreshWeeklyAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log.LogWarning(ex, "Weekly summary failed"); }

                var idle = TimeSpan.FromHours(Math.Max(1, _settings.CurrentValue.Session.RefreshHours));
                if (await _matchEnded.WaitAsync(idle, ct).ConfigureAwait(false))
                {
                    while (_matchEnded.CurrentCount > 0) await _matchEnded.WaitAsync(ct).ConfigureAwait(false); // collapse repeats
                    await Task.Delay(AfterMatch, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>
    /// The last full week against the one before: their games from the cached history, and the details the match history
    /// caches for good (a game not seen before is fetched once, all in one bulk call).
    /// </summary>
    private async Task RefreshWeeklyAsync(CancellationToken ct)
    {
        uint me = _paths.AccountId;
        if (me == 0) return;
        var now = _clock.UtcNow;
        var since = WeeklySummary.LastWeekStart(now, TimeZoneInfo.Local).AddDays(-7);
        var page = await _history.GetAsync(me, MaxWeeklyGames, ct, since).ConfigureAwait(false);
        _state.SetWeekly(WeeklySummary.Build(page.Games, now, TimeZoneInfo.Local, _settings.CurrentValue.Targets, _assets.ItemCost), _assets);
    }

    /// <summary>Two weeks of games, capped so a very busy fortnight still costs one bulk call.</summary>
    private const int MaxWeeklyGames = 80;

    public override void Dispose()
    {
        _matchEnded.Dispose();
        base.Dispose();
    }
}

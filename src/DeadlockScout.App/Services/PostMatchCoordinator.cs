using System.Threading.Channels;
using DeadlockScout.Api.Http;
using DeadlockScout.Api.Work;
using DeadlockScout.App.ViewModels;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.PostMatch;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeadlockScout.App.Services;

/// <summary>
/// After each match: sends new replay salts from the Steam cache, then asks for the match's metadata on a backing-off
/// schedule until the API has processed it, and publishes the post-match report. Also sends salts hourly and shows your
/// latest match at startup. All of it is background work that waits while a match is live.
/// </summary>
public sealed class PostMatchCoordinator : BackgroundService
{
    /// <summary>Minutes after the match ends to try for the report: the API usually has a match within minutes, sometimes hours.</summary>
    public static readonly IReadOnlyList<int> ReportAttemptsMinutes = new[] { 1, 2, 4, 8, 15, 30, 60, 120, 240 };
    /// <summary>
    /// Attempts that ask deadlock-api to fetch the match from Steam when it is not stored yet (its Steam fallback allows 3
    /// an hour per IP without a key): after the usual few minutes for someone else's salts, and once more an hour in.
    /// </summary>
    public static readonly IReadOnlyList<int> SteamAttemptsMinutes = new[] { 8, 60 };
    /// <summary>Attempts that send salts first (the Steam client caches them when the match is opened in game).</summary>
    public static readonly IReadOnlyList<int> IngestAttemptsMinutes = new[] { 1, 8, 30 };
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan IngestEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartupReportMaxAge = TimeSpan.FromHours(24);

    private readonly PostMatchService _reports;
    private readonly SaltIngest _ingest;
    private readonly IMatchDetector _detector;
    private readonly MatchActivityGate _gate;
    private readonly GamePaths _paths;
    private readonly OverlayState _state;
    private readonly AssetCatalog _assets;
    private readonly ILogger<PostMatchCoordinator> _log;
    private readonly Channel<long> _ended = Channel.CreateUnbounded<long>();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _spectated = new();
    private long _shown;

    public PostMatchCoordinator(PostMatchService reports, SaltIngest ingest, IMatchDetector detector, MatchActivityGate gate, GamePaths paths,
                                OverlayState state, AssetCatalog assets, ILogger<PostMatchCoordinator> log)
    {
        _reports = reports;
        _ingest = ingest;
        _detector = detector;
        _gate = gate;
        _paths = paths;
        _state = state;
        _assets = assets;
        _log = log;
        // A spectated match is someone else's: no report, no salts to send for it.
        _detector.MatchStarted += ctx => { if (ctx.Spectating) _spectated[ctx.MatchId] = 0; };
        _detector.MatchEnded += id => { if (!_spectated.TryRemove(id, out _)) _ended.Writer.TryWrite(id); };
    }

    /// <summary>A new report for a match that just ended (not the one shown at startup).</summary>
    public event Action<PostMatchReport>? ReportReady;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct).ConfigureAwait(false);
            await IngestAsync(ct).ConfigureAwait(false);
            await ShowLatestAsync(ct).ConfigureAwait(false);

            var nextIngest = DateTimeOffset.UtcNow + IngestEvery;
            while (!ct.IsCancellationRequested)
            {
                var wait = nextIngest - DateTimeOffset.UtcNow;
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timer.CancelAfter(wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
                try
                {
                    var matchId = await _ended.Reader.ReadAsync(timer.Token).ConfigureAwait(false);
                    _ = FollowUpAsync(matchId, ct); // one follow-up per ended match, in the background
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await IngestAsync(ct).ConfigureAwait(false);
                    nextIngest = DateTimeOffset.UtcNow + IngestEvery;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task FollowUpAsync(long matchId, CancellationToken ct)
    {
        try
        {
            int elapsed = 0;
            foreach (int minute in ReportAttemptsMinutes)
            {
                await Task.Delay(TimeSpan.FromMinutes(minute - elapsed), ct).ConfigureAwait(false);
                elapsed = minute;
                await _gate.WaitAsync(RequestPriority.Background, ct).ConfigureAwait(false); // never during the next match
                if (IngestAttemptsMinutes.Contains(minute)) await IngestAsync(ct).ConfigureAwait(false);
                if (await TryShowAsync(matchId, ct, SteamAttemptsMinutes.Contains(minute)).ConfigureAwait(false) is { } report)
                {
                    ReportReady?.Invoke(report);
                    return;
                }
            }
            _log.LogInformation("No metadata for match {MatchId} after {Minutes} minutes; giving up on its report", matchId, elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _log.LogWarning(ex, "Post-match follow-up for {MatchId} failed", matchId); }
    }

    private async Task ShowLatestAsync(CancellationToken ct)
    {
        try
        {
            if (await _reports.LatestMatchIdAsync(_paths.AccountId, StartupReportMaxAge, ct).ConfigureAwait(false) is { } id)
                await TryShowAsync(id, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.LogDebug(ex, "No latest match to report at startup"); }
    }

    private async Task<PostMatchReport?> TryShowAsync(long matchId, CancellationToken ct, bool askSteam = false)
    {
        PostMatchReport? report;
        try { report = await _reports.TryBuildAsync(matchId, _paths.AccountId, ct, askSteam).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.LogDebug(ex, "Report for match {MatchId} not available yet", matchId); return null; }
        if (report is null) return null;
        if (Interlocked.Exchange(ref _shown, matchId) == matchId) return report;
        _state.SetLastMatch(report, _assets);
        return report;
    }

    private async Task IngestAsync(CancellationToken ct)
    {
        try { await _ingest.RunAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.LogWarning(ex, "Sending replay salts failed"); }
    }
}

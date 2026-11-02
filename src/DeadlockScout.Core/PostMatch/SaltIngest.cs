using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeadlockScout.Core.PostMatch;

/// <summary>
/// Sends replay salts found in the Steam client's HTTP cache to deadlock-api's ingest endpoint, so matches you open in
/// the game get on record (and their metadata becomes available for the report). Each match is sent once; a run that
/// finds nothing new makes no request. Off with <see cref="IngestSettings.AutoSubmitSalts"/>; the match history's
/// "Send my matches" button (<see cref="RunNowAsync"/>) runs a pass on demand either way. Salts reach the Steam cache
/// only when the Deadlock client loads a match's details (opening it in the in-game match history), so a pass that finds
/// nothing says so in plain words (<see cref="Describe"/>).
/// </summary>
public sealed record IngestRun(DateTimeOffset At, bool ByYou, bool CacheFound, int Found, int Sent, string? Error = null);

public sealed class SaltIngest
{
    /// <summary>Server-side cap per request.</summary>
    public const int MaxPerRequest = 1000;
    /// <summary>How many submitted match ids are remembered; older ones have long left the Steam cache.</summary>
    public const int Remembered = 500;
    /// <summary>First run with no history: look this far back in the Steam cache.</summary>
    public static readonly TimeSpan FirstLookBack = TimeSpan.FromDays(2);
    /// <summary>Later runs rescan a little before the last one, so a file written during a run is not missed.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(10);

    private readonly IDeadlockApi _api;
    private readonly ICache _cache;
    private readonly Func<DateTimeOffset, IReadOnlyList<MatchSalts>> _scan;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly ILogger _log;
    private readonly Func<bool> _cacheFound;
    private readonly SemaphoreSlim _running = new(1, 1);

    /// <param name="scan">Salts in Steam cache files written since the given time (SteamHttpCache.ScanSalts).</param>
    /// <param name="cacheFound">Whether Steam's HTTP cache folder exists, for telling "nothing there" from "no cache".</param>
    public SaltIngest(IDeadlockApi api, ICache cache, Func<DateTimeOffset, IReadOnlyList<MatchSalts>> scan, IClock clock,
                      IOptionsMonitor<ScoutSettings> settings, ILogger<SaltIngest>? log = null, Func<bool>? cacheFound = null)
    {
        _cacheFound = cacheFound ?? (() => true);
        _api = api;
        _cache = cache;
        _scan = scan;
        _clock = clock;
        _settings = settings;
        _log = log ?? NullLogger<SaltIngest>.Instance;
    }

    /// <summary>The salts worth sending: at least one salt, not sent before, newest first, capped.</summary>
    public static IReadOnlyList<MatchSalts> SelectNew(IEnumerable<MatchSalts> found, IReadOnlyCollection<long> submitted)
        => found.Where(s => s.MatchId > 0 && (s.MetadataSalt is not null || s.ReplaySalt is not null) && !submitted.Contains(s.MatchId))
            .GroupBy(s => s.MatchId).Select(g => g.First())
            .OrderByDescending(s => s.MatchId)
            .Take(MaxPerRequest)
            .ToList();

    /// <summary>The last pass, automatic or asked for; null before the first. For Diagnostics.</summary>
    public IngestRun? LastRun { get; private set; }

    /// <summary>One automatic pass: scan what Steam cached since the last pass, send what is new, remember it. Returns how many matches were sent.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (!_settings.CurrentValue.Ingest.AutoSubmitSalts) return 0;
        var run = await PassAsync(byYou: false, ct).ConfigureAwait(false);
        if (run.Error is { } error) throw new InvalidOperationException(error);
        return run.Sent;
    }

    /// <summary>
    /// A pass the player asked for (the match history's button): the whole look-back window, whatever the automatic
    /// setting, and never throws: the result says what happened.
    /// </summary>
    public Task<IngestRun> RunNowAsync(CancellationToken ct) => PassAsync(byYou: true, ct);

    private async Task<IngestRun> PassAsync(bool byYou, CancellationToken ct)
    {
        await _running.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock.UtcNow;
            var lastScan = await _cache.GetAsync<DateTimeOffset?>(CacheKeys.IngestLastScan, ct).ConfigureAwait(false);
            var since = !byYou && lastScan is { } last ? last - Overlap : now - FirstLookBack;
            bool cacheFound = _cacheFound();

            var submitted = await _cache.GetAsync<List<long>>(CacheKeys.IngestSubmitted, ct).ConfigureAwait(false) ?? new List<long>();
            var found = _scan(since);
            int foundMatches = found.Where(s => s.MetadataSalt is not null || s.ReplaySalt is not null).Select(s => s.MatchId).Distinct().Count();
            var fresh = SelectNew(found, submitted);
            string? error = null;
            if (fresh.Count > 0)
            {
                try
                {
                    await _api.SubmitSaltsAsync(fresh, ct).ConfigureAwait(false);
                    submitted = fresh.Select(s => s.MatchId).Concat(submitted).Distinct().Take(Remembered).ToList();
                    await _cache.SetAsync(CacheKeys.IngestSubmitted, submitted, null, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    error = ex.Message;
                }
            }
            if (error is null) await _cache.SetAsync<DateTimeOffset?>(CacheKeys.IngestLastScan, now, null, ct).ConfigureAwait(false);
            var run = new IngestRun(now, byYou, cacheFound, foundMatches, error is null ? fresh.Count : 0, error);
            LastRun = run;
            _log.LogInformation("Salt scan{By}: {Found} matches with salts in Steam's cache since {Since:yyyy-MM-dd HH:mm}Z, {Sent} sent{Error}",
                byYou ? " (asked for)" : "", foundMatches, since, run.Sent, error is null ? "" : $"; deadlock-api refused: {error}");
            return run;
        }
        finally { _running.Release(); }
    }

    /// <summary>What the "Send my matches" button says after a pass.</summary>
    public static string Describe(IngestRun run)
    {
        static string Matches(int n) => n == 1 ? "1 match" : $"{n} matches";
        int days = (int)FirstLookBack.TotalDays;
        if (run.Error is not null) return $"deadlock-api did not take them ({run.Error}). Try again later.";
        if (!run.CacheFound) return "Steam's download cache was not found; check the Steam folder in Diagnostics.";
        if (run.Sent > 0) return $"Sent {Matches(run.Sent)} to deadlock-api. They usually show up here within minutes.";
        if (run.Found > 0)
            return $"The {Matches(run.Found)} in Steam's cache from the last {days} days were already sent. One still missing has not been processed yet.";
        return $"No match details in Steam's cache from the last {days} days. Open the match in Deadlock's in-game match history, then send again.";
    }

    /// <summary>The Diagnostics line: when the last pass ran and what it found.</summary>
    public static string Summary(IngestRun? run) => run is null
        ? "no scan yet"
        : $"last scan {run.At.ToLocalTime():HH:mm}{(run.ByYou ? " (by you)" : "")}: {run.Found} in Steam's cache, {run.Sent} sent"
          + (run.Error is not null ? " (deadlock-api refused)" : !run.CacheFound ? " (Steam cache not found)" : "");
}

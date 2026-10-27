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
/// finds nothing new makes no request. Off with <see cref="IngestSettings.AutoSubmitSalts"/>.
/// </summary>
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

    /// <param name="scan">Salts in Steam cache files written since the given time (SteamHttpCache.ScanSalts).</param>
    public SaltIngest(IDeadlockApi api, ICache cache, Func<DateTimeOffset, IReadOnlyList<MatchSalts>> scan, IClock clock,
                      IOptionsMonitor<ScoutSettings> settings, ILogger<SaltIngest>? log = null)
    {
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

    /// <summary>One pass: scan, send what is new, remember it. Returns how many matches were sent.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (!_settings.CurrentValue.Ingest.AutoSubmitSalts) return 0;
        var now = _clock.UtcNow;
        var lastScan = await _cache.GetAsync<DateTimeOffset?>(CacheKeys.IngestLastScan, ct).ConfigureAwait(false);
        var since = lastScan is { } last ? last - Overlap : now - FirstLookBack;

        var submitted = await _cache.GetAsync<List<long>>(CacheKeys.IngestSubmitted, ct).ConfigureAwait(false) ?? new List<long>();
        var fresh = SelectNew(_scan(since), submitted);
        if (fresh.Count > 0)
        {
            await _api.SubmitSaltsAsync(fresh, ct).ConfigureAwait(false);
            submitted = fresh.Select(s => s.MatchId).Concat(submitted).Distinct().Take(Remembered).ToList();
            await _cache.SetAsync(CacheKeys.IngestSubmitted, submitted, null, ct).ConfigureAwait(false);
            _log.LogInformation("Sent salts for {Count} matches to deadlock-api", fresh.Count);
        }
        await _cache.SetAsync<DateTimeOffset?>(CacheKeys.IngestLastScan, now, null, ct).ConfigureAwait(false);
        return fresh.Count;
    }
}

namespace DeadlockScout.Core.Diagnostics;

/// <summary>
/// What the diagnostics page shows, collected as the app runs: the last match (feed, roster, broadcast delay, clock
/// check, the app's own CPU and memory during it) and every API call since the app started (results per endpoint family, rate-limit waits, the latest errors).
/// Thread-safe: the HTTP handlers, the live feed and the session write to it, the page reads snapshots. Holds no account
/// ids or names: callers pass request paths, which <see cref="ApiPath"/> strips of player ids and query strings.
/// </summary>
public sealed class DiagnosticsLog
{
    /// <summary>The latest errors kept for the page, newest first.</summary>
    public const int MaxErrors = 12;

    private readonly object _gate = new();
    private readonly Dictionary<string, FamilyStats> _families = new(StringComparer.Ordinal);
    private readonly LinkedList<ApiError> _errors = new();
    private MatchDiagnostics? _match;
    private readonly DateTimeOffset _since;
    private readonly int _cores;

    public DiagnosticsLog(DateTimeOffset? since = null, int? cores = null)
    {
        _since = since ?? DateTimeOffset.UtcNow;
        _cores = cores ?? Environment.ProcessorCount;
    }

    // ---- Match ----

    public void MatchStarted(long matchId, bool spectating, DateTimeOffset at)
    {
        lock (_gate)
        {
            // The detector re-fires for the same match when its id arrives late; keep what was learned.
            if (_match is { } m && m.MatchId == matchId && m.EndedAt is null) return;
            _match = new MatchDiagnostics { MatchId = matchId, Spectating = spectating, StartedAt = at, Performance = new PerformanceMeter(_cores) };
        }
    }

    public void MatchEnded(DateTimeOffset at) => Edit(m => m.EndedAt ??= at);

    /// <summary>The live feed in use, "Direct broadcast"; a second call (a fallback) replaces it and keeps the first as a note.</summary>
    public void Feed(string name) => Edit(m =>
    {
        if (m.Feed is { } old && old != name) m.Notes.Add($"Feed switched from {old} to {name}");
        m.Feed = name;
    });

    /// <summary>Something about the feed worth seeing: a fallback's reason, a broadcast that kept answering "not yet".</summary>
    public void FeedNote(string text) => Edit(m => { if (m.Notes.Count < 20) m.Notes.Add(text); });

    public void RosterReceived(DateTimeOffset at) => Edit(m => m.RosterAt ??= at);

    /// <summary>The broadcast's delay behind the game, and how it was measured ("the game log's start line").</summary>
    public void BroadcastDelay(TimeSpan delay, string how) => Edit(m => { m.Delay = delay; m.DelayHow = how; });

    /// <summary>The game log's clock start against the HUD clock (OCR): positive when the log's clock runs ahead.</summary>
    public void ClockCheck(TimeSpan offset) => Edit(m => m.ClockOffset = offset);

    /// <summary>A reading of our own process; counted only while a match is live, so idle time never flatters the budget.</summary>
    public void Performance(ProcessSample sample)
    {
        lock (_gate) { if (_match is { EndedAt: null } m) m.Performance.Add(sample); }
    }

    private void Edit(Action<MatchDiagnostics> change)
    {
        lock (_gate) { if (_match is { } m) change(m); }
    }

    // ---- API ----

    /// <summary>One HTTP attempt as it went over the wire (retries count separately): its status, or the transport error.</summary>
    public void ApiAttempt(string family, string path, int? status, string? error, TimeSpan took, DateTimeOffset at)
    {
        lock (_gate)
        {
            var f = Family(family);
            f.Attempts++;
            f.TotalTime += took;
            bool failed = error is not null || status is null or >= 400;
            // A 404 is an answer ("not processed yet", "no such match"), not a fault: counted, not listed.
            if (status == 404) f.NotFound++;
            else if (status == 429) f.TooMany++;
            else if (failed) f.Errors++;
            if (failed && status != 404)
            {
                _errors.AddFirst(new ApiError(at, family, ApiPath(path), status, error));
                while (_errors.Count > MaxErrors) _errors.RemoveLast();
            }
        }
    }

    /// <summary>A request held by our own rate limiter before it was sent.</summary>
    public void RateLimitWait(string family, TimeSpan waited)
    {
        lock (_gate)
        {
            var f = Family(family);
            f.Waits++;
            f.Waited += waited;
            if (waited > f.LongestWait) f.LongestWait = waited;
        }
    }

    private FamilyStats Family(string name)
    {
        if (!_families.TryGetValue(name, out var f)) _families[name] = f = new FamilyStats();
        return f;
    }

    /// <summary>A request path without its query (which holds account ids) and with player ids replaced.</summary>
    public static string ApiPath(string path)
    {
        int q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        return System.Text.RegularExpressions.Regex.Replace(path, @"/players/\d+", "/players/<account>");
    }

    public DiagnosticsSnapshot Snapshot()
    {
        lock (_gate)
        {
            var families = _families.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new ApiFamilyStats(kv.Key, kv.Value.Attempts, kv.Value.Errors, kv.Value.TooMany, kv.Value.NotFound,
                    kv.Value.Attempts == 0 ? TimeSpan.Zero : kv.Value.TotalTime / kv.Value.Attempts,
                    kv.Value.Waits, kv.Value.Waited, kv.Value.LongestWait))
                .ToList();
            var match = _match is { } m
                ? new MatchSnapshot(m.MatchId, m.Spectating, m.StartedAt, m.EndedAt, m.Feed, m.RosterAt - m.StartedAt, m.Delay, m.DelayHow,
                    m.ClockOffset, m.Notes.ToList(), m.Performance.Summary())
                : null;
            return new DiagnosticsSnapshot(_since, match, families, _errors.ToList());
        }
    }

    private sealed class FamilyStats
    {
        public int Attempts, Errors, TooMany, NotFound, Waits;
        public TimeSpan TotalTime, Waited, LongestWait;
    }

    private sealed class MatchDiagnostics
    {
        public long MatchId;
        public bool Spectating;
        public DateTimeOffset StartedAt;
        public DateTimeOffset? EndedAt, RosterAt;
        public string? Feed, DelayHow;
        public TimeSpan? Delay, ClockOffset;
        public List<string> Notes { get; } = new();
        public PerformanceMeter Performance = new(1);
    }
}

public sealed record DiagnosticsSnapshot(DateTimeOffset Since, MatchSnapshot? Match, IReadOnlyList<ApiFamilyStats> Api, IReadOnlyList<ApiError> Errors);

/// <param name="RosterAfter">From the match start to the first roster, null until it came.</param>
/// <param name="Performance">The app's own CPU and memory while the match was live, against the 1.0 budget; null until measured.</param>
public sealed record MatchSnapshot(long MatchId, bool Spectating, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? Feed,
    TimeSpan? RosterAfter, TimeSpan? Delay, string? DelayHow, TimeSpan? ClockOffset, IReadOnlyList<string> Notes,
    PerformanceSummary? Performance = null);

/// <param name="Errors">Failures other than 404 (answered "not found") and 429 (counted in <paramref name="TooMany"/>).</param>
public sealed record ApiFamilyStats(string Family, int Attempts, int Errors, int TooMany, int NotFound, TimeSpan AverageTime,
    int Waits, TimeSpan Waited, TimeSpan LongestWait);

public sealed record ApiError(DateTimeOffset At, string Family, string Path, int? Status, string? Error);

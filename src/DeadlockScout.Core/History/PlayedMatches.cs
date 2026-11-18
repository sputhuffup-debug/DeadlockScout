using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;

namespace DeadlockScout.Core.History;

/// <summary>
/// A match you played, as the app saw it: its id from the console log, when it ran and the hero you locked. One added
/// by id in the match history (<paramref name="Added"/>, 0.7) has no times of its own: both are when it was added.
/// </summary>
public sealed record PlayedMatch(long MatchId, DateTimeOffset StartedAt, DateTimeOffset EndedAt, int? HeroId, bool Added = false);

/// <summary>
/// The matches the app saw you play in the last few days. deadlock-api lists a match in your history only once it has
/// stored it, which can take hours (or never happen when nobody sends its salts), so the history page shows these as
/// "processing" cards until the API catches up, and builds the card from the match's metadata when that comes first.
/// </summary>
public sealed class PlayedMatches
{
    /// <summary>How long a match is kept (and its built history row cached), so one past <see cref="Processing"/> is still there for its last ask.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromDays(3);
    /// <summary>
    /// How long a match the API has not stored shows as processing (0.8): past this it is asked for one last time
    /// (<see cref="TakeExpiredAsync"/>) and dropped, since the API has it by then or never will.
    /// </summary>
    public static readonly TimeSpan Processing = TimeSpan.FromHours(48);
    public const int Max = 20;

    private readonly ICache _cache;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public PlayedMatches(ICache cache, IClock clock)
    {
        _cache = cache;
        _clock = clock;
    }

    public async Task RecordAsync(uint me, PlayedMatch match, CancellationToken ct)
    {
        if (me == 0 || match.MatchId <= 0) return;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = await LoadAsync(me, ct).ConfigureAwait(false);
            list.RemoveAll(m => m.MatchId == match.MatchId);
            list.Insert(0, match);
            await _cache.SetAsync(CacheKeys.Played(me), Trim(list), null, ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    /// <summary>The matches still within <see cref="Processing"/>, newest first.</summary>
    public async Task<IReadOnlyList<PlayedMatch>> RecentAsync(uint me, CancellationToken ct)
    {
        if (me == 0) return Array.Empty<PlayedMatch>();
        var cutoff = _clock.UtcNow - Processing;
        return Trim(await LoadAsync(me, ct).ConfigureAwait(false)).Where(m => m.EndedAt >= cutoff).ToList();
    }

    /// <summary>
    /// The matches past <see cref="Processing"/>, removed from the list: the caller asks for each one last time. A match
    /// the history built a row for stays listed there from that row, so dropping it here loses nothing.
    /// </summary>
    public async Task<IReadOnlyList<PlayedMatch>> TakeExpiredAsync(uint me, CancellationToken ct)
    {
        if (me == 0) return Array.Empty<PlayedMatch>();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = await LoadAsync(me, ct).ConfigureAwait(false);
            var cutoff = _clock.UtcNow - Processing;
            var expired = list.Where(m => m.EndedAt < cutoff).ToList();
            if (expired.Count == 0) return expired;
            list.RemoveAll(m => m.EndedAt < cutoff);
            await _cache.SetAsync(CacheKeys.Played(me), Trim(list), null, ct).ConfigureAwait(false);
            return expired;
        }
        finally { _lock.Release(); }
    }

    private async Task<List<PlayedMatch>> LoadAsync(uint me, CancellationToken ct)
        => await _cache.GetAsync<List<PlayedMatch>>(CacheKeys.Played(me), ct).ConfigureAwait(false) ?? new List<PlayedMatch>();

    private List<PlayedMatch> Trim(IEnumerable<PlayedMatch> list)
    {
        var cutoff = _clock.UtcNow - Keep;
        return list.Where(m => m.EndedAt >= cutoff).OrderByDescending(m => m.StartedAt).Take(Max).ToList();
    }
}

using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Caching;

namespace DeadlockScout.Core.History;

/// <summary>A match you played, as the app saw it: its id from the console log, when it ran and the hero you locked.</summary>
public sealed record PlayedMatch(long MatchId, DateTimeOffset StartedAt, DateTimeOffset EndedAt, int? HeroId);

/// <summary>
/// The matches the app saw you play in the last few days. deadlock-api lists a match in your history only once it has
/// stored it, which can take hours (or never happen when nobody sends its salts), so the history page shows these as
/// "processing" cards until the API catches up, and builds the card from the match's metadata when that comes first.
/// </summary>
public sealed class PlayedMatches
{
    /// <summary>How long a match is kept: past this the API has it or never will.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromDays(3);
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

    /// <summary>The matches kept, newest first.</summary>
    public async Task<IReadOnlyList<PlayedMatch>> RecentAsync(uint me, CancellationToken ct)
        => me == 0 ? Array.Empty<PlayedMatch>() : Trim(await LoadAsync(me, ct).ConfigureAwait(false));

    private async Task<List<PlayedMatch>> LoadAsync(uint me, CancellationToken ct)
        => await _cache.GetAsync<List<PlayedMatch>>(CacheKeys.Played(me), ct).ConfigureAwait(false) ?? new List<PlayedMatch>();

    private List<PlayedMatch> Trim(IEnumerable<PlayedMatch> list)
    {
        var cutoff = _clock.UtcNow - Keep;
        return list.Where(m => m.EndedAt >= cutoff).OrderByDescending(m => m.StartedAt).Take(Max).ToList();
    }
}

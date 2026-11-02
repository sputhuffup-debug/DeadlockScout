namespace DeadlockScout.Core.Updates;

/// <summary>
/// When the automatic update checks run: <see cref="FirstCheck"/> after start, then every <see cref="Every"/>, never
/// while a match is live. A check that falls during a match waits for it to end and runs <see cref="AfterMatch"/>
/// later (again later if the next match has started by then), instead of being skipped until the next round six hours
/// on. The waiting and the clock are passed in, so the schedule is tested without a match or a real delay.
/// </summary>
public sealed class UpdateSchedule
{
    public static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2), Every = TimeSpan.FromHours(6), AfterMatch = TimeSpan.FromMinutes(3);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<bool> _matchLive;
    private readonly Func<CancellationToken, Task> _matchEnded;
    private readonly Func<bool> _enabled;

    /// <param name="delay">Waits a while (Task.Delay in the app).</param>
    /// <param name="matchLive">Whether a match is live now.</param>
    /// <param name="matchEnded">Completes once no match is live.</param>
    /// <param name="enabled">Whether automatic checks are switched on (Settings → Updates), read at each round.</param>
    public UpdateSchedule(Func<TimeSpan, CancellationToken, Task> delay, Func<bool> matchLive, Func<CancellationToken, Task> matchEnded, Func<bool> enabled)
    {
        _delay = delay;
        _matchLive = matchLive;
        _matchEnded = matchEnded;
        _enabled = enabled;
    }

    /// <summary>
    /// Runs <paramref name="check"/> on the schedule until <paramref name="ct"/> is cancelled. Its argument says whether
    /// the check was put off by a match (for the log).
    /// </summary>
    public async Task RunAsync(Func<bool, CancellationToken, Task> check, CancellationToken ct)
    {
        await _delay(FirstCheck, ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            if (_enabled())
            {
                bool putOff = false;
                while (_matchLive())
                {
                    putOff = true;
                    await _matchEnded(ct).ConfigureAwait(false);
                    await _delay(AfterMatch, ct).ConfigureAwait(false); // the post-match report's calls go first
                }
                if (_enabled()) await check(putOff, ct).ConfigureAwait(false);
            }
            await _delay(Every, ct).ConfigureAwait(false);
        }
    }
}

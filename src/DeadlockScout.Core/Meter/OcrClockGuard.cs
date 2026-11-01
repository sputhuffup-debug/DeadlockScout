namespace DeadlockScout.Core.Meter;

/// <summary>
/// Checks the match clock read off the HUD before its reading is used. OCR misreads the clock now and then ("22:52" for
/// 12:52, "2:25" for 12:25), and a reading's clock is its game time everywhere downstream: one read ahead made the soul
/// guard think its two-minute hold had run out and let 29 misread soul counts through as yours (Oct 31, 2026), and one
/// read behind lands a reading minutes back in the meter. The HUD clock only ever moves forward with real time (or
/// stands still while the game is paused), so a reading must follow the last good one: no more than
/// <see cref="Behind"/> earlier, and no further ahead than the real time since plus <see cref="Ahead"/>. Anything else
/// is a misread and is dropped, souls and all.
/// <para>
/// A first good clock needs <see cref="Confirm"/> readings in a row that follow each other. When nothing has been
/// accepted for <see cref="Resync"/>, the same rule takes a new clock, so a reconnect or anything else that really moves
/// the clock is followed, while a single wrong reading never is. Pure, so every rule is tested.
/// </para>
/// </summary>
public sealed class OcrClockGuard
{
    public static readonly TimeSpan Behind = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Ahead = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan Resync = TimeSpan.FromSeconds(30);
    public const int Confirm = 3;

    private TimeSpan? _clock;
    private DateTimeOffset _at;
    private readonly List<(TimeSpan Clock, DateTimeOffset At)> _run = new();

    /// <summary>Readings refused as misreads since the last <see cref="Reset"/> (not those read while confirming a first clock).</summary>
    public int Rejected { get; private set; }

    /// <summary>The last accepted clock and when it was read, if any.</summary>
    public (TimeSpan Clock, DateTimeOffset At)? Last => _clock is { } c ? (c, _at) : null;

    /// <summary>What the clock should read at <paramref name="now"/> if the game is running: the last good one plus the real time since.</summary>
    public TimeSpan? Expected(DateTimeOffset now) => _clock is { } c ? c + (now - _at) : null;

    /// <summary>True when <paramref name="clock"/>, read at <paramref name="now"/>, is believable; it then becomes the last good clock.</summary>
    public bool Accept(TimeSpan clock, DateTimeOffset now)
    {
        if (_clock is { } last && Follows(last, _at, clock, now))
        {
            Take(clock, now);
            return true;
        }
        // No good clock yet, or this one does not follow it: it may start a new run of readings that follow each other.
        if (_run.Count > 0 && !Follows(_run[^1].Clock, _run[^1].At, clock, now)) _run.Clear();
        _run.Add((clock, now));
        if (_run.Count >= Confirm && (_clock is null || now - _at >= Resync))
        {
            Take(clock, now);
            return true;
        }
        if (_clock is not null) Rejected++;
        return false;
    }

    /// <summary>Starts over for a new match, whose clock begins again at 0:00.</summary>
    public void Reset()
    {
        _clock = null;
        _run.Clear();
        Rejected = 0;
    }

    private void Take(TimeSpan clock, DateTimeOffset now)
    {
        _clock = clock;
        _at = now;
        _run.Clear();
    }

    private static bool Follows(TimeSpan last, DateTimeOffset lastAt, TimeSpan clock, DateTimeOffset now)
    {
        var elapsed = now - lastAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return clock >= last - Behind && clock <= last + elapsed + Ahead;
    }
}

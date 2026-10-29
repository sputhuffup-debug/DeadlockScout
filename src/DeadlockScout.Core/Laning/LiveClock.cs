namespace DeadlockScout.Core.Laning;

/// <summary>Where the live game clock comes from, best first.</summary>
public enum ClockSource
{
    /// <summary>OCR of the HUD's match clock: real time, exact.</summary>
    Screen,
    /// <summary>Wall time since console.log's "GameInProgress" line, taken as 0:00 (UNVERIFIED that the clock starts there).</summary>
    GameLog,
    /// <summary>The broadcast's game clock plus the assumed broadcast delay: the fallback.</summary>
    Broadcast,
}

/// <summary>
/// The game clock as it is in the game right now, which the timers need: the broadcast runs 60–90 s behind, too late for a
/// 30-second warning. Fed by OCR readings of the HUD clock, the console log's game-start line, and broadcast ticks; read
/// with the wall time. Thread-safe: the feed, OCR and the detector write from their own threads, the overlay reads.
/// </summary>
public sealed class LiveClock
{
    /// <summary>An OCR reading older than this is stale (OCR switched off or the HUD hidden); the next source takes over.</summary>
    public static readonly TimeSpan ScreenFresh = TimeSpan.FromSeconds(5);
    /// <summary>A game-log anchor implying a broadcast delay outside this range is wrong (a reconnect, a resumed log) and is dropped.</summary>
    public static readonly TimeSpan MaxPlausibleDelay = TimeSpan.FromMinutes(3);

    private readonly object _gate = new();
    private DateTimeOffset? _anchor;
    private (TimeSpan GameTime, DateTimeOffset At)? _screen, _feed;

    /// <summary>The wall time the game clock read 0:00, from the console log; the first one per match wins.</summary>
    public DateTimeOffset? Anchor { get { lock (_gate) return _anchor; } }

    public void SetAnchor(DateTimeOffset clockZeroAt) { lock (_gate) _anchor ??= clockZeroAt; }

    public void ObserveScreen(TimeSpan gameTime, DateTimeOffset at) { lock (_gate) _screen = (gameTime, at); }

    public void ObserveFeed(TimeSpan gameTime, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_feed is { } f && gameTime < f.GameTime) return;
            _feed = (gameTime, at);
            // A game-log anchor that puts the broadcast ahead of the game or minutes behind it is not the match's start.
            if (Delay(at) is { } d && (d < TimeSpan.Zero || d > MaxPlausibleDelay)) _anchor = null;
        }
    }

    /// <summary>How far the broadcast runs behind the game, by the game-log anchor; null without both.</summary>
    public TimeSpan? MeasuredDelay(DateTimeOffset now) { lock (_gate) return Delay(now); }

    private TimeSpan? Delay(DateTimeOffset now) => _anchor is { } a && _feed is { } f ? (now - a) - (f.GameTime + (now - f.At)) : null;

    /// <summary>The game clock now, and where it came from; null before any source has spoken.</summary>
    public (TimeSpan GameTime, ClockSource Source)? Read(DateTimeOffset now, TimeSpan assumedDelay)
    {
        lock (_gate)
        {
            if (_screen is { } s && now - s.At <= ScreenFresh) return (s.GameTime + (now - s.At), ClockSource.Screen);
            if (_anchor is { } a && now >= a) return (now - a, ClockSource.GameLog);
            if (_feed is { } f) return (f.GameTime + (now - f.At) + assumedDelay, ClockSource.Broadcast);
            return null;
        }
    }
}

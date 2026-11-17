namespace DeadlockScout.Core.Laning;

/// <summary>Where the live game clock comes from, best first.</summary>
public enum ClockSource
{
    /// <summary>OCR of the HUD's match clock: real time, exact.</summary>
    Screen,
    /// <summary>
    /// Wall time since console.log's "GameInProgress" line, which is 0:00 (verified on real matches), less the pauses the
    /// broadcast showed (it sees a pause one broadcast delay late).
    /// </summary>
    GameLog,
    /// <summary>The broadcast's game clock plus the assumed broadcast delay: the fallback.</summary>
    Broadcast,
}

/// <summary>
/// The game clock as it is in the game right now, which the timers need: the broadcast runs 30–60 s behind, too late for a
/// 30-second warning. Fed by OCR readings of the HUD clock, the console log's game-start line, and broadcast ticks; read
/// with the wall time. Thread-safe: the feed, OCR and the detector write from their own threads, the overlay reads.
/// A paused game stops the clock: a HUD clock or broadcast clock that has not moved for <see cref="PauseAfter"/> is a pause,
/// and the clock holds there instead of running on with the wall time (the timers would count down through it).
/// </summary>
public sealed class LiveClock
{
    /// <summary>An OCR reading older than this is stale (OCR switched off or the HUD hidden); the next source takes over.</summary>
    public static readonly TimeSpan ScreenFresh = TimeSpan.FromSeconds(5);
    /// <summary>A game-log anchor implying a broadcast delay outside this range is wrong (a reconnect, a resumed log) and is dropped.</summary>
    public static readonly TimeSpan MaxPlausibleDelay = TimeSpan.FromMinutes(3);

    /// <summary>
    /// A clock reading unchanged for this long is a paused game. The HUD and the broadcast both move every second while it
    /// runs, so two seconds without a change is no misread.
    /// </summary>
    public static readonly TimeSpan PauseAfter = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private DateTimeOffset? _anchor;
    private (TimeSpan GameTime, DateTimeOffset At)? _screen, _feed;
    // When each source's current reading was first seen (a reading repeated since then is a frozen clock).
    private DateTimeOffset _screenSince, _feedSince;
    // Wall time the broadcast clock stood still in pauses that have ended, taken off the game-log clock.
    private TimeSpan _pausedBefore;

    /// <summary>True while the freshest clock source shows a paused game.</summary>
    public bool Paused(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_screen is { } s && now - s.At <= ScreenFresh) return s.At - _screenSince >= PauseAfter;
            return _feed is { } f && f.At - _feedSince >= PauseAfter;
        }
    }

    /// <summary>The wall time the game clock read 0:00, from the console log; the first one per match wins.</summary>
    public DateTimeOffset? Anchor { get { lock (_gate) return _anchor; } }

    public void SetAnchor(DateTimeOffset clockZeroAt) { lock (_gate) _anchor ??= clockZeroAt; }

    public void ObserveScreen(TimeSpan gameTime, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_screen is not { } prev || prev.GameTime != gameTime) _screenSince = at;
            _screen = (gameTime, at);
        }
    }

    public void ObserveFeed(TimeSpan gameTime, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_feed is { } f && gameTime < f.GameTime) return;
            if (_feed is not { } prev || prev.GameTime != gameTime)
            {
                // The broadcast clock moves again: the wall time it stood still was a pause, off the game-log clock.
                if (_feed is { } was && was.At - _feedSince >= PauseAfter) _pausedBefore += was.At - _feedSince;
                _feedSince = at;
            }
            _feed = (gameTime, at);
            // A game-log anchor that puts the broadcast ahead of the game or minutes behind it is not the match's start.
            if (Delay(at) is { } d && (d < TimeSpan.Zero || d > MaxPlausibleDelay)) _anchor = null;
        }
    }

    /// <summary>How far the broadcast runs behind the game, by the game-log anchor; null without both.</summary>
    public TimeSpan? MeasuredDelay(DateTimeOffset now) { lock (_gate) return Delay(now); }

    private TimeSpan? Delay(DateTimeOffset now) => _anchor is { } a && _feed is { } f ? (now - a - Paused(now, f)) - (f.GameTime + (now - f.At)) : null;

    /// <summary>All the wall time the broadcast clock has stood still, the pause still going on included.</summary>
    private TimeSpan Paused(DateTimeOffset now, (TimeSpan GameTime, DateTimeOffset At) f)
        => _pausedBefore + (f.At - _feedSince >= PauseAfter ? now - _feedSince : TimeSpan.Zero);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>The game clock now, and where it came from; null before any source has spoken.</summary>
    public (TimeSpan GameTime, ClockSource Source)? Read(DateTimeOffset now, TimeSpan assumedDelay)
    {
        lock (_gate)
        {
            if (_screen is { } s && now - s.At <= ScreenFresh)
                return (s.At - _screenSince >= PauseAfter ? s.GameTime : s.GameTime + (now - s.At), ClockSource.Screen);
            var feedPause = _feed is { } fp ? Paused(now, fp) : _pausedBefore;
            if (_anchor is { } a && now >= a) return (Max(TimeSpan.Zero, now - a - feedPause), ClockSource.GameLog);
            if (_feed is { } f)
                // Paused on the broadcast: the game stands at the broadcast's clock (it paused one delay ago and stays).
                return (f.At - _feedSince >= PauseAfter ? f.GameTime : f.GameTime + (now - f.At) + assumedDelay, ClockSource.Broadcast);
            return null;
        }
    }
}

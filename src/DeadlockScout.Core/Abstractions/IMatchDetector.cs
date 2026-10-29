using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Abstractions;

/// <summary>Fires when a match starts and ends. Implemented by combining the console log (primary) and Steam's content log (fallback).</summary>
public interface IMatchDetector
{
    event Action<MatchContext>? MatchStarted;
    event Action<long>? MatchEnded;
    /// <summary>Hero internal name (e.g. "hero_haze") from console.log.</summary>
    event Action<string>? HeroLocked;
    /// <summary>
    /// The wall time the match's game clock started (console.log's "GameInProgress" line), for the live clock the timers
    /// use. UNVERIFIED that the clock reads 0:00 at that line; the session checks it against the broadcast.
    /// </summary>
    event Action<DateTimeOffset>? ClockStarted;

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}

/// <summary>One raw signal from one source. The composite detector dedupes and orders them.</summary>
public abstract record MatchSignal(DateTimeOffset At, string Source)
{
    public sealed record LobbyCreated(DateTimeOffset At, string Source, long LobbyId, long MatchId) : MatchSignal(At, Source);
    public sealed record Started(DateTimeOffset At, string Source, long MatchId) : MatchSignal(At, Source);
    public sealed record Ended(DateTimeOffset At, string Source, long MatchId) : MatchSignal(At, Source);
    /// <summary>You started watching a live match's broadcast (Watch tab), not playing in it.</summary>
    public sealed record Spectating(DateTimeOffset At, string Source, long MatchId) : MatchSignal(At, Source);
    public sealed record HeroLocked(DateTimeOffset At, string Source, string HeroName) : MatchSignal(At, Source);
    /// <summary>The game state reached GameInProgress: the match clock starts (UNVERIFIED that it reads 0:00 then).</summary>
    public sealed record ClockStarted(DateTimeOffset At, string Source) : MatchSignal(At, Source);
}

/// <summary>A tailer or watcher that turns a log into signals. Adapters implement this; Core combines them.</summary>
public interface IMatchSignalSource
{
    string Name { get; }
    IAsyncEnumerable<MatchSignal> ReadAsync(CancellationToken ct);
}

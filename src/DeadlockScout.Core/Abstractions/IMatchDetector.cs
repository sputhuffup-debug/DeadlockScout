using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Abstractions;

/// <summary>Fires when a match starts and ends. Implemented by combining the console log (primary) and Steam's content log (fallback).</summary>
public interface IMatchDetector
{
    event Action<MatchContext>? MatchStarted;
    event Action<long>? MatchEnded;
    /// <summary>Hero internal name (e.g. "hero_haze") from console.log.</summary>
    event Action<string>? HeroLocked;

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
}

/// <summary>A tailer or watcher that turns a log into signals. Adapters implement this; Core combines them.</summary>
public interface IMatchSignalSource
{
    string Name { get; }
    IAsyncEnumerable<MatchSignal> ReadAsync(CancellationToken ct);
}

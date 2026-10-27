namespace DeadlockScout.Core.Abstractions;

/// <summary>Wall clock, injectable so caches and detectors are testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

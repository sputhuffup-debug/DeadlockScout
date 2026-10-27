namespace DeadlockScout.Core.Models;

/// <summary>Where a soul sample came from. The meter does not care; the overlay labels it.</summary>
public enum SampleSource
{
    LiveFeed,
    Ocr,
}

/// <summary>Your own soul count at a game time.</summary>
public sealed record SoulSample(TimeSpan GameTime, int Souls, SampleSource Source);

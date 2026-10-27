using DeadlockScout.Core.Abstractions;

namespace DeadlockScout.Tests;

public sealed class TestClock : IClock
{
    public TestClock(DateTimeOffset start) => UtcNow = start;
    public DateTimeOffset UtcNow { get; set; }
    public void Advance(TimeSpan by) => UtcNow += by;
}

public static class Fixtures
{
    public static string Path(params string[] parts)
        => System.IO.Path.Combine(new[] { AppContext.BaseDirectory, "Fixtures" }.Concat(parts).ToArray());

    public static string Read(params string[] parts) => File.ReadAllText(Path(parts));
}

using DeadlockScout.Core.Caching;

namespace DeadlockScout.Tests.Core;

public class PatchKeyTests
{
    [Fact]
    public void Rotates_every_seven_days_and_honours_a_fixed_key()
    {
        Assert.Equal(DatePatchKeyProvider.ForDate(new DateOnly(2026, 10, 26)), DatePatchKeyProvider.ForDate(new DateOnly(2026, 10, 27)));
        Assert.NotEqual(DatePatchKeyProvider.ForDate(new DateOnly(2026, 10, 20)), DatePatchKeyProvider.ForDate(new DateOnly(2026, 10, 27)));
        var clock = new TestClock(new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("5.9", new DatePatchKeyProvider(clock, "5.9").GetAsync(default).Result);
        Assert.StartsWith("w", new DatePatchKeyProvider(clock, "auto").GetAsync(default).Result);
    }
}

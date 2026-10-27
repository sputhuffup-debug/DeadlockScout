using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Tests.Core;

public class CompositeBenchmarkTests
{
    private sealed class Fake(int priority, string name, Func<Task<PaceCurve?>> get) : IPaceBenchmark
    {
        public int Priority => priority;
        public string Name => name;
        public int Calls;
        public Task<PaceCurve?> GetAsync(int heroId, RankBracket bracket, CancellationToken ct) { Calls++; return get(); }
    }

    private static PaceCurve Curve(string provider) => new(1, RankBracket.All, "w", new[] { new PacePoint(0, null, null, null, 0) }, 1, provider);

    [Fact]
    public async Task Returns_first_non_null_by_priority_and_skips_failures()
    {
        var slowAccurate = new Fake(2, "metadata", () => Task.FromResult<PaceCurve?>(Curve("metadata")));
        var broken = new Fake(1, "analytics", () => throw new HttpRequestException("boom"));
        var composite = new CompositeBenchmark(new IPaceBenchmark[] { slowAccurate, broken });
        var c = await composite.GetAsync(1, RankBracket.All, CancellationToken.None);
        Assert.Equal("metadata", c!.Provider);
        Assert.Equal(1, broken.Calls);
    }

    [Fact]
    public async Task Watch_yields_each_better_curve_in_order()
    {
        var a = new Fake(1, "analytics", () => Task.FromResult<PaceCurve?>(Curve("analytics")));
        var none = new Fake(2, "metadata", () => Task.FromResult<PaceCurve?>(null));
        var p = new Fake(3, "personal", () => Task.FromResult<PaceCurve?>(Curve("personal")));
        var composite = new CompositeBenchmark(new IPaceBenchmark[] { p, none, a });
        var got = new List<string>();
        await foreach (var c in composite.WatchAsync(1, RankBracket.All, CancellationToken.None)) got.Add(c.Provider);
        Assert.Equal(new[] { "analytics", "personal" }, got);
    }
}

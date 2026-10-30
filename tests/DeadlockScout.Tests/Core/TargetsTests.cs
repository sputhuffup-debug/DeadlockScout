using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.History;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Tests.Core;

/// <summary>Made-up games: a soul timeline rising steadily, a few deaths, phases against a gate.</summary>
public class TargetsTests
{
    private static IReadOnlyList<NetWorthPoint> Souls(int perMinute, int minutes)
        => Enumerable.Range(0, minutes + 1).Select(m => new NetWorthPoint(TimeSpan.FromMinutes(m), m * perMinute)).ToList();

    private static DeathRecord Death(double minute) => new(TimeSpan.FromMinutes(minute), TimeSpan.FromSeconds(15));

    private static PhaseResult Phase(GamePhase p, int gained, double expected)
        => new(p, TimeSpan.Zero, TimeSpan.FromMinutes(10), gained, expected);

    private static readonly TargetSettings All = new() { LaningSpm = 500, MaxLaningDeaths = 1, FixPhase = GamePhase.Mid };

    [Fact]
    public void A_game_is_checked_against_each_target_that_is_set()
    {
        var checks = Targets.Check(All, Souls(520, 30), TimeSpan.FromMinutes(30), new[] { Death(4), Death(9.5), Death(22) },
            new[] { Phase(GamePhase.Laning, 5000, 4000), Phase(GamePhase.Mid, 7000, 7400) });
        Assert.Equal(new[] { TargetKind.LaningSpm, TargetKind.LaningDeaths, TargetKind.Phase }, checks.Select(c => c.Kind));
        Assert.Equal(new TargetCheck(TargetKind.LaningSpm, true, "520/min"), checks[0]);
        Assert.Equal(new TargetCheck(TargetKind.LaningDeaths, false, "2 deaths"), checks[1]); // 4:00 and 9:30; 22:00 is not laning
        Assert.False(checks[2].Hit);
        Assert.Equal("−5%", checks[2].Value);

        // Only what is set is checked.
        Assert.Single(Targets.Check(new TargetSettings { LaningSpm = 500 }, Souls(520, 30), TimeSpan.FromMinutes(30), null, Array.Empty<PhaseResult>()));
        Assert.Empty(Targets.Check(new TargetSettings(), Souls(520, 30), TimeSpan.FromMinutes(30), null, Array.Empty<PhaseResult>()));
    }

    [Fact]
    public void A_game_that_lacks_what_a_target_needs_is_unknown_not_missed()
    {
        // A remake that ended at 6:00, no death times, no rank gate.
        var checks = Targets.Check(All, Souls(520, 6), TimeSpan.FromMinutes(6), null, Array.Empty<PhaseResult>());
        Assert.All(checks, c => Assert.Null(c.Hit));
        // A full game without death times (cached before they were asked for): only that target is unknown.
        var noDeaths = Targets.Check(All, Souls(480, 30), TimeSpan.FromMinutes(30), null, new[] { Phase(GamePhase.Mid, 8000, 7000) });
        Assert.Equal(new bool?[] { false, null, true }, noDeaths.Select(c => c.Hit));
        // Exactly at the target is a hit; no deaths at all is a hit.
        Assert.True(Targets.Check(All with { MaxLaningDeaths = 0 }, Souls(500, 30), TimeSpan.FromMinutes(30), Array.Empty<DeathRecord>(),
            Array.Empty<PhaseResult>()).Take(2).All(c => c.Hit == true));
    }

    [Fact]
    public void The_hit_rate_counts_only_games_that_could_be_checked()
    {
        TargetCheck C(TargetKind k, bool? hit) => new(k, hit, "");
        var games = new IReadOnlyList<TargetCheck>[]
        {
            new[] { C(TargetKind.LaningSpm, true), C(TargetKind.Phase, null) },
            new[] { C(TargetKind.LaningSpm, false), C(TargetKind.Phase, true) },
            new[] { C(TargetKind.LaningSpm, true), C(TargetKind.Phase, false) },
        };
        var rates = Targets.Rates(games);
        Assert.Equal(new[] { TargetKind.LaningSpm, TargetKind.Phase }, rates.Select(r => r.Kind));
        Assert.Equal((2, 1), (rates[0].Hits, rates[0].Misses));
        Assert.Equal(2 / 3.0, rates[0].Rate!.Value, 6);
        Assert.Equal(new bool?[] { null, true, false }, rates[1].Results); // newest first, the unknown kept as a gap
        Assert.Equal(0.5, rates[1].Rate!.Value, 6);
    }

    [Fact]
    public void Targets_read_and_write_as_settings()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new ScoutSettings { Targets = All },
            new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) } });
        Assert.Contains("\"fixPhase\":\"mid\"", json.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new ScoutSettings().WithTargets(All).Targets, All);
        Assert.False(new TargetSettings().Any);
    }
}

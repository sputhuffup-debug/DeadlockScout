using System.Text.Json;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Patch;

namespace DeadlockScout.Tests.Core;

/// <summary>Made-up heroes and items; the ids are arbitrary.</summary>
public class PatchDiffTests
{
    private static PatchSnapshot Snap(long build, IEnumerable<SnapshotHero> heroes, IEnumerable<SnapshotItem> items)
        => new(DateTimeOffset.UnixEpoch, build, "Oct 26 2026", heroes.ToList(), items.ToList());

    private static readonly SnapshotHero Haze = new(13, "Haze", true), Newbie = new(90, "Newbie", false);
    private static readonly SnapshotItem Leech = new(1, "Leech", "spirit", 6400), Boots = new(2, "Boots", "vitality", 800),
                                         Ability = new(3, "Some Ability", "ability", 0);

    [Fact]
    public void Nothing_changed_is_no_lines()
        => Assert.Empty(PatchDiff.Lines(Snap(6821, new[] { Haze }, new[] { Leech }), Snap(6821, new[] { Haze }, new[] { Leech })));

    [Fact]
    public void Each_kind_of_change_is_one_line()
    {
        var old = Snap(6819, new[] { Haze }, new[] { Leech, Boots, Ability });
        var now = Snap(6821, new[] { Haze with { Name = "Haze Prime" }, Newbie },
            new[] { Leech with { Cost = 6800, Slot = "weapon" }, new SnapshotItem(4, "Shiny", "weapon", 3200) });
        var lines = PatchDiff.Lines(old, now);
        Assert.Equal(new[]
        {
            "Game build 6819 → 6821 (Oct 26 2026)",
            "+ hero Newbie (90), not playable yet",
            "~ hero Haze renamed Haze Prime (13)",
            "+ item Shiny (3,200, weapon)",
            "- item Boots (800, vitality)",       // an ability dropping out (cost 0) is not a shop change
            "~ item Leech: cost 6,400 → 6,800, slot spirit → weapon",
        }, lines);
    }

    [Fact]
    public void A_snapshot_round_trips_through_json_and_takes_assets_as_the_app_reads_them()
    {
        var snap = PatchSnapshot.From(DateTimeOffset.UnixEpoch, new SteamInfo(1, 6821, "Oct 26 2026"),
            new[] { new HeroAsset(13, "hero_haze", "Haze", null, true) },
            new[] { new ItemAsset(1, "upgrade_leech", "Leech", "spirit", 4, 6400, null) });
        var back = JsonSerializer.Deserialize<PatchSnapshot>(JsonSerializer.Serialize(snap))!;
        Assert.Empty(PatchDiff.Lines(snap, back));
        Assert.Equal(6821, back.ServerVersion);
        Assert.Equal(Leech, back.Items.Single());
    }
}

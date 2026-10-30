using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Patch;

/// <summary>What the patch check saw last time: the game build and the hero and item assets the app names things by.</summary>
public sealed record PatchSnapshot(DateTimeOffset TakenAt, long? ServerVersion, string? VersionDate,
                                   IReadOnlyList<SnapshotHero> Heroes, IReadOnlyList<SnapshotItem> Items)
{
    public static PatchSnapshot From(DateTimeOffset at, SteamInfo? steam, IEnumerable<HeroAsset> heroes, IEnumerable<ItemAsset> items)
        => new(at, steam?.ServerVersion, steam?.VersionDate,
            heroes.Select(h => new SnapshotHero(h.Id, h.Name, h.Playable)).OrderBy(h => h.Id).ToList(),
            items.Select(i => new SnapshotItem(i.Id, i.Name, i.Slot, i.Cost)).OrderBy(i => i.Id).ToList());
}

public sealed record SnapshotHero(int Id, string Name, bool Playable);
public sealed record SnapshotItem(long Id, string Name, string Slot, int Cost);

/// <summary>
/// What changed between two snapshots, as lines a person reads on patch day: the build, heroes added, removed, renamed or
/// made playable, and items added, removed, renamed, re-priced or moved to another slot. The app keys everything by id,
/// so a rename only changes labels, while a new or re-priced item can move power spikes, counter items and the 3,200
/// "big item" lines. Pure, so it is unit tested.
/// </summary>
public static class PatchDiff
{
    public static IReadOnlyList<string> Lines(PatchSnapshot old, PatchSnapshot now)
    {
        var lines = new List<string>();
        if (old.ServerVersion != now.ServerVersion)
            lines.Add($"Game build {old.ServerVersion?.ToString() ?? "?"} → {now.ServerVersion?.ToString() ?? "?"}" +
                      (now.VersionDate is { Length: > 0 } d ? $" ({d})" : ""));

        var oldHeroes = old.Heroes.ToDictionary(h => h.Id);
        var newHeroes = now.Heroes.ToDictionary(h => h.Id);
        foreach (var h in now.Heroes.Where(h => !oldHeroes.ContainsKey(h.Id)))
            lines.Add($"+ hero {h.Name} ({h.Id}){(h.Playable ? "" : ", not playable yet")}");
        foreach (var h in old.Heroes.Where(h => !newHeroes.ContainsKey(h.Id)))
            lines.Add($"- hero {h.Name} ({h.Id})");
        foreach (var h in now.Heroes.Where(h => oldHeroes.ContainsKey(h.Id)))
        {
            var was = oldHeroes[h.Id];
            if (was.Name != h.Name) lines.Add($"~ hero {was.Name} renamed {h.Name} ({h.Id})");
            if (was.Playable != h.Playable) lines.Add($"~ hero {h.Name} {(h.Playable ? "now playable" : "no longer playable")}");
        }

        var oldItems = old.Items.ToDictionary(i => i.Id);
        var newItems = now.Items.ToDictionary(i => i.Id);
        foreach (var i in now.Items.Where(i => !oldItems.ContainsKey(i.Id) && i.Cost > 0))
            lines.Add($"+ item {i.Name} ({i.Cost:N0}, {i.Slot})");
        foreach (var i in old.Items.Where(i => !newItems.ContainsKey(i.Id) && i.Cost > 0))
            lines.Add($"- item {i.Name} ({i.Cost:N0}, {i.Slot})");
        foreach (var i in now.Items.Where(i => oldItems.ContainsKey(i.Id)))
        {
            var was = oldItems[i.Id];
            var changes = new List<string>();
            if (was.Name != i.Name) changes.Add($"renamed from {was.Name}");
            if (was.Cost != i.Cost) changes.Add($"cost {was.Cost:N0} → {i.Cost:N0}");
            if (was.Slot != i.Slot) changes.Add($"slot {was.Slot} → {i.Slot}");
            if (changes.Count > 0) lines.Add($"~ item {i.Name}: {string.Join(", ", changes)}");
        }
        return lines;
    }
}

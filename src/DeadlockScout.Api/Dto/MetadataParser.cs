using System.Text.Json;
using DeadlockScout.Core.History;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Api.Dto;

/// <summary>
/// Reads match metadata JSON into the trimmed <see cref="MatchMetadata"/>. Handles both shapes the API emits:
/// the single-match endpoint (protobuf flattened: <c>match_info.players[].stats[]</c> as an array of samples) and the
/// bulk endpoint (one object per match with <c>players[].stats</c> as an object of parallel arrays, one per field).
/// Never throws on a missing field; a match without players is skipped.
/// </summary>
public static class MetadataParser
{
    public static MatchMetadata? ParseSingle(JsonElement root)
    {
        var info = root.TryGetProperty("match_info", out var mi) ? mi : root;
        return ParseMatch(info);
    }

    public static IReadOnlyList<MatchMetadata> ParseBulk(JsonElement root)
    {
        var list = new List<MatchMetadata>();
        if (root.ValueKind != JsonValueKind.Array) return list;
        foreach (var m in root.EnumerateArray())
        {
            var parsed = ParseMatch(m);
            if (parsed is not null) list.Add(parsed);
        }
        return list;
    }

    public static MatchMetadata? ParseMatch(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object) return null;
        long matchId = Long(m, "match_id");
        if (matchId == 0) return null;
        var players = new List<MatchPlayer>();
        if (m.TryGetProperty("players", out var ps) && ps.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in ps.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                players.Add(new MatchPlayer(
                    (uint)Long(p, "account_id"),
                    Int(p, "hero_id"),
                    Int(p, "team") is var t && t is 0 or 1 ? t + 2 : Int(p, "team"), // proto 0/1 → in-game 2/3
                    Int(p, "kills"), Int(p, "deaths"), Int(p, "assists"), Int(p, "net_worth"),
                    NullableInt(p, "party"),
                    NullableInt(p, "assigned_lane"),
                    ParseSeries(p),
                    NullableInt(p, "last_hits"),
                    NullableInt(p, "denies"),
                    NullableInt(p, "player_level") ?? NullableInt(p, "level"),
                    ParseItems(p),
                    ParseSources(p)));
            }
        }
        int winning = Int(m, "winning_team");
        return new MatchMetadata(
            matchId,
            DateTimeOffset.FromUnixTimeSeconds(Long(m, "start_time")),
            TimeSpan.FromSeconds(Int(m, "duration_s")),
            winning is 0 or 1 ? winning + 2 : winning,
            Int(m, "average_badge_team0"),
            Int(m, "average_badge_team1"),
            players);
    }

    /// <summary>
    /// stats[] of {time_stamp_s, net_worth} (single match), stats {time_stamp_s: [], net_worth: []} (bulk with
    /// include_player_stats), or flat stats_time_stamp_s / stats_net_worth arrays (bulk with extra_player_columns).
    /// </summary>
    public static IReadOnlyList<NetWorthPoint> ParseSeries(JsonElement player)
    {
        var pts = new List<NetWorthPoint>();
        if (player.ValueKind != JsonValueKind.Object) return pts;
        if (player.TryGetProperty("stats_time_stamp_s", out var flatTimes) && flatTimes.ValueKind == JsonValueKind.Array
            && player.TryGetProperty("stats_net_worth", out var flatWorths) && flatWorths.ValueKind == JsonValueKind.Array)
        {
            var times = flatTimes.EnumerateArray().ToList();
            var worths = flatWorths.EnumerateArray().ToList();
            for (int i = 0; i < Math.Min(times.Count, worths.Count); i++)
                pts.Add(new NetWorthPoint(TimeSpan.FromSeconds(AsLong(times[i])), (int)AsLong(worths[i])));
            pts.Sort((a, b) => a.GameTime.CompareTo(b.GameTime));
            return HistoryMath.FromZero(pts);
        }
        if (!player.TryGetProperty("stats", out var stats)) return pts;
        if (stats.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in stats.EnumerateArray())
                pts.Add(new NetWorthPoint(TimeSpan.FromSeconds(Long(s, "time_stamp_s")), Int(s, "net_worth")));
        }
        else if (stats.ValueKind == JsonValueKind.Object
                 && stats.TryGetProperty("time_stamp_s", out var ts) && ts.ValueKind == JsonValueKind.Array
                 && stats.TryGetProperty("net_worth", out var nw) && nw.ValueKind == JsonValueKind.Array)
        {
            var times = ts.EnumerateArray().ToList();
            var worths = nw.EnumerateArray().ToList();
            for (int i = 0; i < Math.Min(times.Count, worths.Count); i++)
                pts.Add(new NetWorthPoint(TimeSpan.FromSeconds(AsLong(times[i])), (int)AsLong(worths[i])));
        }
        pts.Sort((a, b) => a.GameTime.CompareTo(b.GameTime));
        return HistoryMath.FromZero(pts);
    }

    /// <summary>The <c>stats.gold_*</c> columns <see cref="Columns"/> asks the bulk call for.</summary>
    public static readonly string[] GoldFields =
        { "gold_player", "gold_lane_creep", "gold_denied", "gold_neutral_creep", "gold_boss", "gold_treasure", "gold_death_loss" };

    /// <summary>The bulk call's extra player columns: the soul timeline and the souls by source.</summary>
    public static string Columns => "stats.time_stamp_s,stats.net_worth," + string.Join(',', GoldFields.Select(f => "stats." + f));

    /// <summary>
    /// The souls by source at the last <c>stats</c> sample, in any of the three shapes <see cref="ParseSeries"/> reads;
    /// null when the player has none of the gold columns (an older cached answer, or a call without them).
    /// </summary>
    public static SoulSources? ParseSources(JsonElement player)
    {
        if (player.ValueKind != JsonValueKind.Object) return null;
        var values = new int?[GoldFields.Length];
        bool any = false;
        for (int i = 0; i < GoldFields.Length; i++)
        {
            values[i] = LastOf(player, GoldFields[i]);
            any |= values[i] is not null;
        }
        if (!any) return null;
        int V(int i) => values[i] ?? 0;
        return new SoulSources(V(0), V(1), V(2), V(3), V(4), V(5), V(6));
    }

    /// <summary>A stat's value at the last sample: flat stats_x arrays, a stats object of arrays, or a stats array of objects.</summary>
    private static int? LastOf(JsonElement player, string field)
    {
        if (player.TryGetProperty("stats_" + field, out var flat) && flat.ValueKind == JsonValueKind.Array)
            return flat.GetArrayLength() == 0 ? null : (int)AsLong(flat[flat.GetArrayLength() - 1]);
        if (!player.TryGetProperty("stats", out var stats)) return null;
        if (stats.ValueKind == JsonValueKind.Object && stats.TryGetProperty(field, out var arr) && arr.ValueKind == JsonValueKind.Array)
            return arr.GetArrayLength() == 0 ? null : (int)AsLong(arr[arr.GetArrayLength() - 1]);
        if (stats.ValueKind == JsonValueKind.Array && stats.GetArrayLength() > 0)
        {
            // The samples come in time order; take the latest anyway.
            var last = stats.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).MaxBy(x => Long(x, "time_stamp_s"));
            return last.ValueKind == JsonValueKind.Object && last.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? (int)AsLong(v) : null;
        }
        return null;
    }

    /// <summary>
    /// items[] of {item_id, game_time_s, sold_time_s} (the protobuf's own names, single match and, UNVERIFIED, the bulk
    /// call with include_player_items), or null when the player has no item list. Abilities share the list in the
    /// protobuf; the reader tells them apart by the assets, not here. sold_time_s 0 means never sold.
    /// </summary>
    public static IReadOnlyList<MatchItem>? ParseItems(JsonElement player)
    {
        if (player.ValueKind != JsonValueKind.Object || !player.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<MatchItem>();
        foreach (var i in items.EnumerateArray())
        {
            long id = Long(i, "item_id");
            if (id == 0) continue;
            long sold = Long(i, "sold_time_s");
            list.Add(new MatchItem(id, TimeSpan.FromSeconds(Long(i, "game_time_s")), sold > 0 ? TimeSpan.FromSeconds(sold) : null));
        }
        return list;
    }

    private static long Long(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? AsLong(v) : 0;
    private static int Int(JsonElement e, string name) => (int)Long(e, name);
    private static int? NullableInt(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)AsLong(v) : null;

    private static long AsLong(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : (long)v.GetDouble(),
        JsonValueKind.String => long.TryParse(v.GetString(), out var l) ? l : 0,
        JsonValueKind.True => 1,
        _ => 0,
    };
}

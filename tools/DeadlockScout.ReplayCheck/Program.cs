using System.Diagnostics;
using DeadlockScout.Api.Live;
using DemoFile;
using ICSharpCode.SharpZipLib.BZip2;

// Usage: dotnet run --project tools/DeadlockScout.ReplayCheck -- <replay.dem | replay.dem.bz2>
//
// Reads a finished replay with the same DemoFile parser and BroadcastSnapshotter the Direct live feed uses on a live
// broadcast, then prints the roster, each player's net worth per minute, and a check of the game clock. If this works on
// a replay from the current patch, the Direct feed can decode live matches on that patch too.

if (args is not [var path] || !File.Exists(path))
{
    Console.Error.WriteLine("Usage: DeadlockScout.ReplayCheck <replay.dem | replay.dem.bz2>");
    return 2;
}

Stream stream = File.OpenRead(path);
if (path.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase))
{
    var decompressed = new MemoryStream();
    using (var bz = new BZip2InputStream(stream)) bz.CopyTo(decompressed);
    decompressed.Position = 0;
    stream = decompressed;
}

var demo = new DeadlockDemoParser();
var snap = new BroadcastSnapshotter(demo);
string build = "?";
demo.PacketEvents.SvcServerInfo += e => build = $"{e.GameDir} (tick rate {1 / e.TickInterval:0})";

var series = new Dictionary<uint, SortedDictionary<int, int>>();   // account → minute → net worth
var last = new Dictionary<uint, LiveEventDto>();
var clockSamples = new List<(double MatchClock, double FromStart)>();
int snapshots = 0;
double? lastGameTime = null;
var sw = Stopwatch.StartNew();

try
{
    var reader = DemoFileReader.Create(demo, stream);
    await reader.StartReadingAsync(default);
    while (await reader.MoveNextAsync(default))
    {
        var events = snap.MaybeSnapshot();
        if (events.Count == 0) continue;
        snapshots++;
        foreach (var e in events)
        {
            if (e.IsTickEnd) { lastGameTime = e.GameTime; continue; }
            if (e.SteamId is not { } id) continue;
            last[id] = e;
            if (e.GameTime is { } gt && e.NetWorth is { } nw)
            {
                var minutes = series.TryGetValue(id, out var m) ? m : series[id] = new SortedDictionary<int, int>();
                minutes[(int)(gt / 60)] = nw;
            }
        }
        // Compare the HUD match clock with the game-start fallback every ~5 minutes of snapshots.
        if (snapshots % 300 == 1)
        {
            var c = snap.ReadClock(demo.CurrentGameTick.Value);
            if (c.MatchClockUpdateTick > 0 && c.GameStartTime > 0)
                clockSamples.Add((BroadcastClock.GameTimeSeconds(c)!.Value, c.CurrentTick * c.TickInterval - c.GameStartTime));
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"PARSE FAILED after {snapshots} snapshots: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine("The Direct feed would fall back to the hosted feed for a match on this build.");
    return 1;
}

Console.WriteLine($"Replay: {Path.GetFileName(path)}");
Console.WriteLine($"Server: {build}");
Console.WriteLine($"Parsed in {sw.Elapsed.TotalSeconds:0.0} s: {snapshots} snapshots, last game time {(lastGameTime is { } g ? TimeSpan.FromSeconds(g).ToString(@"mm\:ss") : "unknown")}");
Console.WriteLine();
Console.WriteLine($"{"Account",-11} {"Name",-20} {"Team",4} {"Hero",4} {"Lane",4} {"Net worth",9} {"K/D/A",8}");
foreach (var e in last.Values.OrderBy(e => e.Team).ThenBy(e => e.SteamName))
    Console.WriteLine($"{e.SteamId,-11} {Trim(e.SteamName, 20),-20} {e.Team,4} {e.HeroId,4} {e.AssignedLane,4} {e.NetWorth,9:N0} {$"{e.Kills}/{e.Deaths}/{e.Assists}",8}");

Console.WriteLine();
Console.WriteLine("Net worth by minute (every 5th minute):");
var maxMinute = series.Values.SelectMany(m => m.Keys).DefaultIfEmpty(0).Max();
var shown = Enumerable.Range(0, maxMinute / 5 + 1).Select(i => i * 5).ToList();
Console.WriteLine($"{"Name",-20} " + string.Join(" ", shown.Select(m => $"{m,6}")));
foreach (var (id, minutes) in series.OrderBy(kv => last[kv.Key].Team))
    Console.WriteLine($"{Trim(last[id].SteamName, 20),-20} " + string.Join(" ", shown.Select(m => minutes.TryGetValue(m, out var v) ? $"{v,6}" : $"{"",6}")));

Console.WriteLine();
if (clockSamples.Count == 0)
    Console.WriteLine("Clock: match clock not present in this replay; the Direct feed uses the game-start fallback.");
else
    foreach (var (matchClock, fromStart) in clockSamples)
        Console.WriteLine($"Clock: HUD match clock {TimeSpan.FromSeconds(matchClock):mm\\:ss} vs tick-from-start {TimeSpan.FromSeconds(fromStart):mm\\:ss} (difference {matchClock - fromStart:+0.0;-0.0} s)");
return 0;

static string Trim(string? s, int n) => s is null ? "" : s.Length <= n ? s : s[..(n - 1)] + "…";

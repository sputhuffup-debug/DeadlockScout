using System.Diagnostics;
using DeadlockScout.Api.Live;
using DemoFile;
using ICSharpCode.SharpZipLib.BZip2;

// Usage:
//   dotnet run --project tools/DeadlockScout.ReplayCheck -- <replay.dem | replay.dem.bz2>
//   dotnet run --project tools/DeadlockScout.ReplayCheck -- <broadcast URL> [seconds]
//
// Reads a finished replay, or a live broadcast for a while (default 120 s), with the same DemoFile parser and
// BroadcastSnapshotter the Direct live feed uses, then prints the roster, each player's net worth per minute, and a check
// of the game clock. If this works on the current patch, the Direct feed can decode live matches on that patch.

bool live = args.Length >= 1 && (args[0].StartsWith("http://", StringComparison.OrdinalIgnoreCase) || args[0].StartsWith("https://", StringComparison.OrdinalIgnoreCase));
if (args.Length == 0 || (!live && (args.Length != 1 || !File.Exists(args[0]))))
{
    Console.Error.WriteLine("Usage: DeadlockScout.ReplayCheck <replay.dem | replay.dem.bz2>");
    Console.Error.WriteLine("       DeadlockScout.ReplayCheck <broadcast URL> [seconds]");
    return 2;
}
var path = args[0];
var liveFor = TimeSpan.FromSeconds(live && args.Length > 1 && int.TryParse(args[1], out var secs) ? secs : 120);

Stream? stream = null;
if (!live)
{
    stream = File.OpenRead(path);
    if (path.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase))
    {
        var decompressed = new MemoryStream();
        using (var bz = new BZip2InputStream(stream)) bz.CopyTo(decompressed);
        decompressed.Position = 0;
        stream = decompressed;
    }
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
    Func<CancellationToken, ValueTask<bool>> moveNext;
    using var stop = new CancellationTokenSource(live ? liveFor : Timeout.InfiniteTimeSpan);
    if (live)
    {
        var http = new HttpClient(new RetryingBroadcastHttpHandler(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip }))
        {
            BaseAddress = new Uri(path.TrimEnd('/') + "/"),
        };
        var broadcast = HttpBroadcastReader.Create(demo, http);
        await broadcast.StartReadingAsync(stop.Token);
        Console.WriteLine($"Reading the live broadcast for {liveFor.TotalSeconds:0} s...");
        moveNext = async ct => { try { return await broadcast.MoveNextAsync(ct); } catch (OperationCanceledException) { return false; } };
    }
    else
    {
        var reader = DemoFileReader.Create(demo, stream!);
        await reader.StartReadingAsync(default);
        moveNext = ct => reader.MoveNextAsync(ct);
    }
    while (await moveNext(stop.Token))
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
    if (Environment.GetEnvironmentVariable("REPLAYCHECK_STACK") == "1") Console.WriteLine(ex);
    Console.WriteLine("The Direct feed would fall back to the hosted feed for a match on this build.");
    return 1;
}

Console.WriteLine(live ? $"Broadcast: {path}" : $"Replay: {Path.GetFileName(path)}");
Console.WriteLine($"Server: {build}");
Console.WriteLine($"Parsed in {sw.Elapsed.TotalSeconds:0.0} s: {snapshots} snapshots, last game time {(lastGameTime is { } g ? TimeSpan.FromSeconds(g).ToString(@"mm\:ss") : "unknown")}");
Console.WriteLine();
Console.WriteLine($"{"Account",-11} {"Name",-20} {"Team",4} {"Hero",4} {"Lane",4} {"Net worth",9} {"K/D/A",8}");
foreach (var e in last.Values.OrderBy(e => e.Team).ThenBy(e => e.SteamName))
    Console.WriteLine($"{e.SteamId,-11} {Trim(e.SteamName, 20),-20} {e.Team,4} {e.HeroId,4} {e.AssignedLane,4} {e.NetWorth,9:N0} {$"{e.Kills}/{e.Deaths}/{e.Assists}",8}");

Console.WriteLine();
var observed = series.Values.SelectMany(m => m.Keys).Distinct().OrderBy(m => m).ToList();
Console.WriteLine(observed.Count <= 12 ? "Net worth by minute:" : "Net worth by minute (every 5th minute):");
// a short live window shows every minute it saw; a whole replay shows every 5th minute
var shown = observed.Count <= 12 ? observed : observed.Where(m => m % 5 == 0).ToList();
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

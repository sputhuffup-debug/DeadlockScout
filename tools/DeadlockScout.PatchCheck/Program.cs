using System.Diagnostics;
using System.Text.Json;
using DeadlockScout.Api;
using DeadlockScout.Api.Http;
using DeadlockScout.Core.Caching;
using DeadlockScout.Core.Patch;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Options;

// Usage:
//   dotnet run --project tools/DeadlockScout.PatchCheck -- [--snapshot <file>] [--replay <replay.dem[.bz2] | broadcast URL>] [--no-save]
//
// The patch-day checklist in one run. Prints what changed since the last run (the game build, heroes and items, from
// the assets endpoints, and the builds live matches run), the benchmarks' patch key, and the laning timers' spawn defaults; with --replay it runs the
// replay or a live broadcast through ReplayCheck, which shows whether the Direct feed still decodes this build and when
// the jungle spawned, to set against the defaults. Saves what it saw for next time (in local app data unless
// --snapshot names a file). Exits 1 when something changed, 0 when nothing did, 2 on a usage or network error.

string? snapshotPath = null, replay = null;
bool save = true;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--snapshot" when i + 1 < args.Length: snapshotPath = args[++i]; break;
        case "--replay" when i + 1 < args.Length: replay = args[++i]; break;
        case "--no-save": save = false; break;
        default:
            Console.Error.WriteLine("Usage: DeadlockScout.PatchCheck [--snapshot <file>] [--replay <replay.dem[.bz2] | broadcast URL>] [--no-save]");
            return 2;
    }
}
snapshotPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeadlockScout", "patch-snapshot.json");

// The app's own handlers: the API key (DEADLOCK_API_KEY, if set), retries, and the per-endpoint rate limits.
var settings = new ScoutSettings { Api = new ApiSettings { ApiKey = Environment.GetEnvironmentVariable("DEADLOCK_API_KEY") } };
var pipeline = new ApiKeyHandler(() => settings.Api.ApiKey)
{
    InnerHandler = new RetryHandler { InnerHandler = new RateLimitedHandler(settings.Api.Limits) { InnerHandler = new HttpClientHandler() } },
};
var api = new DeadlockApiClient(new HttpClient(pipeline) { Timeout = TimeSpan.FromSeconds(60) }, new Fixed(settings));

var now = DateTimeOffset.UtcNow;
Console.WriteLine($"DeadlockScout patch check, {now:yyyy-MM-dd HH:mm} UTC");
Console.WriteLine();

PatchSnapshot current;
IReadOnlyDictionary<int, int> liveBuilds = new Dictionary<int, int>();
try
{
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var steam = await api.SteamInfoAsync(cts.Token);
    var heroes = await api.HeroesAsync(cts.Token);
    var items = await api.ItemsAsync(cts.Token);
    current = PatchSnapshot.From(now, steam, heroes, items);
    try { liveBuilds = await api.ActiveBuildsAsync(cts.Token); }
    catch (Exception ex) { Console.WriteLine($"(live matches unavailable: {ex.Message})"); }
}
catch (Exception ex)
{
    Console.WriteLine($"Could not read the assets: {ex.Message}");
    return 2;
}

var json = new JsonSerializerOptions { WriteIndented = true };
PatchSnapshot? previous = null;
if (File.Exists(snapshotPath))
{
    try { previous = JsonSerializer.Deserialize<PatchSnapshot>(File.ReadAllText(snapshotPath), json); }
    catch (Exception ex) { Console.WriteLine($"The last snapshot could not be read ({ex.Message}); starting a new one."); }
}

Console.WriteLine($"1. Game and assets (build {current.ServerVersion?.ToString() ?? "?"}{(current.VersionDate is { Length: > 0 } vd ? $", {vd}" : "")}, " +
                  $"{current.Heroes.Count(h => h.Playable)} playable heroes, {current.Items.Count(i => i.Cost > 0)} shop items)");
if (liveBuilds.Count > 0)
{
    Console.WriteLine("   Live matches by build: " + string.Join(", ", liveBuilds.OrderByDescending(b => b.Key).Select(b => $"{b.Key} ({b.Value})")));
    // steam-info follows Steam with a lag; the live matches show a patch within minutes of it going out.
    if (liveBuilds.Keys.Max() > (current.ServerVersion ?? 0))
        Console.WriteLine($"   A newer build ({liveBuilds.Keys.Max()}) is being played than steam-info reports: a patch just went out. Run step 4 on a match of it.");
}
var changes = previous is null ? Array.Empty<string>() : PatchDiff.Lines(previous, current);
if (previous is null) Console.WriteLine("   No earlier snapshot: this run is the baseline for the next one.");
else if (changes.Count == 0) Console.WriteLine($"   Nothing changed since {previous.TakenAt:yyyy-MM-dd HH:mm} UTC.");
else
{
    Console.WriteLine($"   Since {previous.TakenAt:yyyy-MM-dd HH:mm} UTC:");
    foreach (var line in changes) Console.WriteLine("   " + line);
    Console.WriteLine("   A new or re-priced item can change the power-spike alerts, counter items and the 3,200-soul \"big item\" lines;");
    Console.WriteLine("   the app reloads the assets within a day (or on the first unknown id).");
}
Console.WriteLine();

var key = new DatePatchKeyProvider(new SystemClockShim(), settings.Cache.PatchKey);
Console.WriteLine($"2. Benchmarks: patch key {await key.GetAsync(default)} (a new key every week; cache.patchKey in settings pins one).");
Console.WriteLine("   After a game patch, curves and counter items cached under this week's key still reflect the old patch until");
Console.WriteLine("   the week turns. To rebuild now, set cache.patchKey to a new value (e.g. the build number) and restart the app.");
Console.WriteLine();

Console.WriteLine("3. Laning timers: the spawn defaults the app counts down to (timers.spawns; first spawns verified, note 38):");
foreach (var s in new TimerSettings().Spawns) Console.WriteLine($"   {TimeSpan.FromSeconds(s.FirstSpawnSeconds):m\\:ss}  {s.Name}  ({s.Note})");
var urn = new ObjectiveSettings();
Console.WriteLine($"   Urn: {TimeSpan.FromSeconds(urn.UrnFirstSpawnSeconds):m\\:ss}, then every {TimeSpan.FromSeconds(urn.UrnIntervalSeconds):m\\:ss} (verified, note 23).");
Console.WriteLine();

int replayExit = 0;
if (replay is null)
{
    Console.WriteLine("4. Replay: not run. Pass --replay <replay.dem.bz2> (Deadlock keeps recent ones in game/citadel/replays) or a live");
    Console.WriteLine("   broadcast URL to check the Direct feed still decodes this build and see when the jungle spawned.");
}
else
{
    Console.WriteLine($"4. Replay: running ReplayCheck on {replay}");
    Console.WriteLine("   Compare its \"Jungle in the first 15 minutes\" jumps with the defaults in 3; a PARSE FAILED line means the");
    Console.WriteLine("   Direct feed cannot read this build and the app will fall back to the hosted feed (see VENDORED.md).");
    Console.WriteLine();
    replayExit = RunReplayCheck(replay);
}
Console.WriteLine();

if (save)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(snapshotPath))!);
    File.WriteAllText(snapshotPath, JsonSerializer.Serialize(current, json));
    Console.WriteLine($"Snapshot saved to {snapshotPath}");
}
return changes.Count > 0 || replayExit != 0 ? 1 : 0;

static int RunReplayCheck(string target)
{
    // The tools folder sits next to this one in the repository; dotnet run builds ReplayCheck if it needs to.
    var repo = FindRepoRoot(AppContext.BaseDirectory) ?? FindRepoRoot(Directory.GetCurrentDirectory());
    if (repo is null) { Console.WriteLine("   Could not find the repository to run ReplayCheck from."); return 2; }
    var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = repo, UseShellExecute = false };
    foreach (var a in new[] { "run", "--project", Path.Combine("tools", "DeadlockScout.ReplayCheck"), "--", target }) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    p.WaitForExit();
    return p.ExitCode;
}

static string? FindRepoRoot(string from)
{
    for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "DeadlockScout.sln"))) return dir.FullName;
    return null;
}

sealed class Fixed(ScoutSettings value) : IOptionsMonitor<ScoutSettings>
{
    public ScoutSettings CurrentValue => value;
    public ScoutSettings Get(string? name) => value;
    public IDisposable? OnChange(Action<ScoutSettings, string?> listener) => null;
}

sealed class SystemClockShim : DeadlockScout.Core.Abstractions.IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

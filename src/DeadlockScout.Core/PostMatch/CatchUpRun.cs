namespace DeadlockScout.Core.PostMatch;

/// <summary>
/// What the start-up catch-up found (0.6.2): the matches the game's console.log showed you finished before the app
/// started (the last few, see PostMatchCoordinator.MaxCatchUp), how many deadlock-api already had, how many it was asked
/// to fetch from Steam, and how many it has fetched so far. For Diagnostics and Copy check results (check 6).
/// </summary>
public sealed record CatchUpRun(DateTimeOffset At, bool LogFound, int Finished, int AlreadyIn, int Asked, int Fetched)
{
    /// <summary>The Diagnostics line.</summary>
    public static string Summary(CatchUpRun? run)
    {
        if (run is null) return "not run yet (it runs a minute after start)";
        var at = $"at {run.At.ToLocalTime():HH:mm}";
        if (!run.LogFound) return $"{at}: console.log not found";
        if (run.Finished == 0) return $"{at}: no finished match in console.log";
        var line = $"{at}: {Matches(run.Finished)} finished before the app started, {run.AlreadyIn} already in deadlock-api";
        return run.Asked == 0 ? line : line + $", {run.Asked} asked for from Steam, {run.Fetched} fetched";
    }

    private static string Matches(int n) => n == 1 ? "1 match" : $"{n} matches";
}

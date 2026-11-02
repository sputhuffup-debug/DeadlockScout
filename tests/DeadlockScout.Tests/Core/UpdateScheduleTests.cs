using DeadlockScout.Core.Updates;

namespace DeadlockScout.Tests.Core;

public class UpdateScheduleTests
{
    /// <summary>A pretend PC: delays pass at once and are written down, and a match can be live for some of them.</summary>
    private sealed class Run
    {
        public readonly List<string> Log = new();
        public bool Live;
        public bool Enabled = true;
        public int EveryRounds = 2; // stop at the second six-hour wait
        /// <summary>Called at each delay with its length, to start or end a match at that point.</summary>
        public Action<TimeSpan>? OnDelay;
        private readonly CancellationTokenSource _cts = new();

        public async Task<List<string>> GoAsync()
        {
            var schedule = new UpdateSchedule(
                (d, ct) =>
                {
                    Log.Add(d == UpdateSchedule.FirstCheck ? "first" : d == UpdateSchedule.AfterMatch ? "after-match" : d == UpdateSchedule.Every ? "every" : d.ToString());
                    OnDelay?.Invoke(d);
                    if (d == UpdateSchedule.Every && --EveryRounds == 0) _cts.Cancel();
                    return Task.CompletedTask;
                },
                () => Live,
                ct => { Log.Add("match-ended"); Live = false; return Task.CompletedTask; },
                () => Enabled);
            await schedule.RunAsync((putOff, ct) => { Log.Add(putOff ? "check (put off)" : "check"); return Task.CompletedTask; }, _cts.Token);
            return Log;
        }
    }

    [Fact]
    public async Task Checks_after_start_and_then_every_six_hours()
        => Assert.Equal(new[] { "first", "check", "every", "check", "every" }, await new Run().GoAsync());

    [Fact]
    public async Task A_check_that_falls_in_a_match_runs_a_few_minutes_after_it()
    {
        var run = new Run { Live = true };
        Assert.Equal(new[] { "first", "match-ended", "after-match", "check (put off)", "every", "check", "every" }, await run.GoAsync());
    }

    [Fact]
    public async Task A_new_match_before_the_put_off_check_waits_for_that_one_too()
    {
        var run = new Run { Live = true };
        int afterMatch = 0;
        run.OnDelay = d => { if (d == UpdateSchedule.AfterMatch && ++afterMatch == 1) run.Live = true; }; // queued straight into the next
        Assert.Equal(new[] { "first", "match-ended", "after-match", "match-ended", "after-match", "check (put off)", "every", "check", "every" },
            await run.GoAsync());
    }

    [Fact]
    public async Task Switched_off_means_no_checks()
        => Assert.Equal(new[] { "first", "every", "every" }, await new Run { Enabled = false }.GoAsync());

    [Fact]
    public async Task Switched_off_during_the_match_cancels_the_put_off_check()
    {
        var run = new Run { Live = true };
        run.OnDelay = d => { if (d == UpdateSchedule.AfterMatch) run.Enabled = false; };
        Assert.Equal(new[] { "first", "match-ended", "after-match", "every", "every" }, await run.GoAsync());
    }

    [Fact]
    public async Task Stops_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var schedule = new UpdateSchedule(Task.Delay, () => false, _ => Task.CompletedTask, () => true);
        var task = schedule.RunAsync((_, _) => Task.CompletedTask, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}

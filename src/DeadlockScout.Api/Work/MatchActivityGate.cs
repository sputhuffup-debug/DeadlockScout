using DeadlockScout.Api.Http;

namespace DeadlockScout.Api.Work;

/// <summary>
/// Foreground requests always pass. Background requests wait while a match is live, so benchmark sampling and
/// auto-ingest never compete with the lobby's hero-stats call.
/// </summary>
public sealed class MatchActivityGate
{
    private readonly object _lock = new();
    private TaskCompletionSource _idle = Completed();

    public bool IsMatchLive { get; private set; }

    public void MatchStarted()
    {
        lock (_lock)
        {
            if (IsMatchLive) return;
            IsMatchLive = true;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void MatchEnded()
    {
        lock (_lock)
        {
            IsMatchLive = false;
            _idle.TrySetResult();
        }
    }

    public Task WaitAsync(RequestPriority priority, CancellationToken ct)
    {
        if (priority == RequestPriority.Foreground) return Task.CompletedTask;
        Task idle;
        lock (_lock) idle = _idle.Task;
        return idle.IsCompleted ? Task.CompletedTask : idle.WaitAsync(ct);
    }

    private static TaskCompletionSource Completed()
    {
        var tcs = new TaskCompletionSource();
        tcs.SetResult();
        return tcs;
    }
}

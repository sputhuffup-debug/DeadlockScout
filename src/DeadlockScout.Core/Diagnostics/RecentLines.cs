namespace DeadlockScout.Core.Diagnostics;

/// <summary>
/// The last few lines of one kind the app saw this run (Steam's content log lines for Deadlock, the respawns it
/// observed), kept in memory only, for Diagnostics' "Copy check results". Thread-safe; oldest dropped first.
/// </summary>
public sealed class RecentLines
{
    private readonly int _capacity;
    private readonly Queue<string> _lines = new();
    private readonly object _lock = new();

    public RecentLines(int capacity) => _capacity = Math.Max(1, capacity);

    public void Add(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_lock)
        {
            _lines.Enqueue(line.Trim());
            while (_lines.Count > _capacity) _lines.Dequeue();
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_lock) return _lines.ToList();
    }
}

using System.IO;
using System.Text.Json;
using DeadlockScout.Core.History;
using Microsoft.Extensions.Logging;

namespace DeadlockScout.App.Services;

/// <summary>
/// The moments marked with the mark hotkey (0.6), in <c>%LOCALAPPDATA%\DeadlockScout\marks.json</c>: match ids and game
/// times only, never sent anywhere. Saved on each mark (a few bytes); a file that cannot be read starts empty.
/// </summary>
public sealed class MarkStore
{
    private readonly string _path;
    private readonly ILogger<MarkStore>? _log;
    private readonly object _gate = new();
    private MomentMarks _marks;

    public MarkStore(string? path = null, ILogger<MarkStore>? log = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeadlockScout", "marks.json");
        _log = log;
        _marks = Load();
    }

    public IReadOnlyList<TimeSpan> For(long matchId) { lock (_gate) return _marks.For(matchId); }

    /// <summary>Adds a mark and saves; false when it was a double press, the match id is unknown or the match is full.</summary>
    public bool Add(long matchId, TimeSpan at)
    {
        lock (_gate)
        {
            var next = _marks.With(matchId, at);
            if (ReferenceEquals(next, _marks)) return false;
            _marks = next;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_marks));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log?.LogWarning(ex, "Marks could not be saved"); }
            return true;
        }
    }

    private MomentMarks Load()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<MomentMarks>(File.ReadAllText(_path)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Marks could not be read; starting empty");
            return new();
        }
    }
}
